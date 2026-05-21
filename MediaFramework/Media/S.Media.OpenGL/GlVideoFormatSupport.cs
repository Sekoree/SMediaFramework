using System.Collections.Frozen;
using System.Linq;
using S.Media.Core.Video;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;
using GlInternalFormat = Silk.NET.OpenGL.InternalFormat;
using GlPixelFormat = Silk.NET.OpenGL.PixelFormat;
using GlPixelType = Silk.NET.OpenGL.PixelType;

namespace S.Media.OpenGL;

/// <summary>
/// Single source of truth for OpenGL pixel-format metadata (shaders, plane
/// counts, texture sizing, and default GPU bit scaling).
/// </summary>
internal static class GlVideoFormatSupport
{
    internal readonly record struct GlFormatRecipe(
        string VertexFile,
        string FragmentFile,
        string[] Samplers,
        int PlaneCount,
        Func<VideoFormat, int, (int w, int h)> PlaneSize,
        Func<int, (GlInternalFormat ifmt, GlPixelFormat pfmt, GlPixelType ptype)> PlaneGl,
        float DefaultBitScale,
        bool NeedsYuvMatrix,
        bool NearestSampling);

    private static readonly FrozenDictionary<CorePixelFormat, GlFormatRecipe> Recipes = BuildRecipes();

    private static FrozenDictionary<CorePixelFormat, GlFormatRecipe> BuildRecipes()
    {
        var d = new Dictionary<CorePixelFormat, GlFormatRecipe>
        {
            [CorePixelFormat.Bgra32] = new(
                "fullscreen.vert.glsl", "bgra.frag.glsl",
                ["image"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.Rgba8, GlPixelFormat.Bgra, GlPixelType.UnsignedByte),
                1f, false, false),

            [CorePixelFormat.Rgba32] = new(
                "fullscreen.vert.glsl", "rgba.frag.glsl",
                ["image"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.Rgba8, GlPixelFormat.Rgba, GlPixelType.UnsignedByte),
                1f, false, false),

            [CorePixelFormat.Rgb24] = new(
                "fullscreen.vert.glsl", "rgb8.frag.glsl",
                ["image"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.Rgb8, GlPixelFormat.Rgb, GlPixelType.UnsignedByte),
                1f, false, false),

            [CorePixelFormat.Bgr24] = new(
                "fullscreen.vert.glsl", "rgb8.frag.glsl",
                ["image"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.Rgb8, GlPixelFormat.Bgr, GlPixelType.UnsignedByte),
                1f, false, false),

            [CorePixelFormat.I420] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar420Size,
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, true, false),

            [CorePixelFormat.Yv12] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar420Size,
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, true, false),

            [CorePixelFormat.Yuv422P] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar422Size,
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, true, false),

            [CorePixelFormat.Yuv444P] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar444Size,
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, true, false),

            [CorePixelFormat.Yuv422P10Le] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar422Size,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 1023f, true, false),

            [CorePixelFormat.Nv12] = new(
                "fullscreen.vert.glsl", "yuv_nv12.frag.glsl",
                ["yPlane", "uvPlane"], 2,
                SemiPlanar420Size,
                SemiPlanar420Gl,
                1f, true, false),

            [CorePixelFormat.Nv21] = new(
                "fullscreen.vert.glsl", "yuv_nv21.frag.glsl",
                ["yPlane", "uvPlane"], 2,
                SemiPlanar420Size,
                SemiPlanar420Gl,
                1f, true, false),

            // High 10 bits in each 16-bit word; UNSIGNED_SHORT normalized sampling is already correct.
            [CorePixelFormat.P010] = new(
                "fullscreen.vert.glsl", "yuv_nv12.frag.glsl",
                ["yPlane", "uvPlane"], 2,
                SemiPlanar420Size,
                SemiPlanar420Gl16,
                1f, true, false),

            [CorePixelFormat.P016] = new(
                "fullscreen.vert.glsl", "yuv_nv12.frag.glsl",
                ["yPlane", "uvPlane"], 2,
                SemiPlanar420Size,
                SemiPlanar420Gl16,
                1f, true, false),

            [CorePixelFormat.Argb32] = new(
                "fullscreen.vert.glsl", "argb.frag.glsl",
                ["image"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.Rgba8, GlPixelFormat.Rgba, GlPixelType.UnsignedByte),
                1f, false, false),

            [CorePixelFormat.Abgr32] = new(
                "fullscreen.vert.glsl", "abgr.frag.glsl",
                ["image"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.Rgba8, GlPixelFormat.Rgba, GlPixelType.UnsignedByte),
                1f, false, false),

            [CorePixelFormat.Gray8] = new(
                "fullscreen.vert.glsl", "gray.frag.glsl",
                ["grayPlane"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, false, false),

            [CorePixelFormat.Gray16] = new(
                "fullscreen.vert.glsl", "gray.frag.glsl",
                ["grayPlane"], 1,
                static (vf, p) => p == 0 ? (vf.Width, vf.Height) : (0, 0),
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                1f, false, false),

            [CorePixelFormat.Yuv420P10Le] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar420Size,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 1023f, true, false),

            [CorePixelFormat.Yuv420P12Le] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar420Size,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 4095f, true, false),

            [CorePixelFormat.Yuv444P10Le] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar444Size,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 1023f, true, false),

            [CorePixelFormat.Yuva420p] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar420YuvaSize,
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, true, false),

            [CorePixelFormat.Yuva422P] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar422YuvaSize,
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, true, false),

            [CorePixelFormat.Yuva444P] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar444YuvaSize,
                static _ => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
                1f, true, false),

            [CorePixelFormat.Yuva420P10Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar420YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 1023f, true, false),

            [CorePixelFormat.Yuva422P10Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar422YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 1023f, true, false),

            [CorePixelFormat.Yuva444P10Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar444YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 1023f, true, false),

            [CorePixelFormat.Yuva422P12Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar422YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 4095f, true, false),

            [CorePixelFormat.Yuva444P12Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar444YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 4095f, true, false),

            [CorePixelFormat.Yuva420P16Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar420YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                1f, true, false),

            [CorePixelFormat.Yuva422P16Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar422YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                1f, true, false),

            [CorePixelFormat.Yuva444P16Le] = new(
                "fullscreen.vert.glsl", "yuva_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane", "aPlane"], 4,
                Planar444YuvaSize,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                1f, true, false),

            [CorePixelFormat.Yuv422P12Le] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar422Size,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 4095f, true, false),

            [CorePixelFormat.Yuv444P12Le] = new(
                "fullscreen.vert.glsl", "yuv_planar.frag.glsl",
                ["yPlane", "uPlane", "vPlane"], 3,
                Planar444Size,
                static _ => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
                65535f / 4095f, true, false),

            [CorePixelFormat.Uyvy] = new(
                "fullscreen.vert.glsl", "uyvy422.frag.glsl",
                ["packedTex"], 1,
                Packed422RgbaHalfWidth,
                static _ => (GlInternalFormat.Rgba8, GlPixelFormat.Rgba, GlPixelType.UnsignedByte),
                1f, true, true),

            [CorePixelFormat.Yuyv] = new(
                "fullscreen.vert.glsl", "yuyv422.frag.glsl",
                ["packedTex"], 1,
                Packed422RgbaHalfWidth,
                static _ => (GlInternalFormat.Rgba8, GlPixelFormat.Rgba, GlPixelType.UnsignedByte),
                1f, true, true),
        };
        return d.ToFrozenDictionary();
    }

    internal static bool TryGetRecipe(CorePixelFormat pf, out GlFormatRecipe r) => Recipes.TryGetValue(pf, out r);

    /// <summary>
    /// Sink-side negotiation order: higher-fidelity YUV (e.g. ProRes 422 10-bit) is preferred before
    /// NV12 so hardware decode paths that expose multiple natives do not pick 8-bit 4:2:0 by default.
    /// </summary>
    private static readonly CorePixelFormat[] SupportedPixelFormatsOrder =
    [
        // Highest-fidelity YUVA (alpha-bearing) first so a hardware decode path that exposes both
        // alpha and non-alpha natives prefers the alpha-preserving format. 16-bit > 12-bit > 10-bit > 8-bit.
        CorePixelFormat.Yuva444P16Le,
        CorePixelFormat.Yuva422P16Le,
        CorePixelFormat.Yuva420P16Le,
        CorePixelFormat.Yuva444P12Le,
        CorePixelFormat.Yuva422P12Le,
        CorePixelFormat.Yuva444P10Le,
        CorePixelFormat.Yuva422P10Le,
        CorePixelFormat.Yuva420P10Le,
        CorePixelFormat.Yuva444P,
        CorePixelFormat.Yuva422P,
        CorePixelFormat.Yuva420p,
        // High bit-depth non-alpha YUV.
        CorePixelFormat.Yuv444P12Le,
        CorePixelFormat.Yuv422P12Le,
        CorePixelFormat.Yuv422P10Le,
        CorePixelFormat.Yuv444P10Le,
        CorePixelFormat.Yuv420P10Le,
        CorePixelFormat.Yuv420P12Le,
        CorePixelFormat.P010,
        CorePixelFormat.P016,
        // 8-bit non-alpha YUV.
        CorePixelFormat.Yuv444P,
        CorePixelFormat.Yuv422P,
        CorePixelFormat.I420,
        CorePixelFormat.Yv12,
        CorePixelFormat.Nv12,
        CorePixelFormat.Nv21,
        CorePixelFormat.Uyvy,
        CorePixelFormat.Yuyv,
        // RGB(A) / luminance fall-backs.
        CorePixelFormat.Bgra32,
        CorePixelFormat.Rgba32,
        CorePixelFormat.Bgr24,
        CorePixelFormat.Rgb24,
        CorePixelFormat.Argb32,
        CorePixelFormat.Abgr32,
        CorePixelFormat.Gray8,
        CorePixelFormat.Gray16,
    ];

    internal static CorePixelFormat[] SupportedPixelFormats { get; } = BuildSupportedPixelFormats();

    private static CorePixelFormat[] BuildSupportedPixelFormats()
    {
        var recipeKeys = Recipes.Keys.ToHashSet();
        foreach (var pf in SupportedPixelFormatsOrder)
        {
            if (!recipeKeys.Remove(pf))
                throw new InvalidOperationException(
                    $"GlVideoFormatSupport.SupportedPixelFormatsOrder lists {pf} but no recipe exists (typo?).");
        }

        if (recipeKeys.Count != 0)
            throw new InvalidOperationException(
                "GlVideoFormatSupport: add new PixelFormat entries to SupportedPixelFormatsOrder: "
                + string.Join(", ", recipeKeys.OrderBy(static x => (int)x)));

        return SupportedPixelFormatsOrder;
    }

    private static (int w, int h) Planar420Size(VideoFormat vf, int p) => p switch
    {
        0 => (vf.Width, vf.Height),
        1 or 2 => (PixelFormatInfo.ChromaWidth420(vf.Width), PixelFormatInfo.ChromaHeight420(vf.Height)),
        _ => (0, 0),
    };

    private static (int w, int h) Planar422Size(VideoFormat vf, int p) => p switch
    {
        0 => (vf.Width, vf.Height),
        1 or 2 => (PixelFormatInfo.ChromaWidth422(vf.Width), vf.Height),
        _ => (0, 0),
    };

    private static (int w, int h) Planar444Size(VideoFormat vf, int p) => p is >= 0 and <= 2 ? (vf.Width, vf.Height) : (0, 0);

    private static (int w, int h) Planar420YuvaSize(VideoFormat vf, int p) => p switch
    {
        0 => (vf.Width, vf.Height),
        1 or 2 => (PixelFormatInfo.ChromaWidth420(vf.Width), PixelFormatInfo.ChromaHeight420(vf.Height)),
        3 => (vf.Width, vf.Height),
        _ => (0, 0),
    };

    private static (int w, int h) Planar422YuvaSize(VideoFormat vf, int p) => p switch
    {
        0 => (vf.Width, vf.Height),
        1 or 2 => (PixelFormatInfo.ChromaWidth422(vf.Width), vf.Height),
        3 => (vf.Width, vf.Height),
        _ => (0, 0),
    };

    private static (int w, int h) Planar444YuvaSize(VideoFormat vf, int p) =>
        p is >= 0 and <= 3 ? (vf.Width, vf.Height) : (0, 0);

    private static (int w, int h) SemiPlanar420Size(VideoFormat vf, int p) => p switch
    {
        0 => (vf.Width, vf.Height),
        1 => (PixelFormatInfo.ChromaWidth420(vf.Width), PixelFormatInfo.ChromaHeight420(vf.Height)),
        _ => (0, 0),
    };

    private static (GlInternalFormat ifmt, GlPixelFormat pfmt, GlPixelType ptype) SemiPlanar420Gl(int p) => p switch
    {
        0 => (GlInternalFormat.R8, GlPixelFormat.Red, GlPixelType.UnsignedByte),
        _ => (GlInternalFormat.RG8, GlPixelFormat.RG, GlPixelType.UnsignedByte),
    };

    private static (GlInternalFormat ifmt, GlPixelFormat pfmt, GlPixelType ptype) SemiPlanar420Gl16(int p) => p switch
    {
        0 => (GlInternalFormat.R16, GlPixelFormat.Red, GlPixelType.UnsignedShort),
        _ => (GlInternalFormat.RG16, GlPixelFormat.RG, GlPixelType.UnsignedShort),
    };

    private static (int w, int h) Packed422RgbaHalfWidth(VideoFormat vf, int p) =>
        p == 0 ? (PixelFormatInfo.ChromaWidth422(vf.Width), vf.Height) : (0, 0);
}
