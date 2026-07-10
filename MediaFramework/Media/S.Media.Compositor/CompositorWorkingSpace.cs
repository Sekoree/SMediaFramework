namespace S.Media.Compositor;

/// <summary>The pixel precision the compositor blends in (D12). 8-bit BT.709 for an all-SDR graph;
/// linear-light RGBA16F when any HDR / wide-gamut content is present.</summary>
public enum CompositorWorkingSpace
{
    /// <summary>8-bit BT.709 - every input and output is SDR.</summary>
    Sdr8 = 0,

    /// <summary>Linear-light RGBA16F - some HDR / wide-gamut content is present.</summary>
    Hdr16F = 1,
}

/// <summary>
/// Chooses and holds the compositor working space (D12 / OQ7). HDR or wide-gamut content (PQ/HLG transfer
/// or BT.2020 primaries on any input or output) promotes the graph to linear RGBA16F; an all-SDR graph
/// blends in 8-bit BT.709. Promotion is <strong>eager</strong> (the moment HDR appears); demotion happens
/// <strong>only at an explicit boundary</strong> (cue change / idle) so a running show never resets its
/// FBOs mid-playback. The working space is therefore chosen at <c>Configure</c>/graph-rebuild, not per
/// frame. Returning <c>true</c> from <see cref="Promote"/>/<see cref="DemoteAtBoundary"/> signals the
/// caller to rebuild FBOs at the next frame boundary (same path as a resolution change).
/// </summary>
public sealed class CompositorWorkingSpaceController
{
    private CompositorWorkingSpace _current;

    public CompositorWorkingSpaceController(CompositorWorkingSpace initial = CompositorWorkingSpace.Sdr8) =>
        _current = initial;

    /// <summary>The current working space.</summary>
    public CompositorWorkingSpace Current => _current;

    /// <summary>The GL FBO/readback precision for the current working space.</summary>
    public GlCompositorOutputPrecision Precision =>
        _current == CompositorWorkingSpace.Hdr16F
            ? GlCompositorOutputPrecision.Rgba16F
            : GlCompositorOutputPrecision.Rgba8;

    /// <summary>True when <paramref name="space"/>/<paramref name="transfer"/> need the wide/linear path:
    /// PQ or HLG transfer, or BT.2020 primaries.</summary>
    public static bool IsHdrOrWideGamut(VideoColorSpace space, VideoTransferHint transfer) =>
        transfer is VideoTransferHint.FromPq or VideoTransferHint.FromHlg
        || space is VideoColorSpace.Bt2020 or VideoColorSpace.Bt2020Cl;

    /// <summary>
    /// Promote-eager step - call when the active layer/output color set changes. Promotes SDR→HDR16F the
    /// instant any HDR/wide-gamut content is present and returns <c>true</c> (rebuild FBOs). Never demotes,
    /// so a transient SDR gap can't tear down the HDR pipeline mid-show.
    /// </summary>
    public bool Promote(bool anyHdrOrWideGamut)
    {
        if (anyHdrOrWideGamut && _current == CompositorWorkingSpace.Sdr8)
        {
            _current = CompositorWorkingSpace.Hdr16F;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Demote step - call ONLY at a cue/idle boundary (the hysteresis point). Drops HDR16F→SDR8 when no
    /// HDR/wide-gamut content remains and returns <c>true</c> (rebuild FBOs); a no-op otherwise.
    /// </summary>
    public bool DemoteAtBoundary(bool anyHdrOrWideGamut)
    {
        if (!anyHdrOrWideGamut && _current == CompositorWorkingSpace.Hdr16F)
        {
            _current = CompositorWorkingSpace.Sdr8;
            return true;
        }

        return false;
    }
}
