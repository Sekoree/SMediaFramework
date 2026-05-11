using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Audio;

namespace S.Media.NDI.Audio;

/// <summary>
/// <see cref="IAudioSink"/> backed by an <see cref="NDIOutput"/>'s shared
/// <see cref="NDISender"/>. Constructed only via <see cref="NDIOutput.EnableAudio"/>.
/// </summary>
/// <remarks>
/// Send path — hands packed float samples directly to
/// <c>NDIlib_util_send_send_audio_interleaved_32f</c> (the SDK does the
/// deinterleave + send in optimized native code). Lifetime is owned by
/// <see cref="NDIOutput"/>; callers do not dispose this sink directly.
/// </remarks>
public sealed unsafe class NDIAudioSink : IAudioSink, IDisposable
{
    private readonly NDISender _sender;
    private readonly AudioFormat _format;
    private byte* _packedBuffer;
    private int _packedCapacityBytes;
    private bool _disposed;

    public AudioFormat Format => _format;

    internal NDIAudioSink(NDISender sender, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format), "sample rate must be positive");
        if (format.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(format), "channel count must be positive");
        _sender = sender;
        _format = format;
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

        var packedBytes = packedSamples.Length * sizeof(float);
        EnsurePackedCapacity(packedBytes);
        packedSamples.CopyTo(new Span<float>(_packedBuffer, packedSamples.Length));

        var interleaved = new NDIAudioInterleaved32f
        {
            SampleRate = _format.SampleRate,
            NoChannels = channels,
            NoSamples = samplesPerChannel,
            Timecode = 0x7FFFFFFFFFFFFFFF,
            PData = (nint)_packedBuffer,
        };
        NDIAudioUtils.SendInterleaved32f(_sender, interleaved);
    }

    private void EnsurePackedCapacity(int neededBytes)
    {
        if (_packedCapacityBytes >= neededBytes) return;
        if (_packedBuffer != null) NativeMemory.Free(_packedBuffer);
        var capacity = 1;
        while (capacity < neededBytes) capacity <<= 1;
        _packedBuffer = (byte*)NativeMemory.Alloc((nuint)capacity);
        _packedCapacityBytes = capacity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_packedBuffer != null)
        {
            NativeMemory.Free(_packedBuffer);
            _packedBuffer = null;
            _packedCapacityBytes = 0;
        }
        // The NDISender is owned by NDIOutput — do NOT dispose it here.
    }
}
