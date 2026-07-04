using S.Media.Core.Video;

namespace S.Media.Gpu;

/// <summary>Shared HDR hint mapping for <see cref="YuvVideoRenderer"/> used by SDL3 and Avalonia GL outputs.</summary>
public static class GlVideoOutputHdr
{
    public static void ApplyTransferHint(YuvVideoRenderer renderer, VideoFrame frame, GlVideoOutputHdrPreference hdrPreference)
    {
        switch (hdrPreference)
        {
            case GlVideoOutputHdrPreference.IgnoreFrameHints:
                return;
            case GlVideoOutputHdrPreference.ForceSdrDisplay:
                renderer.HdrTransfer = VideoHdrTransfer.None;
                return;
            case GlVideoOutputHdrPreference.ForceSrgbPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Srgb;
                return;
            case GlVideoOutputHdrPreference.ForcePqPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Pq;
                return;
            case GlVideoOutputHdrPreference.ForceHlgPreview:
                renderer.HdrTransfer = VideoHdrTransfer.Hlg;
                return;
            case GlVideoOutputHdrPreference.FollowFrameHints:
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
