namespace S.Media.SDL3;

/// <summary>
/// How <see cref="SDL3GLVideoSink"/> maps per-frame <see cref="S.Media.Core.Video.VideoFrame.ColorTransferHint"/>
/// into <see cref="S.Media.OpenGL.YuvVideoRenderer.HdrTransfer"/>.
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
