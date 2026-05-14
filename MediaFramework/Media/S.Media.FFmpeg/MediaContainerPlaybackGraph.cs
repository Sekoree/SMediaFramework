using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Playback;
using S.Media.Core.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Holds the usual file-playback trio (<see cref="MediaContainerDecoder"/>, <see cref="VideoPlayer"/>, clock)
/// plus an <see cref="AvRouter"/> built with the same <see cref="IAvPlaybackSession"/> wiring as <see cref="MediaContainerAvRouter.Create"/>.
/// Does not own the decoder, video player, clock, or audio player — callers keep existing disposal order.
/// </summary>
/// <remarks>
/// <para>
/// This holder is <strong>not</strong> <see cref="IDisposable"/> (no composite <c>Dispose</c>); disposal is unchanged from ad-hoc wiring: release <see cref="Video"/>, <see cref="Audio"/> (if any),
/// <see cref="Decoder"/>, and any shared resources in an order consistent with your graph; this type only groups references.
/// For a single <see cref="IDisposable"/> owner with a fixed mux-safe teardown order and per-step <strong>Debug</strong>
/// <see cref="S.Media.Core.Diagnostics.MediaDiagnostics"/> logging on owned parts, see <see cref="MediaContainerMegaPlaybackHost"/>.
/// </para>
/// <para>
/// Graph-wide master-clock PPM, synchronized multi-sink drop/repeat, or other coordinated timing policy is not implemented
/// by this holder — see <see cref="MediaClock"/>, <see cref="S.Media.Core.Audio.AudioRouter"/>, and checklist Tier E **18**
/// (<see cref="Audio.AdaptiveRateAudioSink"/> for per-sink resampling hints).
/// </para>
/// </remarks>
public sealed class MediaContainerPlaybackGraph
{
    public MediaContainerPlaybackGraph(
        MediaContainerDecoder decoder,
        VideoPlayer video,
        IMediaClock clock,
        AudioPlayer? audio = null)
    {
        Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Audio = audio;
        Router = MediaContainerAvRouter.Create(decoder, video, clock, audio);
    }

    public MediaContainerDecoder Decoder { get; }

    public VideoPlayer Video { get; }

    public IMediaClock Clock { get; }

    public AudioPlayer? Audio { get; }

    public AvRouter Router { get; }
}
