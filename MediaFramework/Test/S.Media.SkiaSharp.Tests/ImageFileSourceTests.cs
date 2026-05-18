using S.Media.Core.Video;
using S.Media.SkiaSharp;
using SkiaSharp;
using Xunit;

namespace S.Media.SkiaSharp.Tests;

public sealed class ImageFileSourceTests
{
    [Fact]
    public void OpenFromStream_DecodesPngToBgra32()
    {
        using var stream = WriteTestPng(width: 32, height: 16, fill: SKColors.Red);
        stream.Position = 0;

        using var src = ImageFileSource.OpenFromStream(stream);
        Assert.Equal(32, src.Format.Width);
        Assert.Equal(16, src.Format.Height);
        Assert.Equal(PixelFormat.Bgra32, src.Format.PixelFormat);
        Assert.Equal(new[] { PixelFormat.Bgra32 }, src.NativePixelFormats);

        Assert.True(src.TryReadNextFrame(out var frame));
        Assert.Equal(32, frame.Format.Width);
        Assert.Equal(16, frame.Format.Height);
        Assert.Single(frame.Planes);
        Assert.Equal(32 * 4, frame.Strides[0]);

        // Premul BGRA8888 for solid red: B=0, G=0, R=255, A=255.
        var plane = frame.Planes[0].Span;
        Assert.Equal(0, plane[0]);
        Assert.Equal(0, plane[1]);
        Assert.Equal(255, plane[2]);
        Assert.Equal(255, plane[3]);

        frame.Dispose();
    }

    [Fact]
    public void TryReadNextFrame_EmitsRepeatedlyWithMonotonicPts()
    {
        using var stream = WriteTestPng(8, 8, SKColors.Blue);
        stream.Position = 0;

        using var src = ImageFileSource.OpenFromStream(stream);
        Assert.True(src.TryReadNextFrame(out var first));
        Assert.True(src.TryReadNextFrame(out var second));
        Assert.True(second.PresentationTime > first.PresentationTime);
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void Dispose_StopsEmitting()
    {
        using var stream = WriteTestPng(8, 8, SKColors.Green);
        stream.Position = 0;

        var src = ImageFileSource.OpenFromStream(stream);
        src.Dispose();
        Assert.True(src.IsExhausted);
        Assert.False(src.TryReadNextFrame(out _));
    }

    [Fact]
    public void OpenFromStream_GarbageInput_Throws()
    {
        using var ms = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        Assert.Throws<InvalidDataException>(() => ImageFileSource.OpenFromStream(ms));
    }

    private static MemoryStream WriteTestPng(int width, int height, SKColor fill)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(fill);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return ms;
    }
}
