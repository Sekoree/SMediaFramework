using System.Text;
using S.Media.Core.Audio;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpAudioBackendVTable</c> to the managed <see cref="IAudioBackend"/> — device
/// enumeration plus opening <see cref="NativeAudioOutput"/> / <see cref="NativeAudioInput"/>. Registered into a live
/// <see cref="S.Media.Core.Registry.IMediaRegistry"/> via <c>AddAudioBackend</c>, a plugin backend is a peer of
/// PortAudio/miniaudio.
/// </summary>
public sealed unsafe class NativeAudioBackend : IAudioBackend
{
    private readonly MfpAudioBackendVTable* _vt;
    private readonly void* _self;

    internal NativeAudioBackend(string name, nint vtable, nint self)
    {
        Name = name;
        _vt = (MfpAudioBackendVTable*)vtable;
        _self = (void*)self;
    }

    public string Name { get; }

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() => Enumerate(_vt->EnumerateOutputs);
    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => Enumerate(_vt->EnumerateInputs);

    public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        if (_vt->OpenOutput == null)
            throw new NotSupportedException("plugin audio backend has no open_output.");
        var handle = OpenHandle(_vt->OpenOutput, deviceId, format, options);
        if (handle == null)
            throw new InvalidOperationException("plugin audio backend failed to open an output device.");
        return new NativeAudioOutput(_vt, handle, format);
    }

    public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        if (_vt->OpenInput == null)
            throw new NotSupportedException("plugin audio backend has no open_input.");
        var handle = OpenHandle(_vt->OpenInput, deviceId, format, options);
        if (handle == null)
            throw new InvalidOperationException("plugin audio backend failed to open an input device.");
        return new NativeAudioInput(_vt, handle, format);
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
        if (enumerate == null)
            return [];
        const int cap = 32;
        var buf = stackalloc MfpAudioDeviceInfo[cap];
        var count = 0;
        if (enumerate(_self, buf, cap, &count) != (int)MfpStatus.Ok || count <= 0)
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
internal sealed unsafe class NativeAudioOutput : IAudioOutput, IAudioOutputPlaybackStats, IDisposable
{
    private readonly MfpAudioBackendVTable* _vt;
    private readonly void* _handle;
    private bool _disposed;

    public NativeAudioOutput(MfpAudioBackendVTable* vt, void* handle, AudioFormat format)
    {
        _vt = vt;
        _handle = handle;
        Format = format;
    }

    public AudioFormat Format { get; }
    public long PlayedSamples => _vt->OutputPlayedFrames != null ? _vt->OutputPlayedFrames(_handle) : 0;
    public long UnderrunSamples => 0;
    public long DroppedSamples => 0;

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        if (_disposed || _vt->OutputSubmit == null || packedSamples.IsEmpty)
            return;
        fixed (float* p = packedSamples)
            _vt->OutputSubmit(_handle, p, packedSamples.Length);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->CloseHandle != null)
            _vt->CloseHandle(_handle);
    }
}

/// <summary>A native plugin audio input (capture): ReadInto forwards to the plugin's input_read_into.</summary>
internal sealed unsafe class NativeAudioInput : IAudioSource, IDisposable
{
    private readonly MfpAudioBackendVTable* _vt;
    private readonly void* _handle;
    private bool _disposed;

    public NativeAudioInput(MfpAudioBackendVTable* vt, void* handle, AudioFormat format)
    {
        _vt = vt;
        _handle = handle;
        Format = format;
    }

    public AudioFormat Format { get; }
    public bool IsExhausted => _disposed;

    public int ReadInto(Span<float> destination)
    {
        if (_disposed || _vt->InputReadInto == null || destination.IsEmpty)
            return 0;
        fixed (float* p = destination)
            return _vt->InputReadInto(_handle, p, destination.Length);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_vt->CloseHandle != null)
            _vt->CloseHandle(_handle);
    }
}
