using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class VideoFrameMetadataTests
{
    private static readonly VideoFormat Bgra32_4x4 = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));

    [Fact]
    public void Defaults_AreUnspecifiedAndProgressive()
    {
        var plane = new byte[4 * 4 * 4];
        var frame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, plane, 16, release: null);
        try
        {
            Assert.Equal(VideoColorSpace.Unspecified, frame.ColorSpace);
            Assert.Equal(VideoColorRange.Unspecified, frame.ColorRange);
            Assert.Equal(VideoFieldOrder.Progressive, frame.FieldOrder);
            Assert.Equal(VideoAlphaMode.Unspecified, frame.AlphaMode);
            Assert.Null(frame.Timecode);
        }
        finally { frame.Dispose(); }
    }

    [Fact]
    public void NewMetadataParams_Surface()
    {
        var plane = new byte[4 * 4 * 4];
        var tc = new VideoTimecode(0, 0, 1, 0, false, new Rational(30, 1));
        var meta = new VideoFrameMetadata(
            ColorSpace: VideoColorSpace.Bt2020,
            ColorRange: VideoColorRange.Limited,
            FieldOrder: VideoFieldOrder.TopFieldFirst,
            Timecode: tc,
            AlphaMode: VideoAlphaMode.Straight);
        var frame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, plane, 16, release: null, metadata: meta);
        try
        {
            Assert.Equal(VideoColorSpace.Bt2020, frame.ColorSpace);
            Assert.Equal(VideoColorRange.Limited, frame.ColorRange);
            Assert.Equal(VideoFieldOrder.TopFieldFirst, frame.FieldOrder);
            Assert.NotNull(frame.Timecode);
            Assert.Equal(VideoAlphaMode.Straight, frame.AlphaMode);
            Assert.Equal(0, frame.Timecode!.Value.Frames);
            Assert.Equal(1, frame.Timecode.Value.Seconds);
            // Property-style read forwards to the same value as direct Metadata access.
            Assert.Equal(meta, frame.Metadata);
        }
        finally { frame.Dispose(); }
    }

    [Fact]
    public void MetadataIsBundled_ReadableAsRecord()
    {
        var plane = new byte[4 * 4 * 4];
        var meta = new VideoFrameMetadata(
            ColorTransferHint: VideoTransferHint.FromPq,
            ColorSpace: VideoColorSpace.Bt2020);
        var frame = new VideoFrame(TimeSpan.Zero, Bgra32_4x4, plane, 16, metadata: meta);
        try
        {
            Assert.Equal(meta, frame.Metadata);
            Assert.Equal(VideoTransferHint.FromPq, frame.ColorTransferHint);
            // "with" expression should mutate cleanly.
            var mutated = frame.Metadata with { FieldOrder = VideoFieldOrder.TopFieldFirst };
            Assert.Equal(VideoFieldOrder.TopFieldFirst, mutated.FieldOrder);
            Assert.Equal(VideoColorSpace.Bt2020, mutated.ColorSpace);
        }
        finally { frame.Dispose(); }
    }
}
