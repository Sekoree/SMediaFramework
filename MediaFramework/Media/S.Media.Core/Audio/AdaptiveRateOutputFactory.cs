namespace S.Media.Core.Audio;

/// <summary>
/// Builds a per-output adaptive-rate drift-correction wrapper around a non-master <see cref="IAudioOutput"/>
/// (FFmpeg/swresample-backed). Wired into the media registry by the FFmpeg module and applied by the router
/// to its non-master outputs.
/// </summary>
/// <param name="inner">The non-master output to wrap.</param>
/// <param name="playbackPpmBias">The router-derived drift signal (parts-per-million, signed): negative ⇒ the
/// output is slow, shed samples; positive ⇒ starved, add samples.</param>
/// <param name="maxRateDeltaHz">Clamp for the rate correction around the device rate.</param>
/// <param name="biasSource">Optional object disposed with the returned wrapper - e.g. the (Routing-side)
/// pump-pressure monitor backing <paramref name="playbackPpmBias"/>, so its subscription is released when the
/// output is removed.</param>
public delegate IAudioOutput AdaptiveRateOutputFactory(
    IAudioOutput inner,
    Func<double> playbackPpmBias,
    int maxRateDeltaHz,
    IDisposable? biasSource);
