using S.Media.OpenGL;
using Xunit;

namespace S.Media.OpenGL.Tests;

public sealed class Nv12Win32SharedHandleGpuUploaderLayoutTests
{
    [Fact]
    public void Nv12ChromaPlaneByteOffset_UsesAllocatedTextureHeight_NotVisibleHeight()
    {
        var offset = Nv12Win32SharedHandleGpuUploader.Nv12ChromaPlaneByteOffset(
            rowPitchBytes: 2048,
            d3dTextureHeight: 1088,
            visibleLumaHeight: 1080);

        Assert.Equal((nint)(2048 * 1088), offset);
    }

    [Fact]
    public void Nv12InteropR8TextureHeight_IncludesStackedChromaRows()
    {
        Assert.Equal(1632, Nv12Win32SharedHandleGpuUploader.Nv12InteropR8TextureHeight(1088));
    }

    [Fact]
    public void Nv12LumaAllocationRowsForChromaOffset_RejectsTooSmallTexture()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Nv12Win32SharedHandleGpuUploader.Nv12LumaAllocationRowsForChromaOffset(
                d3dTextureHeight: 1072,
                visibleLumaHeight: 1080));
    }
}
