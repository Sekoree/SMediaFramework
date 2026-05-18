using System.Threading;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg.Audio;

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
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
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
