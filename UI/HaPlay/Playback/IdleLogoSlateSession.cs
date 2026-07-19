using System.Diagnostics.CodeAnalysis;
using System.Text;
using HaPlay.ViewModels;
using S.Media.Core.Video;

namespace HaPlay.Playback;

/// <summary>
/// Shows the user-supplied fallback image on selected video outputs when no <see cref="MediaPlayer"/> session
/// is open. For NDI outputs, the image is installed on the persistent <c>NDIOutputPreviewRuntime</c> carrier
/// (via <see cref="OutputManagementViewModel.SetNDICarrierLogo"/>) so receivers see the slate over the same
/// sender they were already locked onto - no NDI re-discovery. For local video outputs, the line's PERSISTENT
/// window/sink is acquired (single-holder, same seam playback uses) and the image is submitted once - previews
/// stay alive across playback in the current output model (<c>StopPreviewsForPlayback</c> is a no-op), so the
/// old approach of creating a dedicated slate window put a SECOND window next to the real output instead of
/// showing the image on it. A line currently held by a playback session is skipped; the periodic
/// <c>SyncIdleSlate</c> re-sync picks it up once it frees.
/// </summary>
internal sealed class IdleLogoSlateSession : IDisposable
{
    private readonly List<StaticSlateVideoOutput> _localLogos = new();
    private readonly List<OutputLineViewModel> _localLines = new();
    private readonly List<OutputLineViewModel> _ndiLines = new();
    private readonly OutputManagementViewModel _outputs;
    private bool _disposed;

    private IdleLogoSlateSession(
        IReadOnlyList<StaticSlateVideoOutput> localLogos,
        IReadOnlyList<OutputLineViewModel> localLines,
        IReadOnlyList<OutputLineViewModel> ndiLines,
        OutputManagementViewModel outputs)
    {
        _localLogos.AddRange(localLogos);
        _localLines.AddRange(localLines);
        _ndiLines.AddRange(ndiLines);
        _outputs = outputs;
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

        var localLogos = new List<StaticSlateVideoOutput>();
        var localAcquired = new List<OutputLineViewModel>();
        var ndiInstalled = new List<OutputLineViewModel>();
        try
        {
            foreach (var line in lines)
            {
                switch (line.Definition)
                {
                    case LocalVideoOutputDefinition lv:
                    {
                        // Acquire the line's PERSISTENT window/sink (the same single-holder seam playback
                        // uses) and pump the image into it. Null ⇒ the line is held by a playback session or
                        // its preview isn't running - skip it; the periodic slate re-sync retries later.
                        var sink = repository.TryAcquireLocalVideoOutputForPlayback(line);
                        if (sink is null)
                            break;

                        var logo = new StaticSlateVideoOutput(sink);
                        try
                        {
                            var (sw, sh) = InitialLocalSize(lv);
                            var slateFmt = new VideoFormat(sw, sh, PixelFormat.Bgra32, new Rational(30, 1));
                            logo.Configure(slateFmt);
                            var proto = FallbackImageLoader.TryBuildHoldCpuFrame(slateFmt, imagePath)
                                ?? throw new InvalidOperationException(
                                    "Slate image conversion failed for the output size.");
                            try
                            {
                                logo.SetTemplate(FallbackImageLoader.CloneHoldTemplate(proto));
                            }
                            finally
                            {
                                proto.Dispose();
                            }

                            // Local outputs retain the uploaded texture and redraw it when exposed. Re-submitting
                            // the same pixels at 30 fps only repeats the CPU-to-GPU upload (about
                            // 249 MB/s for a 1080p BGRA frame).
                            logo.Submit();
                        }
                        catch
                        {
                            logo.Dispose(); // wrapper only - the borrowed sink stays alive
                            repository.ReleaseLocalVideoOutputForPlayback(line);
                            throw;
                        }

                        localLogos.Add(logo);
                        localAcquired.Add(line);
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

            if (localLogos.Count == 0 && ndiInstalled.Count == 0)
            {
                errorMessage = "No NDI or local video outputs are selected (or all are in use).";
                ndiProto?.Dispose();
                return false;
            }

            ndiProto?.Dispose();
            session = new IdleLogoSlateSession(
                localLogos, localAcquired, ndiInstalled, repository);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            foreach (var logo in localLogos)
            {
                try { logo.Dispose(); }
                catch { /* best effort */ }
            }

            foreach (var line in localAcquired)
            {
                try { repository.ReleaseLocalVideoOutputForPlayback(line); }
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

    private static (int W, int H) InitialLocalSize(LocalVideoOutputDefinition d)
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

        foreach (var logo in _localLogos)
        {
            try { logo.Dispose(); } // wrapper only - the borrowed persistent sink stays alive
            catch { /* best effort */ }
        }

        _localLogos.Clear();

        // Release the acquired lines so they return to their idle preview (and a playback session can
        // acquire them next - the deck's open path calls StopIdleSlate BEFORE it acquires).
        foreach (var line in _localLines)
        {
            try { _outputs.ReleaseLocalVideoOutputForPlayback(line); }
            catch { /* best effort */ }
        }

        _localLines.Clear();

        foreach (var line in _ndiLines)
        {
            try { _outputs.SetNDICarrierLogo(line, null); }
            catch { /* best effort */ }
        }

        _ndiLines.Clear();
    }
}
