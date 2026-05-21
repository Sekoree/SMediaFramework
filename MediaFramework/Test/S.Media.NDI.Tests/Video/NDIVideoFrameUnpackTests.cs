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
}
