namespace S.Media.Core.Video;

/// <summary>
/// Discriminated hardware memory backing for a <see cref="VideoFrame"/>.
/// At most one backing may be attached per frame (<c>null</c> for CPU-only frames).
/// </summary>
public abstract class VideoFrameHardwareBacking : IDisposable
{
    public abstract void Dispose();
}
