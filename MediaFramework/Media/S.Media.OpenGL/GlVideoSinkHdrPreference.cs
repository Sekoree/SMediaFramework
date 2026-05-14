namespace S.Media.OpenGL;

/// <summary>
/// How GL-backed video sinks map per-frame <see cref="S.Media.Core.Video.VideoFrame.ColorTransferHint"/>
/// into <see cref="YuvVideoRenderer.HdrTransfer"/>.
/// </summary>
public enum GlVideoSinkHdrPreference
{
    FollowFrameHints = 0,
    IgnoreFrameHints = 1,

    ForceSdrDisplay,
    ForceSrgbPreview,
    ForcePqPreview,
    ForceHlgPreview,
}
