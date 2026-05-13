using System.Diagnostics;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Video;

public sealed class VideoFileDecoderTests : IDisposable
{
    private static readonly VideoDecoderOpenOptions SoftwareDecodeOnly = new() { TryHardwareAcceleration = false };

    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 10;
    private const int DurationSeconds = 1;

    private readonly string? _videoPath;

    public VideoFileDecoderTests()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sm_video_test_{Guid.NewGuid():N}.mp4");
        if (TryGenerateVideo(path, Width, Height, Fps, DurationSeconds))
            _videoPath = path;
        else
            _videoPath = null;
    }

    public void Dispose()
    {
        if (_videoPath != null)
        {
            try { File.Delete(_videoPath); }
#if DEBUG
            catch (Exception ex) { MediaDiagnostics.LogError(ex, $"{nameof(VideoFileDecoderTests)}: temp video delete"); }
#else
            catch { /* ignored */ }
#endif
        }
    }

    [Fact]
    public void Open_ReportsExpectedFormat()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);

        Assert.Equal(Width, decoder.Format.Width);
        Assert.Equal(Height, decoder.Format.Height);
        Assert.InRange(decoder.Format.FrameRate.ToDouble(), Fps - 0.1, Fps + 0.1);
        Assert.False(string.IsNullOrEmpty(decoder.CodecName));
        // H.264 from FFmpeg testsrc decodes to YUV420P → I420 pass-through.
        Assert.Equal(PixelFormat.I420, decoder.Format.PixelFormat);
    }

    [Fact]
    public void ReadAllFrames_TotalsToExpectedFrameCount()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        var frames = 0;
        while (decoder.TryReadNextFrame(out var frame))
        {
            using (frame)
            {
                Assert.Equal(decoder.Format, frame.Format);
                frames++;
            }
        }

        Assert.True(decoder.IsAtEnd);
        // Allow ±2 for codec-side rounding around the GOP boundary.
        Assert.InRange(frames, Fps * DurationSeconds - 2, Fps * DurationSeconds + 2);
    }

    [Fact]
    public void I420Frame_HasThreePlanesWithExpectedDimensions()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        Assert.True(decoder.TryReadNextFrame(out var frame));
        using (frame)
        {
            Assert.Equal(3, frame.Planes.Length);
            // Y plane: at least width × height bytes (stride may pad).
            Assert.True(frame.Strides[0] >= Width);
            Assert.True(frame.Planes[0].Length >= Width * Height);
            // U/V: stride >= width/2, length >= width/2 × height/2
            Assert.True(frame.Strides[1] >= Width / 2);
            Assert.True(frame.Planes[1].Length >= Width / 2 * (Height / 2));
            Assert.True(frame.Strides[2] >= Width / 2);
            Assert.True(frame.Planes[2].Length >= Width / 2 * (Height / 2));
        }
    }

    [Fact]
    public void NativePixelFormats_ContainsCodecOutput()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        Assert.Contains(PixelFormat.I420, decoder.NativePixelFormats);
    }

    [Fact]
    public void SelectOutputFormat_Bgra32_ConvertsAndPacksSinglePlane()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        decoder.SelectOutputFormat(PixelFormat.Bgra32);

        Assert.Equal(PixelFormat.Bgra32, decoder.Format.PixelFormat);

        Assert.True(decoder.TryReadNextFrame(out var frame));
        using (frame)
        {
            Assert.Equal(PixelFormat.Bgra32, frame.Format.PixelFormat);
            Assert.Single(frame.Planes);
            Assert.Equal(Width * 4, frame.Strides[0]);
            Assert.Equal(Width * 4 * Height, frame.Planes[0].Length);
        }
    }

    [Fact]
    public void SelectOutputFormat_BackToNative_RestoresPassThrough()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        decoder.SelectOutputFormat(PixelFormat.Bgra32);
        decoder.SelectOutputFormat(PixelFormat.I420);

        Assert.Equal(PixelFormat.I420, decoder.Format.PixelFormat);

        Assert.True(decoder.TryReadNextFrame(out var frame));
        using (frame)
        {
            Assert.Equal(3, frame.Planes.Length);
        }
    }

    [Fact]
    public void Frame_DisposeReleasesUnderlyingFrame()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        Assert.True(decoder.TryReadNextFrame(out var frame));
        // Should not throw — release callback is one-shot.
        frame.Dispose();
        frame.Dispose();
    }

    [Fact]
    public void Position_AdvancesWithFrames()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        Assert.True(decoder.TryReadNextFrame(out var f1));
        f1.Dispose();
        var after1 = decoder.Position;
        Assert.True(decoder.TryReadNextFrame(out var f2));
        f2.Dispose();
        var after2 = decoder.Position;

        Assert.True(after2 > after1, $"position {after2} should advance past {after1}");
    }

    [Fact]
    public void Seek_RewindsAndAllowsReread()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        while (decoder.TryReadNextFrame(out var f)) f.Dispose();
        Assert.True(decoder.IsAtEnd);

        decoder.Seek(TimeSpan.Zero);
        Assert.False(decoder.IsAtEnd);
        Assert.True(decoder.TryReadNextFrame(out var first));
        first.Dispose();
    }

    [Fact]
    public void Seek_NegativeThrows()
    {
        if (_videoPath is null) return;

        using var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Seek(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Open_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => VideoFileDecoder.Open("/no/such/file.mp4"));
    }

    [Fact]
    public void Dispose_IsIdempotentAndBlocksFurtherReads()
    {
        if (_videoPath is null) return;

        var decoder = VideoFileDecoder.Open(_videoPath, SoftwareDecodeOnly);
        decoder.Dispose();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.TryReadNextFrame(out _));
    }

    private static bool TryGenerateVideo(string path, int width, int height, int fps, int durationSec)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y",
                    "-f", "lavfi",
                    "-i", $"testsrc=size={width}x{height}:rate={fps}:duration={durationSec}",
                    "-pix_fmt", "yuv420p",
                    "-c:v", "libx264",
                    "-loglevel", "error",
                    path,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
