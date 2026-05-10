using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Audio;

namespace S.Media.NDI.Audio;

/// <summary>
/// <see cref="IAudioSink"/> backed by an NDI sender. Each <see cref="Submit"/>
/// call is converted from packed float32 to NDI's planar FLTP format and sent
/// via <c>NDIlib_send_send_audio_v3</c>.
/// </summary>
/// <remarks>
/// Synchronous send: when the sender is created with <c>clockAudio = true</c>
/// (default), <see cref="Submit"/> blocks long enough to pace the audio
/// stream at its declared sample rate, which gives the network receivers a
/// stable cadence.
/// </remarks>
public sealed unsafe class NDIAudioSender : IAudioSink, IDisposable
{
    private readonly NDIRuntime _runtime;
    private readonly NDISender _sender;
    private readonly AudioFormat _format;
    private byte* _planarBuffer;
    private int _planarCapacityBytes;
    private bool _disposed;

    public AudioFormat Format => _format;
    public string SourceName { get; }
    public int ConnectionCount => _sender.GetConnectionCount();

    public NDIAudioSender(AudioFormat format, string sourceName, string? groups = null, bool clockAudio = true)
    {
        if (format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format), "sample rate must be positive");
        if (format.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(format), "channel count must be positive");
        ArgumentException.ThrowIfNullOrEmpty(sourceName);

        _format = format;
        SourceName = sourceName;

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null) throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            rc = NDISender.Create(out var sender, sourceName, groups, clockVideo: false, clockAudio: clockAudio);
            if (rc != 0 || sender is null) throw new NDIException(rc, "NDISender.Create");
            _sender = sender;
        }
        catch
        {
            _runtime.Dispose();
            throw;
        }
    }

    public void Submit(in AudioFrame frame)
    {
        if (frame.Format != _format)
            throw new ArgumentException(
                $"frame format {frame.Format} does not match sender format {_format}", nameof(frame));
        Submit(frame.Samples.Span);
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channels = _format.Channels;
        if (packedSamples.Length % channels != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {channels}",
                nameof(packedSamples));

        var samplesPerChannel = packedSamples.Length / channels;
        if (samplesPerChannel == 0) return;

        // Build a planar (FLTP) NDI frame from our packed source.
        // NDIlib_send_send_audio_v3 is synchronous so the unmanaged buffer is
        // safe to reuse on the next call.
        var planeStrideBytes = samplesPerChannel * sizeof(float);
        var planarBytes = planeStrideBytes * channels;
        EnsurePlanarCapacity(planarBytes);

        var planarFloats = (float*)_planarBuffer;
        // Deinterleave: source[s, c] (packed) → dest[c, s] (planar).
        for (var s = 0; s < samplesPerChannel; s++)
        {
            var srcBase = s * channels;
            for (var c = 0; c < channels; c++)
                planarFloats[c * samplesPerChannel + s] = packedSamples[srcBase + c];
        }

        var native = new NDIAudioFrameV3
        {
            SampleRate = _format.SampleRate,
            NoChannels = channels,
            NoSamples = samplesPerChannel,
            FourCC = NDIFourCCAudioType.Fltp,
            PData = (nint)_planarBuffer,
            ChannelStrideInBytes = planeStrideBytes,
            Timecode = 0x7FFFFFFFFFFFFFFF, // synthesise: sender is its own clock
            PMetadata = nint.Zero,
            Timestamp = 0,
        };

        _sender.SendAudio(native);
    }

    private void EnsurePlanarCapacity(int neededBytes)
    {
        if (_planarCapacityBytes >= neededBytes) return;
        if (_planarBuffer != null) NativeMemory.Free(_planarBuffer);
        // Round up to a power of two so growing once doesn't immediately
        // reallocate again on a slightly larger chunk.
        var capacity = 1;
        while (capacity < neededBytes) capacity <<= 1;
        _planarBuffer = (byte*)NativeMemory.Alloc((nuint)capacity);
        _planarCapacityBytes = capacity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_planarBuffer != null)
        {
            NativeMemory.Free(_planarBuffer);
            _planarBuffer = null;
            _planarCapacityBytes = 0;
        }
        _sender.Dispose();
        _runtime.Dispose();
    }
}
