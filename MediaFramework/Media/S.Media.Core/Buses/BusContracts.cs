using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Buses;

/// <summary>
/// One audio processing stage hosted by an insertable bus node (see <c>S.Media.Routing.AudioEffectBus</c>).
/// <see cref="Process"/> runs on the audio pull path and must be real-time safe: in-place, no
/// allocation, no locks held across it, bounded work per chunk.
/// </summary>
public interface IAudioBusEffect : IDisposable
{
    /// <summary>Called once before the first <see cref="Process"/> and again if the host reconfigures.</summary>
    void Configure(AudioFormat format);

    /// <summary>Processes one interleaved chunk in place. <paramref name="samplePosition"/> is the
    /// running per-channel sample count since the bus started (for LFOs/automation).</summary>
    void Process(Span<float> interleaved, long samplePosition);
}

/// <summary>
/// One video processing stage hosted by an insertable output decorator (see
/// <c>S.Media.Routing.VideoEffectBusOutput</c>). <see cref="Process"/> runs on the pump drain thread,
/// off the clock path; it may replace the frame (returning ownership rules below).
/// </summary>
public interface IVideoBusEffect : IDisposable
{
    /// <summary>Called once with the negotiated format before the first <see cref="Process"/> (and
    /// again after a format change).</summary>
    void Configure(VideoFormat format);

    /// <summary>
    /// Processes one frame. To pass through or mutate CPU pixels in place, return <paramref name="frame"/>
    /// unchanged. To emit a different frame, return the replacement and dispose the input - the caller
    /// treats the returned frame as the one it owns and submits downstream.
    /// </summary>
    VideoFrame Process(VideoFrame frame, TimeSpan presentationTime);
}

/// <summary>
/// A generator that consumes audio and produces video - visualizers. The audio face is attached to the
/// router like any output (tap); the video face is attached like any source (player/compositor layer).
/// Implementations decide their internal buffering (typically a lock-free PCM ring drained on render).
/// </summary>
public interface IAudioVisualSource : IVideoSource, IAudioOutput
{
}
