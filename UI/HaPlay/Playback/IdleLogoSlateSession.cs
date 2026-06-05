using System.Diagnostics.CodeAnalysis;
using System.Text;
using Avalonia.Threading;
using HaPlay.ViewModels;
using S.Media.Core.Video;
using S.Media.SDL3;

namespace HaPlay.Playback;

/// <summary>
/// Shows the user-supplied fallback image on selected video outputs when no <see cref="MediaPlayer"/> session
/// is open. For NDI outputs, the image is installed on the persistent <c>NDIOutputPreviewRuntime</c> carrier
/// (via <see cref="OutputManagementViewModel.SetNDICarrierLogo"/>) so receivers see the slate over the same
/// sender they were already locked onto — no NDI re-discovery. For SDL3 OpenGL outputs, a dedicated logo
/// output + pump is created here.
/// </summary>
internal sealed class IdleLogoSlateSession : IDisposable
{
    private readonly List<LogoFallbackVideoOutput> _sdlLogos = new();
    private readonly List<OutputLineViewModel> _ndiLines = new();
    private readonly OutputManagementViewModel _outputs;
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _frameDuration;
    private long _frameIndex;
    private bool _disposed;

    private IdleLogoSlateSession(IReadOnlyList<LogoFallbackVideoOutput> sdlLogos, IReadOnlyList<OutputLineViewModel> ndiLines,
        OutputManagementViewModel outputs, TimeSpan frameDuration)
    {
        _sdlLogos.AddRange(sdlLogos);
        _ndiLines.AddRange(ndiLines);
        _outputs = outputs;
        _frameDuration = frameDuration;
        _timer = new DispatcherTimer { Interval = frameDuration };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        _frameIndex++;
        var pt = TimeSpan.FromTicks(checked(_frameIndex * _frameDuration.Ticks));
        foreach (var logo in _sdlLogos)
        {
            try
            {
                logo.SubmitTemplateFrame(pt);
            }
            catch
            {
                /* best effort */
            }
        }
    }

    public static bool TryStart(
        IReadOnlyList<OutputLineViewModel> selectedOutputs,
        OutputManagementViewModel repository,
        string imagePath,
        [NotNullWhen(true)] out IdleLogoSlateSession? session,
        out string? errorMessage)
    {
        session = null;
        errorMessage = null;
        repository.StopPreviewsForPlayback(selectedOutputs);

        var lines = selectedOutputs
            .Where(static l => l.SupportsMediaPlayerRouting)
            .ToList();

        if (lines.Count == 0)
        {
            errorMessage = "Select at least one video output (NDI or SDL3) for the slate.";
            return false;
        }

        var ndiLines = lines
            .Where(static l => l.Definition is NDIOutputDefinition nd && nd.StreamMode != NDIOutputStreamMode.AudioOnly)
            .ToList();

        // Build the NDI logo template once at the carrier's negotiated format (1920×1080 BGRA32 to match
        // NDIOutputPreviewRuntime). Each carrier owns its own template copy via SetNDICarrierLogo.
        VideoFrame? ndiProto = null;
        if (ndiLines.Count > 0)
        {
            var ndiVideoFormat = new VideoFormat(1920, 1080, PixelFormat.Bgra32, new Rational(30, 1));
            ndiProto = FallbackImageLoader.TryBuildHoldCpuFrame(ndiVideoFormat, imagePath);
            if (ndiProto is null)
            {
                errorMessage = "Could not load or convert the image for NDI slate resolution.";
                return false;
            }
        }

        var sdlLogos = new List<LogoFallbackVideoOutput>();
        var ndiInstalled = new List<OutputLineViewModel>();
        try
        {
            foreach (var line in lines)
            {
                switch (line.Definition)
                {
                    case LocalVideoOutputDefinition lv when lv.Engine == VideoOutputEngine.SdlOpenGl:
                    {
                        var (sw, sh) = InitialSdlSize(lv);
                        var sdlFmt = new VideoFormat(sw, sh, PixelFormat.Bgra32, new Rational(60, 1));
                        var sdl = new SDL3GLVideoOutput(lv.DisplayName, sw, sh);
                        var logo = new LogoFallbackVideoOutput(sdl, disposeInnerOnDispose: true);
                        logo.Configure(sdlFmt);
                        var sdlProto = FallbackImageLoader.TryBuildHoldCpuFrame(sdlFmt, imagePath);
                        if (sdlProto is null)
                            throw new InvalidOperationException("Slate image conversion failed for SDL output size.");
                        try
                        {
                            logo.TrySetHoldTemplate(FallbackImageLoader.CloneHoldTemplate(sdlProto));
                        }
                        finally
                        {
                            sdlProto.Dispose();
                        }

                        sdlLogos.Add(logo);
                        break;
                    }
                    case NDIOutputDefinition nd when nd.StreamMode != NDIOutputStreamMode.AudioOnly:
                    {
                        repository.SetNDICarrierLogo(line, FallbackImageLoader.CloneHoldTemplate(ndiProto!));
                        ndiInstalled.Add(line);
                        break;
                    }
                }
            }

            if (sdlLogos.Count == 0 && ndiInstalled.Count == 0)
            {
                errorMessage = "No NDI or SDL3 video outputs are selected.";
                ndiProto?.Dispose();
                return false;
            }

            ndiProto?.Dispose();
            session = new IdleLogoSlateSession(sdlLogos, ndiInstalled, repository, TimeSpan.FromSeconds(1.0 / 30.0));
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            foreach (var logo in sdlLogos)
            {
                try { logo.Dispose(); }
                catch { /* best effort */ }
            }

            foreach (var line in ndiInstalled)
            {
                try { repository.SetNDICarrierLogo(line, null); }
                catch { /* best effort */ }
            }

            ndiProto?.Dispose();
            return false;
        }
    }

    /// <summary>Stable key so the UI can skip rebuilding when nothing relevant changed.</summary>
    public static string BuildSignature(
        bool holdFallback,
        string? fallbackPath,
        IEnumerable<OutputLineViewModel> selectedOutputs)
    {
        var sb = new StringBuilder();
        sb.Append(holdFallback ? '1' : '0').Append('|').Append(fallbackPath).Append('|');
        foreach (var line in selectedOutputs)
        {
            if (!line.SupportsMediaPlayerRouting)
                continue;
            sb.Append(line.Definition.Id).Append(';');
        }

        return sb.ToString();
    }

    private static (int W, int H) InitialSdlSize(LocalVideoOutputDefinition d)
    {
        if (d.SurfaceMode == VideoSurfaceMode.Windowed && d.WindowWidth is { } w && d.WindowHeight is { } h)
            return (w, h);
        return (1280, 720);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
        }
        catch
        {
            /* best effort */
        }

        foreach (var logo in _sdlLogos)
        {
            try { logo.Dispose(); }
            catch { /* best effort */ }
        }

        _sdlLogos.Clear();

        foreach (var line in _ndiLines)
        {
            try { _outputs.SetNDICarrierLogo(line, null); }
            catch { /* best effort */ }
        }

        _ndiLines.Clear();
    }
}
