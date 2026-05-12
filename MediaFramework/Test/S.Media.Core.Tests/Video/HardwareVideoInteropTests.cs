using System.Runtime.InteropServices;
using S.Media.Core.Video;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class HardwareVideoInteropTests
{
    private static bool IsVulkanInteropTestHost()
        => OperatingSystem.IsLinux()
           || OperatingSystem.IsWindows()
           || OperatingSystem.IsMacOS()
           || OperatingSystem.IsMacCatalyst()
           || OperatingSystem.IsAndroid()
           || OperatingSystem.IsIOS()
           || OperatingSystem.IsTvOS();

    private static bool IsAppleHardwareVideoOs()
        => OperatingSystem.IsMacOS()
           || OperatingSystem.IsMacCatalyst()
           || OperatingSystem.IsIOS()
           || OperatingSystem.IsTvOS();

    [DllImport("libc", EntryPoint = "dup")]
    private static extern int dup(int fd);

    [Fact]
    public void NoOpInterop_DisablesImports()
    {
        IHardwareVideoInterop h = new NoOpHardwareVideoInterop();
        Assert.False(h.IsGpuImportSupported);
        Assert.Equal(0, h.PlatformContextHandle);
        Assert.False(h.TryDescribeImportedSurface((nint)1, out var d));
        Assert.Equal(default, d);
    }

    [Fact]
    public void LinuxDmabufNv12Interop_OnLinux_describes_imported_nv12_from_token()
    {
        if (!OperatingSystem.IsLinux())
            return;

        IHardwareVideoInterop h = new LinuxDmabufNv12Interop();
        Assert.True(h.IsGpuImportSupported);

        using var file = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)file.DangerousGetHandle());
        var y = dup(baseFd);
        var uv = dup(baseFd);
        Assert.True(y >= 0 && uv >= 0);

        var backing = new VideoDmabufNv12Backing(y, 0, 256, uv, 0, 256, 0xABCDUL, 0xDCBAUL);
        var token = LinuxDmabufNv12Interop.AllocToken(backing, 3840, 2160);
        try
        {
            Assert.True(h.TryDescribeImportedSurface(token, out var d));
            Assert.Equal(3840, d.WidthPixels);
            Assert.Equal(2160, d.HeightPixels);
            Assert.Equal(2, (int)d.PlaneCount);
            Assert.Equal(HardwareVideoMemoryKind.LinuxDmabufFd, d.Plane0.Kind);
            Assert.Equal((nint)y, d.Plane0.HandleOrDescriptor);
            Assert.Equal((nuint)256, d.Plane0.RowPitchBytes);
            Assert.Equal(0xABCDUL, d.Plane0.Modifier);
            Assert.Equal(HardwareVideoMemoryKind.LinuxDmabufFd, d.Plane1.Kind);
            Assert.Equal((nint)uv, d.Plane1.HandleOrDescriptor);
            Assert.Equal((nuint)256, d.Plane1.RowPitchBytes);
            Assert.Equal(0xDCBAUL, d.Plane1.Modifier);
        }
        finally
        {
            LinuxDmabufNv12Interop.FreeToken(token);
        }

        backing.Dispose();
    }

    [Fact]
    public void LinuxDmabufNv12Interop_TryDescribe_zero_token_returns_false()
    {
        IHardwareVideoInterop h = new LinuxDmabufNv12Interop();
        Assert.False(h.TryDescribeImportedSurface(0, out var d));
        Assert.Equal(default, d);
    }

    [Fact]
    public void WindowsNv12SharedHandleInterop_AllocToken_Throws_OnNonWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Throws<PlatformNotSupportedException>(() =>
            WindowsNv12SharedHandleInterop.AllocToken((nint)1, (nint)2, 64, 64, 64, 64));
    }

    [Fact]
    public void WindowsNv12SharedHandleInterop_NotWindows_DisablesImports()
    {
        if (OperatingSystem.IsWindows())
            return;

        IHardwareVideoInterop h = new WindowsNv12SharedHandleInterop();
        Assert.False(h.IsGpuImportSupported);
        Assert.False(h.TryDescribeImportedSurface((nint)1, out var d));
        Assert.Equal(default, d);
    }

    [Fact]
    public void WindowsNv12SharedHandleInterop_OnWindows_describes_nv12_from_token()
    {
        if (!OperatingSystem.IsWindows())
            return;

        IHardwareVideoInterop h = new WindowsNv12SharedHandleInterop();
        Assert.True(h.IsGpuImportSupported);

        var token = WindowsNv12SharedHandleInterop.AllocToken((nint)101, (nint)202, 1280, 720, 1280, 640);
        try
        {
            Assert.True(h.TryDescribeImportedSurface(token, out var d));
            Assert.Equal(1280, d.WidthPixels);
            Assert.Equal(720, d.HeightPixels);
            Assert.Equal(2, (int)d.PlaneCount);
            Assert.Equal(HardwareVideoMemoryKind.Win32SharedHandle, d.Plane0.Kind);
            Assert.Equal((nint)101, d.Plane0.HandleOrDescriptor);
            Assert.Equal((nuint)1280, d.Plane0.RowPitchBytes);
            Assert.Equal(0UL, d.Plane0.Modifier);
            Assert.Equal(HardwareVideoMemoryKind.Win32SharedHandle, d.Plane1.Kind);
            Assert.Equal((nint)202, d.Plane1.HandleOrDescriptor);
            Assert.Equal((nuint)640, d.Plane1.RowPitchBytes);
            Assert.Equal(0UL, d.Plane1.Modifier);
        }
        finally
        {
            WindowsNv12SharedHandleInterop.FreeToken(token);
        }
    }

    [Fact]
    public void WindowsNv12SharedHandleInterop_OnWindows_zero_chroma_handle_uses_luma_handle()
    {
        if (!OperatingSystem.IsWindows())
            return;

        IHardwareVideoInterop h = new WindowsNv12SharedHandleInterop();
        var token = WindowsNv12SharedHandleInterop.AllocToken((nint)55, 0, 320, 240, 320, 320);
        try
        {
            Assert.True(h.TryDescribeImportedSurface(token, out var d));
            Assert.Equal((nint)55, d.Plane0.HandleOrDescriptor);
            Assert.Equal((nint)55, d.Plane1.HandleOrDescriptor);
        }
        finally
        {
            WindowsNv12SharedHandleInterop.FreeToken(token);
        }
    }

    [Fact]
    public void WindowsNv12SharedHandleInterop_OnWindows_zero_luma_throws()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsNv12SharedHandleInterop.AllocToken(0, (nint)1, 10, 10, 1, 1));
    }

    [Fact]
    public void WindowsNv12SharedHandleInterop_TryDescribe_zero_token_returns_false()
    {
        if (!OperatingSystem.IsWindows())
            return;

        IHardwareVideoInterop h = new WindowsNv12SharedHandleInterop();
        Assert.False(h.TryDescribeImportedSurface(0, out var d));
        Assert.Equal(default, d);
    }

    [Fact]
    public void VulkanExternalNv12Interop_WhenUnsupportedOs_DisablesImports()
    {
        if (IsVulkanInteropTestHost())
            return;

        IHardwareVideoInterop h = new VulkanExternalNv12Interop();
        Assert.False(h.IsGpuImportSupported);
        Assert.Throws<PlatformNotSupportedException>(() =>
            VulkanExternalNv12Interop.AllocToken((nint)1, 0, 1, 64, 64, 64, 64));
    }

    [Fact]
    public void VulkanExternalNv12Interop_OnHost_describes_nv12_from_token()
    {
        if (!IsVulkanInteropTestHost())
            return;

        IHardwareVideoInterop h = new VulkanExternalNv12Interop();
        Assert.True(h.IsGpuImportSupported);

        const uint vkHandleType = 1; // arbitrary non-zero test discriminant (not a real Vk flag in CI)
        var token = VulkanExternalNv12Interop.AllocToken((nint)301, (nint)302, vkHandleType, 1920, 1080, 1920, 960);
        try
        {
            Assert.True(h.TryDescribeImportedSurface(token, out var d));
            Assert.Equal(1920, d.WidthPixels);
            Assert.Equal(1080, d.HeightPixels);
            Assert.Equal(2, (int)d.PlaneCount);
            Assert.Equal(HardwareVideoMemoryKind.VulkanExternal, d.Plane0.Kind);
            Assert.Equal((nint)301, d.Plane0.HandleOrDescriptor);
            Assert.Equal((nuint)1920, d.Plane0.RowPitchBytes);
            Assert.Equal(0UL, d.Plane0.Modifier);
            Assert.Equal(vkHandleType, d.Plane0.ExternalMemoryHandleType);
            Assert.Equal(HardwareVideoMemoryKind.VulkanExternal, d.Plane1.Kind);
            Assert.Equal((nint)302, d.Plane1.HandleOrDescriptor);
            Assert.Equal((nuint)960, d.Plane1.RowPitchBytes);
            Assert.Equal(0UL, d.Plane1.Modifier);
            Assert.Equal(vkHandleType, d.Plane1.ExternalMemoryHandleType);
        }
        finally
        {
            VulkanExternalNv12Interop.FreeToken(token);
        }
    }

    [Fact]
    public void VulkanExternalNv12Interop_OnHost_shared_allocation_exposes_uv_byte_offset_in_plane1_modifier()
    {
        if (!IsVulkanInteropTestHost())
            return;

        IHardwareVideoInterop h = new VulkanExternalNv12Interop();
        const uint vkHandleType = 7;
        var token = VulkanExternalNv12Interop.AllocToken((nint)900, 0, vkHandleType, 640, 480, 640, 640, uvPlaneByteOffset: 614_400);
        try
        {
            Assert.True(h.TryDescribeImportedSurface(token, out var d));
            Assert.Equal((nint)900, d.Plane0.HandleOrDescriptor);
            Assert.Equal((nint)900, d.Plane1.HandleOrDescriptor);
            Assert.Equal(614_400UL, d.Plane1.Modifier);
        }
        finally
        {
            VulkanExternalNv12Interop.FreeToken(token);
        }
    }

    [Fact]
    public void VulkanExternalNv12Interop_OnHost_zero_y_handle_throws()
    {
        if (!IsVulkanInteropTestHost())
            return;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VulkanExternalNv12Interop.AllocToken(0, (nint)1, 1, 8, 8, 8, 8));
    }

    [Fact]
    public void VulkanExternalNv12Interop_OnHost_zero_external_type_throws()
    {
        if (!IsVulkanInteropTestHost())
            return;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VulkanExternalNv12Interop.AllocToken((nint)1, 0, 0, 8, 8, 8, 8));
    }

    [Fact]
    public void VulkanExternalNv12Interop_OnHost_TryDescribe_zero_token_returns_false()
    {
        if (!IsVulkanInteropTestHost())
            return;

        IHardwareVideoInterop h = new VulkanExternalNv12Interop();
        Assert.False(h.TryDescribeImportedSurface(0, out var d));
        Assert.Equal(default, d);
    }

    [Fact]
    public void VulkanExternalNv12Interop_OnHost_wrong_token_type_returns_false()
    {
        if (!IsVulkanInteropTestHost())
            return;

        var wrong = GCHandle.Alloc("not-a-vulkan-token", GCHandleType.Normal);
        try
        {
            IHardwareVideoInterop h = new VulkanExternalNv12Interop();
            Assert.False(h.TryDescribeImportedSurface(GCHandle.ToIntPtr(wrong), out var d));
            Assert.Equal(default, d);
        }
        finally
        {
            wrong.Free();
        }
    }

    [Fact]
    public void MetalIosurfaceNv12Interop_AllocToken_Throws_OnNonApple()
    {
        if (IsAppleHardwareVideoOs())
            return;

        Assert.Throws<PlatformNotSupportedException>(() =>
            MetalIosurfaceNv12Interop.AllocToken((nint)1, 0, 64, 64, 64, 64));
    }

    [Fact]
    public void MetalIosurfaceNv12Interop_NotApple_DisablesImports()
    {
        if (IsAppleHardwareVideoOs())
            return;

        IHardwareVideoInterop h = new MetalIosurfaceNv12Interop();
        Assert.False(h.IsGpuImportSupported);
        Assert.False(h.TryDescribeImportedSurface((nint)1, out var d));
        Assert.Equal(default, d);
    }

    [Fact]
    public void MetalIosurfaceNv12Interop_OnApple_describes_nv12_from_token()
    {
        if (!IsAppleHardwareVideoOs())
            return;

        IHardwareVideoInterop h = new MetalIosurfaceNv12Interop();
        Assert.True(h.IsGpuImportSupported);

        var token = MetalIosurfaceNv12Interop.AllocToken((nint)0x5A5, (nint)0xB6B, 800, 600, 800, 800);
        try
        {
            Assert.True(h.TryDescribeImportedSurface(token, out var d));
            Assert.Equal(800, d.WidthPixels);
            Assert.Equal(600, d.HeightPixels);
            Assert.Equal(2, (int)d.PlaneCount);
            Assert.Equal(HardwareVideoMemoryKind.MetalIoSurface, d.Plane0.Kind);
            Assert.Equal((nint)0x5A5, d.Plane0.HandleOrDescriptor);
            Assert.Equal(HardwareVideoMemoryKind.MetalIoSurface, d.Plane1.Kind);
            Assert.Equal((nint)0xB6B, d.Plane1.HandleOrDescriptor);
        }
        finally
        {
            MetalIosurfaceNv12Interop.FreeToken(token);
        }
    }

    [Fact]
    public void MetalIosurfaceNv12Interop_OnApple_zero_uv_surface_reuses_luma()
    {
        if (!IsAppleHardwareVideoOs())
            return;

        IHardwareVideoInterop h = new MetalIosurfaceNv12Interop();
        var token = MetalIosurfaceNv12Interop.AllocToken((nint)77, 0, 320, 240, 320, 320);
        try
        {
            Assert.True(h.TryDescribeImportedSurface(token, out var d));
            Assert.Equal((nint)77, d.Plane0.HandleOrDescriptor);
            Assert.Equal((nint)77, d.Plane1.HandleOrDescriptor);
        }
        finally
        {
            MetalIosurfaceNv12Interop.FreeToken(token);
        }
    }

    [Fact]
    public void MetalIosurfaceNv12Interop_OnApple_zero_iosurface_throws()
    {
        if (!IsAppleHardwareVideoOs())
            return;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MetalIosurfaceNv12Interop.AllocToken(0, (nint)1, 10, 10, 1, 1));
    }
}
