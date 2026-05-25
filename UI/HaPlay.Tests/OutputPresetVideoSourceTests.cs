using HaPlay.Playback;
using S.Media.Core;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

public sealed class OutputPresetVideoSourceTests
{
    [Fact]
    public void TryReadNextFrame_ConvertsUyvyLiveInput_ToBgraPresetRaster()
    {
        const int width = 4;
        const int height = 2;
        const int uyvyStride = width * 2;
        var uyvy = new byte[]
        {
            128, 16, 128, 32, 128, 48, 128, 64,
            128, 80, 128, 96, 128, 112, 128, 128,
        };
        var uyvyFormat = new VideoFormat(width, height, PixelFormat.Uyvy, new Rational(30, 1));
        using var src = new OneShotSource(uyvyFormat, new VideoFrame(
            TimeSpan.Zero,
            uyvyFormat,
            uyvy,
            uyvyStride,
            release: null));

        var target = new VideoFormat(8, 4, PixelFormat.Bgra32, new Rational(30, 1));
        using var wrapped = new OutputPresetVideoSource(src, target);

        Assert.True(wrapped.TryReadNextFrame(out var composed));
        using (composed)
        {
            Assert.Equal(PixelFormat.Bgra32, composed.Format.PixelFormat);
            Assert.Equal(8, composed.Format.Width);
            Assert.Equal(4, composed.Format.Height);
            // Center of letterboxed red-ish macropixel (Y=32) should not be near zero.
            var mid = composed.Planes[0].Span[(composed.Strides[0] * 1) + 8];
            Assert.True(mid > 15, $"Expected converted UYVY sample to be non-black; got {mid}.");
        }
    }

    [Fact]
    public void TryReadNextFrame_DoesNotDisposeSourceBeforeComposition()
    {
        var format = new VideoFormat(2, 2, PixelFormat.Bgra32, new Rational(30, 1));
        var payload = new byte[2 * 2 * 4];
        for (var i = 0; i < payload.Length; i += 4)
        {
            payload[i + 0] = 0;   // B
            payload[i + 1] = 0;   // G
            payload[i + 2] = 255; // R
            payload[i + 3] = 255; // A
        }

        var disposed = 0;
        var frame = new VideoFrame(
            TimeSpan.Zero,
            format,
            payload,
            stride: 2 * 4,
            release: DisposableRelease.Wrap(() =>
            {
                disposed++;
                Array.Clear(payload);
            }));

        using var src = new OneShotSource(format, frame);
        using var wrapped = new OutputPresetVideoSource(src, format);

        Assert.True(wrapped.TryReadNextFrame(out var composed));
        try
        {
            var span = composed.Planes[0].Span;
            Assert.Equal(255, span[2]); // R should survive until compose reads the layer.
            Assert.Equal(255, span[3]); // A
        }
        finally
        {
            composed.Dispose();
        }

        Assert.Equal(0, disposed); // held by the compositor slot for future output ticks.
        wrapped.Dispose();
        Assert.Equal(1, disposed); // disposed once when the slot is torn down.
    }

    private sealed class OneShotSource(VideoFormat format, VideoFrame frame) : IVideoSource, IDisposable
    {
        private bool _emitted;

        public VideoFormat Format => format;

        public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [format.PixelFormat];

        public bool IsExhausted => _emitted;

        public void SelectOutputFormat(PixelFormat pixelFormat)
        {
            if (pixelFormat != format.PixelFormat)
                throw new InvalidOperationException();
        }

        public bool TryReadNextFrame(out VideoFrame next)
        {
            if (_emitted)
            {
                next = null!;
                return false;
            }

            _emitted = true;
            next = frame;
            return true;
        }

        public void Dispose()
        {
            if (!_emitted)
            {
                _emitted = true;
                frame.Dispose();
            }
        }
    }
}
