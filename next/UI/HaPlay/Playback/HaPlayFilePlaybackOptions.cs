
namespace HaPlay.Playback;

/// <summary>Per-open file playback options (output raster preset and cut/fade transition).</summary>
public readonly record struct HaPlayFilePlaybackOptions(
    PlayerOutputPreset OutputPreset = PlayerOutputPreset.AsSource,
    PlayerTransitionMode TransitionMode = PlayerTransitionMode.Cut,
    int TransitionDurationMs = 500,
    int CueFadeInMs = 0,
    int CueFadeOutMs = 0,
    int CustomOutputWidth = 1920,
    int CustomOutputHeight = 1080)
{
    public static HaPlayFilePlaybackOptions Default { get; } = new();

    /// <summary>Video fade-in duration: per-cue fade wins over player transition fade.</summary>
    public int EffectiveVideoFadeInMs =>
        CueFadeInMs > 0
            ? CueFadeInMs
            : TransitionMode == PlayerTransitionMode.Fade
                ? Math.Max(0, TransitionDurationMs)
                : 0;
}
