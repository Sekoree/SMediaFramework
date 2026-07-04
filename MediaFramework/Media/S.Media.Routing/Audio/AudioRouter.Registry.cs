namespace S.Media.Routing;

/// <summary>
/// Registry-wired capability hooks for <see cref="AudioRouter"/>. These replace the old process-wide
/// <c>MediaFrameworkPlugins</c> slots (P2): the host/session sets them once from the media registry
/// before the router runs. Both are optional — when unset, the dependent feature (autoResample /
/// adaptive-rate) throws a clear error instead of silently using global state.
/// </summary>
public sealed partial class AudioRouter
{
    /// <summary>
    /// Resampler factory <c>(inner, targetSampleRate) =&gt; wrapped</c>, wired from
    /// <c>IMediaRegistry.CreateResampler</c>. Required when adding a source with <c>autoResample: true</c>.
    /// </summary>
    public Func<IAudioSource, int, IAudioSource>? ResamplerFactory { get; set; }

    /// <summary>
    /// Adaptive-rate output wrapper for non-master outputs, wired from the FFmpeg module via the registry.
    /// Required by <see cref="EnableAdaptiveRateOnNonMasterOutputs"/>.
    /// </summary>
    public AdaptiveRateOutputWrapper? AdaptiveRateWrapper { get; set; }
}
