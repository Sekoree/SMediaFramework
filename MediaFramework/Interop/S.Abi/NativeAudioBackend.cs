using System.Text;
using S.Media.Core.Audio;
using S.Media.Time;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpAudioBackendVTable</c> to the managed <see cref="IAudioBackend"/> - device
/// enumeration plus opening <see cref="NativeAudioOutput"/> / <see cref="NativeAudioInput"/>. Registered into a live
/// <see cref="S.Media.Core.Registry.IMediaRegistry"/> via <c>AddAudioBackend</c>, a plugin backend is a peer of
/// PortAudio/miniaudio.
/// </summary>
public sealed unsafe class NativeAudioBackend : IAudioBackend, IDisposable
{
    private readonly MfpAudioBackendVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    internal NativeAudioBackend(string name, nint vtable, nint self, AbiPluginLease lease)
    {
        Name = name;
        _vt = (MfpAudioBackendVTable*)vtable;
        _self = (void*)self;
        _lease = lease;
    }

    public string Name { get; }

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() => Enumerate(_vt->EnumerateOutputs);
    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => Enumerate(_vt->EnumerateInputs);

    public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->OpenOutput == null)
            throw new NotSupportedException("plugin audio backend has no open_output.");
        AbiPluginHost.ClearLastError();
        var handle = OpenHandle(_vt->OpenOutput, deviceId, format, options);
        if (handle == null)
            throw OpenFailed("output");
        var lease = _lease.AcquireDependent();
        try
        {
            return NativeAudioOutput.Create(_vt, handle, format, lease);
        }
        catch
        {
            if (_vt->CloseHandle != null)
                _vt->CloseHandle(handle);
            lease.Dispose();
            throw;
        }
    }

    public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->OpenInput == null)
            throw new NotSupportedException("plugin audio backend has no open_input.");
        AbiPluginHost.ClearLastError();
        var handle = OpenHandle(_vt->OpenInput, deviceId, format, options);
        if (handle == null)
            throw OpenFailed("input");
        return new NativeAudioInput(_vt, handle, format, _lease.AcquireDependent());
    }

    private void* OpenHandle(
        delegate* unmanaged<void*, byte*, MfpAudioFormat*, MfpAudioOpts*, void*> open,
        string? deviceId, AudioFormat format, AudioBackendOptions? options)
    {
        var fmt = new MfpAudioFormat { SampleRate = (uint)format.SampleRate, Channels = (uint)format.Channels, SampleFormat = 0 };
        var opts = new MfpAudioOpts
        {
            SuggestedLatencySeconds = options?.SuggestedLatencySeconds ?? 0.0,
            PrebufferFrames = (uint)Math.Max(0, options?.RingCapacityFrames ?? 0),
        };
        var idBytes = deviceId is null ? null : Utf8(deviceId);
        fixed (byte* idPtr = idBytes)
            return open(_self, idPtr, &fmt, &opts);
    }

    private IReadOnlyList<AudioDeviceInfo> Enumerate(delegate* unmanaged<void*, MfpAudioDeviceInfo*, int, int*, int> enumerate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enumerate == null)
            return [];
        const int cap = 32;
        var buf = stackalloc MfpAudioDeviceInfo[cap];
        var count = 0;
        AbiPluginHost.ClearLastError();
        var status = enumerate(_self, buf, cap, &count);
        if (status != (int)MfpStatus.Ok)
            throw AbiPluginHost.StatusException("plugin audio device enumeration", status);
        if (count <= 0)
            return [];
        count = Math.Min(count, cap);
        var list = new List<AudioDeviceInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var basePtr = (byte*)(buf + i);                        // Id at offset 0, Name at offset 128 (char[128] each)
            var id = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)basePtr) ?? string.Empty;
            var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)(basePtr + 128)) ?? string.Empty;
            list.Add(new AudioDeviceInfo(id, name, (int)buf[i].MaxChannels, buf[i].DefaultSampleRate, false));
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lease.Dispose();
    }

    private static InvalidOperationException OpenFailed(string direction)
    {
        var detail = string.IsNullOrWhiteSpace(AbiPluginHost.LastErrorMessage)
            ? string.Empty
            : $": {AbiPluginHost.LastErrorMessage}";
        return new InvalidOperationException($"plugin audio backend failed to open an {direction} device{detail}");
    }

    private static byte[] Utf8(string s)
    {
        var n = Encoding.UTF8.GetByteCount(s);
        var b = new byte[n + 1];
        Encoding.UTF8.GetBytes(s, b);
        return b;
    }
}

/// <summary>A native plugin audio output: forwards Submit + exposes the plugin's played-frame counter as the
/// master-clock stat (<see cref="IAudioOutputPlaybackStats"/>).</summary>
internal unsafe class NativeAudioOutput : IAudioOutput, IAudioOutputPlaybackStats, IDisposable
{
    private readonly MfpAudioBackendVTable* _vt;
    private readonly void* _handle;
    private readonly AbiPluginLease _lease;
    protected bool Disposed;

    protected NativeAudioOutput(MfpAudioBackendVTable* vt, void* handle, AudioFormat format, AbiPluginLease lease)
    {
        _vt = vt;
        _handle = handle;
        Format = format;
        _lease = lease;
    }

    public static NativeAudioOutput Create(
        MfpAudioBackendVTable* vt, void* handle, AudioFormat format, AbiPluginLease lease) =>
        vt->OutputPlayedFrames != null && vt->OutputWritableFrames != null
            ? new ClockedNativeAudioOutput(vt, handle, format, lease)
            : new NativeAudioOutput(vt, handle, format, lease);

    public AudioFormat Format { get; }
    public long PlayedSamples => _vt->OutputPlayedFrames != null ? Math.Max(0, _vt->OutputPlayedFrames(_handle)) : 0;
    public long UnderrunSamples => 0;
    public long DroppedSamples => 0;

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (_vt->OutputSubmit == null || packedSamples.IsEmpty)
            return;
        if (packedSamples.Length % Format.Channels != 0)
            throw new ArgumentException("Packed audio length must be divisible by the output channel count.", nameof(packedSamples));
        AbiPluginHost.ClearLastError();
        fixed (float* p = packedSamples)
        {
            var status = _vt->OutputSubmit(_handle, p, packedSamples.Length);
            if (status == (int)MfpStatus.ErrAgain)
                throw new InvalidOperationException("plugin audio output has no room; WaitForCapacity must succeed before Submit.");
            if (status != (int)MfpStatus.Ok)
                throw AbiPluginHost.StatusException("plugin audio output submit", status);
        }
    }

    public void Dispose()
    {
        if (Disposed)
            return;
        Disposed = true;
        if (_vt->CloseHandle != null)
            _vt->CloseHandle(_handle);
        _lease.Dispose();
        GC.SuppressFinalize(this);
    }

    ~NativeAudioOutput() => Dispose();

    protected MfpAudioBackendVTable* VTable => _vt;
    protected void* Handle => _handle;
}

internal sealed unsafe class ClockedNativeAudioOutput : NativeAudioOutput, IClockedOutput, IPlaybackClock
{
    internal ClockedNativeAudioOutput(
        MfpAudioBackendVTable* vt, void* handle, AudioFormat format, AbiPluginLease lease)
        : base(vt, handle, format, lease)
    {
    }

    public TimeSpan ElapsedSinceStart =>
        TimeSpan.FromSeconds((double)PlayedSamples / Math.Max(1, Format.SampleRate));

    public bool IsAdvancing => !Disposed;

    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chunkSamples);
        while (!Disposed)
        {
            if (token.IsCancellationRequested)
                return false;
            AbiPluginHost.ClearLastError();
            var writable = VTable->OutputWritableFrames(Handle);
            if (writable >= chunkSamples)
                return true;
            if (writable < 0 && writable != (int)MfpStatus.ErrAgain)
                throw AbiPluginHost.StatusException("plugin audio output capacity query", writable);
            token.WaitHandle.WaitOne(1);
        }
        return false;
    }
}

/// <summary>A native plugin audio input (capture): ReadInto forwards to the plugin's input_read_into.</summary>
internal sealed unsafe class NativeAudioInput : IAudioSource, IDisposable
{
    private readonly MfpAudioBackendVTable* _vt;
    private readonly void* _handle;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    public NativeAudioInput(MfpAudioBackendVTable* vt, void* handle, AudioFormat format, AbiPluginLease lease)
    {
        _vt = vt;
        _handle = handle;
        Format = format;
        _lease = lease;
    }

    public AudioFormat Format { get; }
    public bool IsExhausted => _disposed;

    public int ReadInto(Span<float> destination)
    {
        if (_disposed || _vt->InputReadInto == null || destination.IsEmpty)
            return 0;
        fixed (float* p = destination)
        {
            AbiPluginHost.ClearLastError();
            var result = _vt->InputReadInto(_handle, p, destination.Length);
            if (result is (int)MfpStatus.ErrAgain or (int)MfpStatus.ErrEnd)
                return 0;
            if (result < 0)
                throw AbiPluginHost.StatusException("plugin audio input read", result);
            if (result > destination.Length)
                throw new InvalidOperationException(
                    $"plugin audio input returned {result} floats for a {destination.Length}-float buffer.");
            return result;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->CloseHandle != null)
            _vt->CloseHandle(_handle);
        _lease.Dispose();
        GC.SuppressFinalize(this);
    }

    ~NativeAudioInput() => Dispose();
}
