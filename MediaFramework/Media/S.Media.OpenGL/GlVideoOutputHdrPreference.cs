namespace S.Media.OpenGL;

/// <summary>
/// How GL-backed video outputs map per-frame <see cref="S.Media.Core.Video.VideoFrame.ColorTransferHint"/>
/// into <see cref="YuvVideoRenderer.HdrTransfer"/>.
/// </summary>
public enum GlVideoOutputHdrPreference
{
    FollowFrameHints = 0,
    IgnoreFrameHints = 1,

    ForceSdrDisplay,
    ForceSrgbPreview,
    ForcePqPreview,
    ForceHlgPreview,
}
