namespace S.Media.Core.Video;

/// <summary>
/// Canonical video stream description shared by every source and output:
/// FFmpeg-decoded files, NDI receivers, SDL3 / Avalonia displays, etc.
/// </summary>
public readonly record struct VideoFormat(
    int Width,
    int Height,
    PixelFormat PixelFormat,
    Rational FrameRate)
{
    /// <summary>
    /// Throws <see cref="ArgumentException"/> when this format can't drive a pipeline: non-positive
    /// dimensions or a non-positive frame rate. Call before wiring a format into a player/router/output
    /// so a bad frame fails at the boundary instead of deep inside SDL/GL/FFmpeg/NDI (mirrors
    /// <see cref="Audio.AudioFormat.Validate"/>). Pixel-format suitability is left to each output's
    /// <see cref="IVideoOutput.AcceptedPixelFormats"/> negotiation.
    /// </summary>
    public void Validate(string? paramName = null)
    {
        if (Width <= 0 || Height <= 0)
            throw new ArgumentException(
                $"VideoFormat dimensions must be positive (was {Width}x{Height}, pixelFormat={PixelFormat}).",
                paramName);
        if (FrameRate.Numerator <= 0 || FrameRate.Denominator <= 0)
            throw new ArgumentException(
                $"VideoFormat.FrameRate must be positive (was {FrameRate.Numerator}/{FrameRate.Denominator}, dimensions={Width}x{Height}).",
                paramName);
    }
}
