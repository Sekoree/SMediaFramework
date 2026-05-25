namespace S.Media.OpenGL;

/// <summary>How a video frame is mapped into a pixel viewport.</summary>
public enum VideoViewportFit
{
    /// <summary>Stretch to fill the viewport (may distort aspect ratio).</summary>
    Stretch = 0,
    /// <summary>Scale uniformly to fit inside the viewport; letter/pillar-box.</summary>
    Contain = 1,
    /// <summary>Scale uniformly so the frame fully covers the viewport (center crop).</summary>
    Cover = 2,
}

/// <summary>Computes GL viewport rectangles for <see cref="VideoViewportFit"/>.</summary>
public static class VideoViewportLayout
{
    /// <returns>(x, y, width, height) in framebuffer pixels, relative to the given viewport origin.</returns>
    public static (int x, int y, int w, int h) Compute(
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight,
        int frameWidth,
        int frameHeight,
        VideoViewportFit fit)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || frameWidth <= 0 || frameHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "dimensions must be positive");

        if (fit == VideoViewportFit.Stretch)
            return (viewportX, viewportY, viewportWidth, viewportHeight);

        var vw = viewportWidth;
        var vh = viewportHeight;
        var fw = (double)frameWidth;
        var fh = (double)frameHeight;
        double s = fit == VideoViewportFit.Contain
            ? Math.Min(vw / fw, vh / fh)
            : Math.Max(vw / fw, vh / fh);

        var rw = (int)Math.Clamp(Math.Round(fw * s), 1d, double.MaxValue);
        var rh = (int)Math.Clamp(Math.Round(fh * s), 1d, double.MaxValue);

        if (fit == VideoViewportFit.Contain)
        {
            rw = Math.Min(rw, vw);
            rh = Math.Min(rh, vh);
        }

        var ox = viewportX + (vw - rw) / 2;
        var oy = viewportY + (vh - rh) / 2;
        return (ox, oy, rw, rh);
    }
}
