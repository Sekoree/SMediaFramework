using S.Media.Core.Video;

namespace S.Media.OpenGL;

/// <summary>Shared HDR hint mapping for <see cref="YuvVideoRenderer"/> used by SDL3 and Avalonia GL sinks.</summary>
public static class GlVideoSinkHdr
{
    public static void ApplyTransferHint(YuvVideoRenderer renderer, VideoFrame frame, GlVideoSinkHdrPreference hdrPreference)
    {
        switch (hdrPreference)
        {
            case GlVideoSinkHdrPreference.IgnoreFrameHints:
                return;
            case GlVideoSinkHdrPreference.ForceSdrDisplay:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
            case GlVideoSinkHdrPreference.ForceSrgbPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Srgb;
                return;
            case GlVideoSinkHdrPreference.ForcePqPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Pq;
                return;
            case GlVideoSinkHdrPreference.ForceHlgPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Hlg;
                return;
            case GlVideoSinkHdrPreference.FollowFrameHints:
                break;
            default:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
        }

        switch (frame.ColorTransferHint)
        {
            case VideoTransferHint.Unspecified:
                return;
            case VideoTransferHint.Sdr:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
            case VideoTransferHint.FromSrgb:
                renderer.HdrTransfer = VideoHdrTransfer.Srgb;
                return;
            case VideoTransferHint.FromPq:
                renderer.HdrTransfer = VideoHdrTransfer.Pq;
                return;
            case VideoTransferHint.FromHlg:
                renderer.HdrTransfer = VideoHdrTransfer.Hlg;
                return;
            default:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
        }
    }
}
