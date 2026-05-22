namespace S.Media.Effects;

/// <summary>Off-screen composite FBO precision for <see cref="OpenGL.GlVideoCompositor"/>.</summary>
public enum GlCompositorOutputPrecision
{
    /// <summary>RGBA8 readback as <see cref="S.Media.Core.Video.PixelFormat.Bgra32"/> (default).</summary>
    Rgba8 = 0,

    /// <summary>RGBA16 UNORM readback as <see cref="S.Media.Core.Video.PixelFormat.Rgba16"/>.</summary>
    Rgba16 = 16,

    /// <summary>RGBA16F readback as <see cref="S.Media.Core.Video.PixelFormat.Rgba16F"/>.</summary>
    Rgba16F = 1,
}
