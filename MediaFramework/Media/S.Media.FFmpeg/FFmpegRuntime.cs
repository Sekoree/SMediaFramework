using System.Threading;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Video;
using CorePixelFormat = S.Media.Core.Video.PixelFormat;

namespace S.Media.FFmpeg;

/// <summary>
/// One-time FFmpeg native binding initialization. Safe to call multiple times.
/// </summary>
/// <remarks>
/// FFmpeg.AutoGen 8.x routes every <c>av_*</c> call through a function-pointer
/// table that must be populated up-front; without this you get
/// <see cref="NotSupportedException"/> from every API call. On Linux/macOS we
/// default <see cref="ffmpeg.RootPath"/> to empty so the system loader finds
/// the libs in standard paths (e.g. <c>/usr/lib/libavformat.so.62</c>); on
/// Windows the AutoGen default (the executable directory) is left alone so
/// the FFmpeg.AutoGen.Redist.windows.x64 companion package keeps working.
/// <para>
/// The <strong>first</strong> successful initialization wins: later calls are
/// cheap no-ops, but a non-null <paramref name="rootPath"/> that disagrees with
/// the already-configured native search path is ignored (logged once at warning
/// level). Hot-swapping to a different FFmpeg build directory requires a new
/// process.
/// </para>
/// </remarks>
public static class FFmpegRuntime
{
    private static readonly Lock Gate = new();
    private static volatile bool _initialized;
    private static int _ignoredRootPathLogged;

    /// <summary>
    /// Initializes the dynamic bindings. Optionally override the lookup path
    /// for native libraries (e.g. a custom FFmpeg build directory).
    /// </summary>
    public static void EnsureInitialized(string? rootPath = null)
    {
        if (_initialized)
        {
            MaybeLogIgnoredRootPath(rootPath);
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                MaybeLogIgnoredRootPath(rootPath);
                return;
            }

            if (rootPath is not null)
                ffmpeg.RootPath = rootPath;
            else if (OperatingSystem.IsLinux())
                ffmpeg.RootPath = "";

            DynamicallyLoadedBindings.Initialize();

            // Install the swresample-backed wrapper for AudioRouter.AddSource(autoResample: true).
            // The factory contract is that the caller (the router) owns the wrapper's lifecycle,
            // but the original source belongs to whoever passed it in — pass
            // disposeInnerWhenDisposed: false so the router-disposed wrapper never reaches into
            // the caller's source. Last write wins on the static slot; other resampler packages
            // (if any) can replace it after FFmpegRuntime.EnsureInitialized() returns.
            AudioRouterAutoResample.SourceWrapper ??=
                (inner, targetRate) => new ResamplingAudioSource(inner, targetRate, disposeInnerWhenDisposed: false);

            // Install the swscale-backed CPU video converter so the Core VideoRouter can do branch
            // pixel conversion without referencing FFmpeg. Last write wins on the static slots —
            // other packages can replace these after FFmpegRuntime.EnsureInitialized() returns.
            VideoCpuFrameConverterRegistry.Factory ??= () => new VideoCpuFrameConverter();
            VideoCpuFrameConverterRegistry.CanConvertProbe ??= VideoCpuFrameConverter.CanConvert;

            // Install yadif-based deinterlacer so Core consumers get it via
            // VideoDeinterlacerRegistry.Create(input) when they reference S.Media.FFmpeg. Any
            // YadifDeinterlacer-supported planar layout (I420 / NV12 / Yuv422P / Yuv444P) routes
            // through libavfilter; anything else falls back to BobDeinterlacer.
            VideoDeinterlacerRegistry.Factory ??= input =>
            {
                if (input.PixelFormat is CorePixelFormat.I420
                    or CorePixelFormat.Nv12
                    or CorePixelFormat.Yuv422P
                    or CorePixelFormat.Yuv444P)
                    return new YadifDeinterlacer(input);
                return new BobDeinterlacer(input);
            };

            _initialized = true;
        }
    }

    private static void MaybeLogIgnoredRootPath(string? requested)
    {
        if (requested is null)
            return;

        var current = ffmpeg.RootPath ?? "";
        if (string.Equals(current, requested, StringComparison.Ordinal))
            return;

        if (Interlocked.Exchange(ref _ignoredRootPathLogged, 1) != 0)
            return;

        MediaDiagnostics.LogWarning(
            "FFmpegRuntime.EnsureInitialized: bindings already initialized (RootPath '{0}'); ignoring requested rootPath '{1}'. Use a new process to load a different native FFmpeg build.",
            current,
            requested);
    }
}
