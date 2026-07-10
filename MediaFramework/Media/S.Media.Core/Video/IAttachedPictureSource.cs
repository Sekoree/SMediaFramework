namespace S.Media.Core.Video;

/// <summary>
/// Optional capability for an <see cref="IVideoSource"/> whose video is a single attached picture (album art /
/// cover image - FFmpeg's <c>AV_DISPOSITION_ATTACHED_PIC</c>) rather than a motion video track. Consumers can
/// query this to drive a still-frame display mode (decode/present the one frame and hold it) instead of running
/// a continuous video pump. Sources that never carry attached pictures simply don't implement the interface.
/// </summary>
public interface IAttachedPictureSource
{
    /// <summary>True when the video stream is a single attached cover picture.</summary>
    bool IsAttachedPicture { get; }
}
