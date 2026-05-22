using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Video;
using S.Media.NDI.Video;
using Xunit;

namespace S.Media.NDI.Tests.Video;

public sealed class NDIVideoFrameUnpackTests
{
    [Fact]
    public void TryUnpack_Yv12_MapsToYv12PlanesInYuvOrder()
    {
        const int width = 4;
        const int height = 2;
        const int yStride = width;
        var y = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17 };
        var v = new byte[] { 101, 102 };
        var u = new byte[] { 201, 202 };
        var payload = new byte[y.Length + v.Length + u.Length];
        Buffer.BlockCopy(y, 0, payload, 0, y.Length);
        Buffer.BlockCopy(v, 0, payload, y.Length, v.Length);
        Buffer.BlockCopy(u, 0, payload, y.Length + v.Length, u.Length);

        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.Yv12,
                FrameRateN = 25,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = yStride,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(PixelFormat.Yv12, frame.Format.PixelFormat);
                Assert.Equal(3, frame.PlaneCount);
                Assert.Equal(yStride, frame.Strides[0]);
                Assert.Equal(PixelFormatInfo.ChromaWidth420(width), frame.Strides[1]);
                Assert.Equal(PixelFormatInfo.ChromaWidth420(width), frame.Strides[2]);
                Assert.Equal(y, frame.Planes[0].ToArray());
                Assert.Equal(u, frame.Planes[1].ToArray());
                Assert.Equal(v, frame.Planes[2].ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_Uyvy_PaddedLineStride_TightensRows()
    {
        const int width = 4;
        const int height = 2;
        const int visibleStride = width * 2;
        const int paddedStride = visibleStride + 8;
        var row0 = new byte[] { 10, 20, 30, 40, 11, 21, 31, 41 };
        var row1 = new byte[] { 50, 60, 70, 80, 51, 61, 71, 81 };
        var payload = new byte[paddedStride * height];
        Buffer.BlockCopy(row0, 0, payload, 0, row0.Length);
        Buffer.BlockCopy(row1, 0, payload, paddedStride, row1.Length);

        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.Uyvy,
                FrameRateN = 30,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = paddedStride,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(PixelFormat.Uyvy, frame.Format.PixelFormat);
                Assert.Equal(VideoColorRange.Full, frame.ColorRange);
                Assert.Equal(VideoColorSpace.Bt709, frame.ColorSpace);
                Assert.Equal(visibleStride, frame.Strides[0]);
                Assert.Equal(visibleStride * height, frame.Planes[0].Length);
                Assert.Equal(row0, frame.Planes[0].Span[..row0.Length].ToArray());
                Assert.Equal(row1, frame.Planes[0].Span.Slice(visibleStride, row1.Length).ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_Uyvy_WhenXresEqualsLineStrideBytes_RecoversPixelWidth()
    {
        const int pixelWidth = 4;
        const int height = 1;
        const int lineStrideBytes = pixelWidth * 2;
        var payload = new byte[] { 10, 20, 30, 40, 11, 21, 31, 41 };
        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                // Mis-labelled: Xres carries line stride in bytes, not luma width.
                Xres = lineStrideBytes,
                Yres = height,
                FourCC = NDIFourCCVideoType.Uyvy,
                FrameRateN = 30,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = lineStrideBytes,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(pixelWidth, frame.Format.Width);
                Assert.Equal(lineStrideBytes, frame.Strides[0]);
                Assert.Equal(payload, frame.Planes[0].ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_Uyvy_WhenLineStrideIsTotalBufferSize_UsesDefaultLineStride()
    {
        // Needs height >= 3 so total PData (width*height*2) is unambiguously larger than 2× per-line stride.
        const int width = 4;
        const int height = 3;
        const int visibleStride = width * 2;
        var row0 = new byte[] { 10, 20, 30, 40, 11, 21, 31, 41 };
        var row1 = new byte[] { 50, 60, 70, 80, 51, 61, 71, 81 };
        var row2 = new byte[] { 90, 100, 110, 120, 91, 101, 111, 121 };
        var payload = new byte[visibleStride * height];
        Buffer.BlockCopy(row0, 0, payload, 0, row0.Length);
        Buffer.BlockCopy(row1, 0, payload, visibleStride, row1.Length);
        Buffer.BlockCopy(row2, 0, payload, visibleStride * 2, row2.Length);
        var totalBufferSize = payload.Length;

        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.Uyvy,
                FrameRateN = 30,
                FrameRateD = 1,
                PData = ptr,
                // Some senders put total PData bytes here instead of per-line stride.
                LineStrideInBytes = totalBufferSize,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(width, frame.Format.Width);
                Assert.Equal(visibleStride, frame.Strides[0]);
                Assert.Equal(row0, frame.Planes[0].Span[..row0.Length].ToArray());
                Assert.Equal(row1, frame.Planes[0].Span.Slice(visibleStride, row1.Length).ToArray());
                Assert.Equal(row2, frame.Planes[0].Span.Slice(visibleStride * 2, row2.Length).ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_Uyvy_WhenXresIsDoubleActiveLineWidth_UsesStrideWidth()
    {
        const int pixelWidth = 4;
        const int height = 1;
        const int lineStrideBytes = pixelWidth * 2;
        var payload = new byte[] { 10, 20, 30, 40, 11, 21, 31, 41 };
        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = pixelWidth * 2,
                Yres = height,
                FourCC = NDIFourCCVideoType.Uyvy,
                FrameRateN = 30,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = lineStrideBytes,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(pixelWidth, frame.Format.Width);
                Assert.Equal(lineStrideBytes, frame.Strides[0]);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_Bgrx_MapsToBgra32()
    {
        const int width = 2;
        const int height = 1;
        const int stride = width * 4;
        var payload = new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 };
        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.Bgrx,
                FrameRateN = 30,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = stride,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(PixelFormat.Bgra32, frame.Format.PixelFormat);
                Assert.Equal(stride, frame.Strides[0]);
                Assert.Equal(payload, frame.Planes[0].ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_Uyva_UsesColorPlaneAsUyvy()
    {
        const int width = 2;
        const int height = 1;
        const int stride = width * 2;
        var uyvy = new byte[] { 120, 10, 140, 20 }; // U, Y0, V, Y1
        var alpha = new byte[] { 255, 64 };
        var payload = new byte[uyvy.Length + alpha.Length];
        Buffer.BlockCopy(uyvy, 0, payload, 0, uyvy.Length);
        Buffer.BlockCopy(alpha, 0, payload, uyvy.Length, alpha.Length);

        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.Uyva,
                FrameRateN = 30,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = stride,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(PixelFormat.Uyvy, frame.Format.PixelFormat);
                Assert.Equal(stride, frame.Strides[0]);
                Assert.Equal(uyvy, frame.Planes[0].ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_P216_ConvertsToUyvy8Bit()
    {
        const int width = 2;
        const int height = 1;
        const int stride = width * sizeof(ushort);

        // Y16: [0x4000, 0x8000] -> Y8 [64, 128] (rounded)
        var y = new byte[] { 0x00, 0x40, 0x00, 0x80 };
        // UV16 pair: U=0x2000, V=0x6000 -> U/V 8-bit [32, 96] (rounded)
        var uv = new byte[] { 0x00, 0x20, 0x00, 0x60 };
        var payload = new byte[y.Length + uv.Length];
        Buffer.BlockCopy(y, 0, payload, 0, y.Length);
        Buffer.BlockCopy(uv, 0, payload, y.Length, uv.Length);

        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.P216,
                FrameRateN = 60,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = stride,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(PixelFormat.Uyvy, frame.Format.PixelFormat);
                Assert.Equal(width * 2, frame.Strides[0]);
                Assert.Equal(new byte[] { 32, 64, 96, 128 }, frame.Planes[0].ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void TryUnpack_Pa16_IgnoresAlphaAndConvertsToUyvy8Bit()
    {
        const int width = 2;
        const int height = 1;
        const int stride = width * sizeof(ushort);

        // Y16 -> Y8 [16, 240]
        var y = new byte[] { 0x00, 0x10, 0x00, 0xF0 };
        // UV16 -> U/V 8-bit [64, 192]
        var uv = new byte[] { 0x00, 0x40, 0x00, 0xC0 };
        // Alpha16 plane (ignored by unpack path).
        var alpha = new byte[] { 0x34, 0x12, 0xCD, 0xAB };
        var payload = new byte[y.Length + uv.Length + alpha.Length];
        Buffer.BlockCopy(y, 0, payload, 0, y.Length);
        Buffer.BlockCopy(uv, 0, payload, y.Length, uv.Length);
        Buffer.BlockCopy(alpha, 0, payload, y.Length + uv.Length, alpha.Length);

        var ptr = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, ptr, payload.Length);
            var native = new NDIVideoFrameV2
            {
                Xres = width,
                Yres = height,
                FourCC = NDIFourCCVideoType.Pa16,
                FrameRateN = 25,
                FrameRateD = 1,
                PData = ptr,
                LineStrideInBytes = stride,
            };

            Assert.True(NDIVideoFrameUnpack.TryUnpack(native, TimeSpan.Zero, out var frame));
            Assert.NotNull(frame);
            using (frame!)
            {
                Assert.Equal(PixelFormat.Uyvy, frame.Format.PixelFormat);
                Assert.Equal(width * 2, frame.Strides[0]);
                Assert.Equal(new byte[] { 64, 16, 192, 240 }, frame.Planes[0].ToArray());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
