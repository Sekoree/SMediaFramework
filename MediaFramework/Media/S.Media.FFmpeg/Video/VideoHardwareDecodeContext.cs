using System.Runtime.InteropServices;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace S.Media.FFmpeg.Video;

/// <summary>Optional flags for <see cref="VideoFileDecoder.Open(string, VideoDecoderOpenOptions?)"/>.</summary>
/// <remarks>
/// <para>
/// <see cref="DecoderThreadCount"/> configures one libav <c>AVCodecContext</c> (applied in <see cref="VideoFileDecoder.Open(string, VideoDecoderOpenOptions?)"/>).
/// Multi-instance decode, process-wide caps, or hardware vs software fan-out are host concerns (audio-side parallel notes: <c>AudioFileDecoderOpenOptions</c>).
/// </para>
/// </remarks>
public sealed class VideoDecoderOpenOptions
{
    /// <summary>When true, try libav hardware acceleration before falling back to software decode (default).</summary>
    public bool TryHardwareAcceleration { get; init; } = true;

    /// <summary>
    /// FFmpeg decoder thread count for frame/slice threading on software decode.
    /// Zero picks a bounded default from <see cref="Environment.ProcessorCount"/> (ignored when hardware decode is active).
    /// </summary>
    public int DecoderThreadCount { get; init; }

    /// <summary>
    /// Hardware device types to try in order. When empty, a platform default list is used
    /// (VAAPI on Linux, D3D11VA / QSV on Windows).
    /// </summary>
    public IReadOnlyList<HardwareVideoDeviceType> PreferredDeviceTypes { get; init; } = [];

    /// <summary>
    /// When <see cref="TryHardwareAcceleration"/> is true on Linux and the codec advertises DRM PRIME,
    /// keep decode on dma-bufs (no libav CPU <c>av_hwframe_transfer_data</c> copy) for EGL / GL upload.
    /// </summary>
    public bool RetainDmabufForGl { get; init; }

    /// <summary>
    /// When <see cref="TryHardwareAcceleration"/> is true on Windows with D3D11VA and the codec advertises
    /// <c>AV_PIX_FMT_D3D11</c>, keep libav output on D3D11 surfaces and export DXGI NT shared handles for GL / interop.
    /// </summary>
    public bool RetainD3D11SharedHandleForGl { get; init; }

    /// <summary>
    /// When true together with <see cref="RetainD3D11SharedHandleForGl"/>, build <see cref="VideoWin32Nv12Backing"/>
    /// from DXGI NT shared handles only (omit non-owning libav <c>ID3D11Device</c> / <c>ID3D11Texture2D</c> COM pointers on the backing).
    /// GL import then uses <c>OpenSharedResource</c> on a host-owned D3D11 device (e.g. SDL <c>D3D11GlInteropDeviceHost</c> or
    /// <see cref="IVideoSinkD3D11GlBorrowSetup"/>). Incompatible with lazy true zero-host that binds the uploader solely from the first frame's
    /// <c>LibavD3D11DeviceComPtr</c> — keep SDL's interop device or a pre-bound renderer device when this is enabled.
    /// </summary>
    /// <remarks>
    /// Same behavior when the environment variable <c>MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY</c> is <c>1</c> or <c>true</c> (with
    /// <see cref="RetainD3D11SharedHandleForGl"/>); see <see cref="IsWin32Nv12SharedHandleOnlyRequested"/>.
    /// </remarks>
    public bool Win32Nv12SharedHandleOnlyExport { get; init; }

    /// <summary>
    /// Returns whether shared-handle-only Win32 NV12 export is requested via <see cref="Win32Nv12SharedHandleOnlyExport"/> or
    /// <c>MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY</c> (<c>1</c> / <c>true</c>). Callers still require <see cref="RetainD3D11SharedHandleForGl"/>.
    /// </summary>
    public static bool IsWin32Nv12SharedHandleOnlyRequested(VideoDecoderOpenOptions? options)
    {
        if (options?.Win32Nv12SharedHandleOnlyExport == true)
            return true;
        var v = Environment.GetEnvironmentVariable("MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY");
        return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Portable hardware device enum (mirrors <see cref="AVHWDeviceType"/>).</summary>
public enum HardwareVideoDeviceType
{
    Vaapi = AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI,
    D3D11Va = AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
    Dxva2 = AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2,
    Qsv = AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
    Cuda = AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
    Vulkan = AVHWDeviceType.AV_HWDEVICE_TYPE_VULKAN,
}

/// <summary>Libav hardware decode helper: device context, <c>get_format</c> hook, and CPU transfer scratch.</summary>
/// <remarks>
/// <para>
/// <see cref="Dispose"/> frees the scratch <c>AVFrame</c>, hardware device buffer ref, and the pinning <see cref="GCHandle"/> in order.
/// <strong>Debug</strong> builds log per-step failures via <see cref="MediaDiagnostics.LogError"/>; <strong>Release</strong> continues best-effort
/// (same policy as <see cref="VideoRouter.Dispose"/>).
/// </para>
/// </remarks>
internal sealed unsafe class VideoHardwareDecodeContext : IDisposable
{
    // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX
    private const int HwConfigMethodHwDeviceCtx = 0x01;

    private GCHandle _self;
    private AVBufferRef* _deviceRef;
    private AVPixelFormat _hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVFrame* _swScratch;
    private bool _disposed;

    private VideoHardwareDecodeContext(AVBufferRef* deviceRef, AVPixelFormat hwPixFmt)
    {
        _deviceRef = deviceRef;
        _hwPixFmt = hwPixFmt;
        _swScratch = av_frame_alloc();
        if (_swScratch == null)
            throw new OutOfMemoryException("av_frame_alloc (hw scratch)");
    }

    /// <summary>Delegate instance reachable for libav (do not discard).</summary>
    private static readonly AVCodecContext_get_format HwGetFormat = HwGetFormatImpl;

    public static VideoHardwareDecodeContext? TryCreate(AVCodec* codec, AVCodecContext* codecCtx,
        IReadOnlyList<HardwareVideoDeviceType> preferredOrder,
        bool preferLinuxDrmPrimeForGl,
        bool preferWindowsD3D11SharedHandleForGl)
    {
        var order = preferredOrder.Count > 0
            ? preferredOrder
            : DefaultDeviceOrder();

        var dmabufPrefer = preferLinuxDrmPrimeForGl && OperatingSystem.IsLinux();
        var d3d11Prefer = preferWindowsD3D11SharedHandleForGl && OperatingSystem.IsWindows();

        foreach (var devEnum in order)
        {
            var devType = (AVHWDeviceType)devEnum;
            AVBufferRef* devRef = null;
            var ret = av_hwdevice_ctx_create(&devRef, devType, null, null, 0);
            if (ret < 0 || devRef == null)
                continue;

            if (!TryFindHwPixFmt(codec, devType, dmabufPrefer, d3d11Prefer, out var hwPix))
            {
                av_buffer_unref(&devRef);
                continue;
            }

            var ctx = new VideoHardwareDecodeContext(devRef, hwPix);
            ctx._self = GCHandle.Alloc(ctx);
            codecCtx->opaque = (void*)GCHandle.ToIntPtr(ctx._self);
            codecCtx->get_format =
                HwGetFormat;
            codecCtx->hw_device_ctx = av_buffer_ref(devRef);
            return ctx;
        }

        return null;
    }

    internal bool OutputsDrmPrimeGpuFrame =>
        _hwPixFmt == AVPixelFormat.AV_PIX_FMT_DRM_PRIME;

    internal bool OutputsD3D11GpuFrame =>
        _hwPixFmt is AVPixelFormat.AV_PIX_FMT_D3D11 or AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD;

    /// <summary>
    /// Libav's D3D11VA <c>ID3D11Device</c> pointer (COM) from <c>AVHWDeviceContext</c>, when this context uses D3D11VA.
    /// Use for Win32 NV12 GL upload without creating a second D3D11 device in the video sink.
    /// </summary>
    internal nint TryGetD3D11DeviceComPtr()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OutputsD3D11GpuFrame || _deviceRef == null)
            return 0;
        var pData = _deviceRef->data;
        if (pData == null)
            return 0;
        var hw = (AVHWDeviceContext*)pData;
        if (hw->type != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA || hw->hwctx == null)
            return 0;
        var d3d = (AVD3D11VADeviceContext*)hw->hwctx;
        return (nint)d3d->device;
    }

    /// <summary>DXGI adapter LUID for the libav D3D11 device (packed <see langword="long"/>), when D3D11VA is active.</summary>
    internal bool TryGetD3D11AdapterLuid(out long adapterLuidPacked)
    {
        adapterLuidPacked = 0;
        var p = TryGetD3D11DeviceComPtr();
        if (p == 0)
            return false;

        try
        {
            using var dev = new global::Vortice.Direct3D11.ID3D11Device(p);
            using var dxgiDevice = dev.QueryInterfaceOrNull<IDXGIDevice>();
            if (dxgiDevice is null)
                return false;

            using var adapter = dxgiDevice.GetAdapter();
            using var adapter1 = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
            if (adapter1 is null)
                return false;

            var desc = adapter1.Description1;
            adapterLuidPacked = PackLuid(desc.Luid);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long PackLuid(Luid luid) =>
        unchecked((long)(((ulong)(uint)luid.HighPart << 32) | luid.LowPart));

    private static IReadOnlyList<HardwareVideoDeviceType> DefaultDeviceOrder()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return
            [
                HardwareVideoDeviceType.D3D11Va, HardwareVideoDeviceType.Qsv, HardwareVideoDeviceType.Dxva2,
                HardwareVideoDeviceType.Cuda,
            ];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return [HardwareVideoDeviceType.Vaapi, HardwareVideoDeviceType.Vulkan];
        return [HardwareVideoDeviceType.Vaapi];
    }

    private static bool TryFindHwPixFmt(AVCodec* codec, AVHWDeviceType devType, bool preferDrmPrime,
        bool preferWindowsD3D11SharedHandle,
        out AVPixelFormat hwPixFmt)
    {
        hwPixFmt = AVPixelFormat.AV_PIX_FMT_NONE;
        AVPixelFormat drmPrime = AVPixelFormat.AV_PIX_FMT_NONE;
        AVPixelFormat d3d11Out = AVPixelFormat.AV_PIX_FMT_NONE;
        AVPixelFormat other = AVPixelFormat.AV_PIX_FMT_NONE;

        for (var i = 0;; i++)
        {
            var cfg = avcodec_get_hw_config(codec, i);
            if (cfg == null)
                break;

            if ((cfg->methods & HwConfigMethodHwDeviceCtx) == 0)
                continue;

            if (cfg->device_type != devType)
                continue;

            var px = cfg->pix_fmt;
            if (px == AVPixelFormat.AV_PIX_FMT_DRM_PRIME)
                drmPrime = px;
            else if (px is AVPixelFormat.AV_PIX_FMT_D3D11 or AVPixelFormat.AV_PIX_FMT_D3D11VA_VLD)
                d3d11Out = px;
            else if (px != AVPixelFormat.AV_PIX_FMT_NONE && other == AVPixelFormat.AV_PIX_FMT_NONE)
                other = px;
        }

        if (preferDrmPrime && drmPrime != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            hwPixFmt = drmPrime;
            return true;
        }

        if (preferWindowsD3D11SharedHandle && devType == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA &&
            d3d11Out != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            hwPixFmt = d3d11Out;
            return true;
        }

        hwPixFmt = other != AVPixelFormat.AV_PIX_FMT_NONE ? other : drmPrime;
        if (hwPixFmt == AVPixelFormat.AV_PIX_FMT_NONE && d3d11Out != AVPixelFormat.AV_PIX_FMT_NONE)
            hwPixFmt = d3d11Out;
        return hwPixFmt != AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public AVPixelFormat HwAccelPixFmt => _hwPixFmt;

    private static AVPixelFormat HwGetFormatImpl(AVCodecContext* avctx, AVPixelFormat* fmt)
    {
        if (avctx->opaque == null) return AVPixelFormat.AV_PIX_FMT_NONE;
        var h = GCHandle.FromIntPtr((nint)avctx->opaque);
        if (!h.IsAllocated || h.Target is not VideoHardwareDecodeContext ctx)
            return AVPixelFormat.AV_PIX_FMT_NONE;

        var want = ctx._hwPixFmt;
        for (var p = fmt; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == want)
                return *p;
        }
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    /// <summary>CPU copy of a hardware frame — <paramref name="hwFrame"/> stays valid for the caller.</summary>
    public AVFrame* TransferToScratch(AVFrame* hwFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(hwFrame);
        av_frame_unref(_swScratch);
        var ret = av_hwframe_transfer_data(_swScratch, hwFrame, 0);
        FFmpegException.ThrowIfError(ret, nameof(av_hwframe_transfer_data));
        return _swScratch;
    }

    /// <summary>Clear hooks on <paramref name="codecCtx"/> — does not dispose this context (caller still owns).</summary>
    public void DetachFromCodec(AVCodecContext* codecCtx)
    {
        codecCtx->get_format = null;
        codecCtx->opaque = null;
        var hw = codecCtx->hw_device_ctx;
        if (hw != null)
        {
            var tmp = hw;
            av_buffer_unref(&tmp);
            codecCtx->hw_device_ctx = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_swScratch != null)
        {
            try
            {
                var f = _swScratch;
                av_frame_free(&f);
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "VideoHardwareDecodeContext.Dispose: scratch AVFrame");
            }
#else
            catch
            {
            }
#endif
            _swScratch = null;
        }

        if (_deviceRef != null)
        {
            try
            {
                var dev = _deviceRef;
                av_buffer_unref(&dev);
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "VideoHardwareDecodeContext.Dispose: device AVBufferRef");
            }
#else
            catch
            {
            }
#endif
            _deviceRef = null;
        }

        if (_self.IsAllocated)
        {
            try
            {
                _self.Free();
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "VideoHardwareDecodeContext.Dispose: GCHandle.Free");
            }
#else
            catch
            {
            }
#endif
        }
    }
}
