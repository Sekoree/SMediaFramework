namespace S.Media.Core.Video;

/// <summary>
/// Canonical video stream description shared by every source and sink:
/// FFmpeg-decoded files, NDI receivers, SDL3 / Avalonia displays, etc.
/// </summary>
public readonly record struct VideoFormat(
    int Width,
    int Height,
    PixelFormat PixelFormat,
    Rational FrameRate);
