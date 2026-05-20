using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.NDI;

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
/// <para>
/// <see cref="Submit(in AudioFrame)"/> stamps NDI <c>Timecode</c> from
/// <see cref="AudioFrame.PresentationTime"/> (100 ns ticks). When an <see cref="NDIOutput"/> uses
/// <see cref="Video.NDIVideoTimecodeMode.PresentationRelativeTicks"/>, a shared egress timeline
/// re-bases audio timecodes to the session anchor so they match video. Otherwise presentation time is sent as absolute mux ticks.
/// <see cref="Submit(ReadOnlySpan{float})"/> uses a running sample counter when no frame PTS is available.
/// </para>
/// <para>
/// <strong>SDK pacing:</strong> <see cref="NDIOutput"/> normally constructs the shared <see cref="NDISender"/> with
/// <c>clockAudio:true</c> so the runtime can throttle sends to the negotiated sample rate. There is no separate
/// wall-clock throttle on this path like optional spacing on <see cref="Video.NDIVideoSender"/> for video.
/// </para>
/// <para>
/// A single native packed buffer is grown with headroom (at least double the
/// prior capacity, rounded up to a power of two) so upstream chunk-size
/// changes during the first seconds of a session rarely require more than one
/// or two reallocations on the pump thread.
/// </para>
/// <para>
/// <see cref="Dispose"/> frees the native packed buffer; <strong>Debug</strong> builds log failures via <see cref="MediaDiagnostics.LogError"/>.
/// </para>
/// </remarks>
public sealed unsafe class NDIAudioSink : IAudioSink, IDisposable
{
    private readonly NDISender _sender;
    private readonly AudioFormat _format;
    private readonly NDIEgressPresentationTimeline? _presentationTimeline;
    private byte* _packedBuffer;
    private int _packedCapacityBytes;
    private bool _disposed;
    private int _firstSubmitLogged;

    private long _samplesSentPerChannel;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.Audio.NDIAudioSink");

    public AudioFormat Format => _format;

    internal NDIAudioSink(NDISender sender, AudioFormat format, NDIEgressPresentationTimeline? presentationTimeline = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format), "sample rate must be positive");
        if (format.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(format), "channel count must be positive");
        _sender = sender;
        _format = format;
        _presentationTimeline = presentationTimeline;
    }

    public void Submit(in AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Format != _format)
            throw new ArgumentException(
                $"frame format {frame.Format} does not match sender format {_format}", nameof(frame));
        var timecode100Ns = _presentationTimeline is not null
            ? _presentationTimeline.TimecodeFromPresentationTime(frame.PresentationTime)
            : frame.PresentationTime.Ticks;
        SubmitCore(frame.Samples.Span, timecode100Ns);
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

        // NDI timecode is 100 ns units — same as TimeSpan.Ticks.
        var timecode100Ns = _samplesSentPerChannel * 10_000_000L / _format.SampleRate;
        SubmitCore(packedSamples, timecode100Ns);
    }

    /// <summary>
    /// Packs and sends; <paramref name="timecode100Ns"/> is the NDI timecode for the first sample in this packet.
    /// </summary>
    private void SubmitCore(ReadOnlySpan<float> packedSamples, long timecode100Ns)
    {
        var channels = _format.Channels;
        var samplesPerChannel = packedSamples.Length / channels;
        if (samplesPerChannel == 0) return;

        if (Interlocked.Exchange(ref _firstSubmitLogged, 1) == 0)
            Trace.LogDebug("First Submit: format={Format} samples={Samples} tc100ns={TC}",
                _format, samplesPerChannel, timecode100Ns);

        var packedBytes = packedSamples.Length * sizeof(float);
        EnsurePackedCapacity(packedBytes);
        packedSamples.CopyTo(new Span<float>(_packedBuffer, packedSamples.Length));

        var interleaved = new NDIAudioInterleaved32f
        {
            SampleRate = _format.SampleRate,
            NoChannels = channels,
            NoSamples = samplesPerChannel,
            Timecode = timecode100Ns,
            PData = (nint)_packedBuffer,
        };
        NDIAudioUtils.SendInterleaved32f(_sender, interleaved);
        _samplesSentPerChannel += samplesPerChannel;
    }

    private void EnsurePackedCapacity(int neededBytes)
    {
        if (_packedCapacityBytes >= neededBytes) return;

        // Grow with slack so modest increases in chunk size (e.g. router pump
        // tuning) do not thrash Alloc/Free on every Submit.
        var minNext = Math.Max(neededBytes, _packedCapacityBytes > 0 ? _packedCapacityBytes * 2 : neededBytes);
        var capacity = 1;
        while (capacity < minNext) capacity <<= 1;

        _packedBuffer = (byte*)NativeMemory.Realloc(_packedBuffer, (nuint)capacity);
        _packedCapacityBytes = capacity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_packedBuffer != null)
        {
            try
            {
                NativeMemory.Free(_packedBuffer);
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "NDIAudioSink.Dispose: packed buffer");
            }
#else
            catch
            {
            }
#endif
            _packedBuffer = null;
            _packedCapacityBytes = 0;
        }
        // The NDISender is owned by NDIOutput — do NOT dispose it here.
    }
}
