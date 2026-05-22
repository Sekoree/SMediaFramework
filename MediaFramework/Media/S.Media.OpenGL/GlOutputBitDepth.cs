namespace S.Media.OpenGL;

/// <summary>Requested GL drawable / swapchain color depth for windowed outputs.</summary>
public enum GlOutputBitDepth
{
    /// <summary>8 bits per RGB channel (default).</summary>
    Eight = 8,

    /// <summary>10-bit RGB + 2-bit alpha (e.g. <c>R10G10B10A2</c>).</summary>
    Ten = 10,

    /// <summary>Try 10-bit when creating the context; fall back to 8-bit with a single warning if unavailable.</summary>
    Auto = 0,
}
