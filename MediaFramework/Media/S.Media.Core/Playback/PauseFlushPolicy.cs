namespace S.Media.Core.Playback;

/// <summary>
/// Whether coordinated pause/seek should run a shared-mux libav flush after A/V quiesce.
/// </summary>
public enum PauseFlushPolicy
{
    /// <summary>Run the default flush hook (e.g. <c>MediaContainerDecoder.FlushCodecPipelines</c>).</summary>
    FlushCodecPipelines,

    /// <summary>Skip flush — use when the decode thread may still be inside libav.</summary>
    SkipFlush,
}
