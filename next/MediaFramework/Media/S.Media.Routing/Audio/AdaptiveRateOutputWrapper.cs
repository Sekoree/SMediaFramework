namespace S.Media.Routing;

/// <summary>
/// Wraps a non-master <see cref="IAudioOutput"/> for per-output adaptive-rate drift correction
/// (FFmpeg-backed). Set <see cref="AudioRouter.AdaptiveRateWrapper"/> from the media registry; this is
/// the typed replacement for the old static <c>MediaFrameworkPlugins.WrapAdaptiveRateOutput</c> slot (P2).
/// </summary>
public delegate IAudioOutput AdaptiveRateOutputWrapper(
    AudioRouter router,
    IAudioOutput inner,
    string outputId,
    int maxRateDeltaHz);
