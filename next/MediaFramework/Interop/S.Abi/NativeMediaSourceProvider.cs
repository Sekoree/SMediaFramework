using System.Text;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Abi;

/// <summary>Adapts a native media-source provider, including correlated video/audio opens.</summary>
public sealed unsafe class NativeMediaSourceProvider : IMediaDecoderProvider, IDisposable
{
    private const long PairWindowMilliseconds = 2_000;

    private readonly MfpMediaSourceProviderVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private readonly object _gate = new();
    private readonly Dictionary<(string Uri, int ThreadId), PendingAudio> _pendingAudio = [];
    private readonly CleanupTimerState _cleanupTimer;
    private bool _disposed;

    internal NativeMediaSourceProvider(string name, nint vtable, nint self, AbiPluginLease lease)
    {
        Name = name;
        _vt = (MfpMediaSourceProviderVTable*)vtable;
        _self = (void*)self;
        _lease = lease;
        _cleanupTimer = CleanupTimerState.Create(this);
    }

    public string Name { get; }

    public double Probe(string uri, MediaKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hasKind = kind switch
        {
            MediaKind.Video => _vt->VideoSourceVTable != null,
            MediaKind.Audio => _vt->AudioSourceVTable != null,
            _ => false,
        };
        return hasKind && CanOpen(uri) ? 1.0 : 0.0;
    }

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) =>
        TryOpenVideo(uri) ?? throw new InvalidOperationException($"plugin '{Name}' could not open video for '{uri}'.");

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        TryOpenAudio(uri) ?? throw new InvalidOperationException($"plugin '{Name}' could not open audio for '{uri}'.");

    public bool CanOpen(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->CanOpen == null)
            return false;
        var bytes = Utf8(uri);
        fixed (byte* p = bytes)
            return _vt->CanOpen(_self, p) != 0;
    }

    public IVideoSource? TryOpenVideo(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->Open == null || _vt->VideoSourceVTable == null)
            return null;

        var media = Open(uri);
        if (media.Video == null)
        {
            DestroyAudio(media.Audio);
            return null;
        }

        if (media.Audio != null)
            StorePendingAudio(uri, media.Audio);

        var lease = _lease.AcquireDependent();
        try
        {
            return NativeVideoSource.Create((nint)_vt->VideoSourceVTable, (nint)media.Video, lease);
        }
        catch
        {
            if (_vt->VideoSourceVTable->Destroy != null)
                _vt->VideoSourceVTable->Destroy(media.Video);
            lease.Dispose();
            throw;
        }
    }

    public IAudioSource? TryOpenAudio(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->Open == null || _vt->AudioSourceVTable == null)
            return null;

        var audio = TakePendingAudio(uri);
        if (audio == null)
        {
            var media = Open(uri);
            audio = media.Audio;
            DestroyVideo(media.Video);
        }
        if (audio == null)
            return null;

        var lease = _lease.AcquireDependent();
        try
        {
            return NativeAudioSource.Create(_vt->AudioSourceVTable, audio, lease);
        }
        catch
        {
            if (_vt->AudioSourceVTable->Destroy != null)
                _vt->AudioSourceVTable->Destroy(audio);
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        List<nint> pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _cleanupTimer.Dispose();
            pending = _pendingAudio.Values.Select(x => x.Source).ToList();
            _pendingAudio.Clear();
        }
        foreach (var source in pending)
            DestroyAudio((void*)source);
        _lease.Dispose();
        GC.SuppressFinalize(this);
    }

    ~NativeMediaSourceProvider() => Dispose();

    private MfpMediaSource Open(string uri)
    {
        var bytes = Utf8(uri);
        MfpMediaSource media = default;
        AbiPluginHost.ClearLastError();
        int status;
        fixed (byte* p = bytes)
            status = _vt->Open(_self, p, &media);
        if (status != (int)MfpStatus.Ok)
        {
            DestroyVideo(media.Video);
            DestroyAudio(media.Audio);
            throw AbiPluginHost.StatusException($"plugin '{Name}' open", status);
        }
        return media;
    }

    private void StorePendingAudio(string uri, void* source)
    {
        var key = (uri, Environment.CurrentManagedThreadId);
        lock (_gate)
        {
            if (_pendingAudio.Remove(key, out var prior))
                DestroyAudio((void*)prior.Source);
            _pendingAudio[key] = new PendingAudio(
                (nint)source, Environment.TickCount64 + PairWindowMilliseconds);
        }
    }

    private void* TakePendingAudio(string uri)
    {
        CleanupExpired();
        var key = (uri, Environment.CurrentManagedThreadId);
        lock (_gate)
            return _pendingAudio.Remove(key, out var pending) ? (void*)pending.Source : null;
    }

    private void CleanupExpired()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = Environment.TickCount64;
            foreach (var item in _pendingAudio.Where(x => x.Value.ExpiresAtMilliseconds < now).ToArray())
            {
                _pendingAudio.Remove(item.Key);
                DestroyAudio((void*)item.Value.Source);
            }
        }
    }

    private void DestroyVideo(void* source)
    {
        if (source != null && _vt->VideoSourceVTable != null && _vt->VideoSourceVTable->Destroy != null)
            _vt->VideoSourceVTable->Destroy(source);
    }

    private void DestroyAudio(void* source)
    {
        if (source != null && _vt->AudioSourceVTable != null && _vt->AudioSourceVTable->Destroy != null)
            _vt->AudioSourceVTable->Destroy(source);
    }

    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private sealed record PendingAudio(nint Source, long ExpiresAtMilliseconds);

    private sealed class CleanupTimerState : IDisposable
    {
        private readonly WeakReference<NativeMediaSourceProvider> _owner;
        private Timer? _timer;

        private CleanupTimerState(NativeMediaSourceProvider owner) =>
            _owner = new WeakReference<NativeMediaSourceProvider>(owner);

        public static CleanupTimerState Create(NativeMediaSourceProvider owner)
        {
            var state = new CleanupTimerState(owner);
            state._timer = new Timer(static value => ((CleanupTimerState)value!).Tick(), state,
                PairWindowMilliseconds, PairWindowMilliseconds);
            return state;
        }

        public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Dispose();

        private void Tick()
        {
            if (_owner.TryGetTarget(out var owner))
                owner.CleanupExpired();
            else
                Dispose();
        }
    }
}

internal sealed unsafe class NativeAudioSource : IAudioSource, IDisposable
{
    private readonly MfpAudioSourceVTable* _vt;
    private readonly void* _source;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    private NativeAudioSource(
        MfpAudioSourceVTable* vt, void* source, AudioFormat format, AbiPluginLease lease)
    {
        _vt = vt;
        _source = source;
        _lease = lease;
        Format = format;
    }

    internal static NativeAudioSource Create(
        MfpAudioSourceVTable* vt, void* source, AbiPluginLease lease)
    {
        if (vt->NativeFormat == null)
            throw new InvalidOperationException("plugin audio source has no native_format callback.");
        MfpAudioFormat native = default;
        AbiPluginHost.ClearLastError();
        var status = vt->NativeFormat(source, &native);
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("plugin audio source format query", status);
        if (native.SampleRate == 0 || native.Channels == 0 || native.SampleFormat != 0)
            throw new InvalidOperationException(
                $"plugin audio source returned unsupported format {native.SampleRate} Hz, {native.Channels} channels, sample format {native.SampleFormat}.");
        return new NativeAudioSource(
            vt, source, new AudioFormat((int)native.SampleRate, (int)native.Channels), lease);
    }

    public AudioFormat Format { get; }
    public bool IsExhausted =>
        _disposed || (_vt->IsExhausted != null && _vt->IsExhausted(_source) != 0);

    public int ReadInto(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->ReadInto == null || destination.IsEmpty)
            return 0;
        fixed (float* samples = destination)
        {
            AbiPluginHost.ClearLastError();
            var result = _vt->ReadInto(_source, samples, destination.Length);
            if (result is (int)MfpStatus.ErrAgain or (int)MfpStatus.ErrEnd)
                return 0;
            if (result < 0)
                throw AbiPluginHost.StatusException("plugin audio source read", result);
            if (result > destination.Length)
                throw new InvalidOperationException(
                    $"plugin audio source returned {result} floats for a {destination.Length}-float buffer.");
            return result;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->Destroy != null)
            _vt->Destroy(_source);
        _lease.Dispose();
        GC.SuppressFinalize(this);
    }

    ~NativeAudioSource() => Dispose();
}
