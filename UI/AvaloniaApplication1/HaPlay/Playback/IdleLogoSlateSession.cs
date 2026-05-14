using System.Diagnostics.CodeAnalysis;
using System.Text;
using Avalonia.Threading;
using HaPlay.Models;
using HaPlay.ViewModels;
using S.Media.Core.Video;
using S.Media.FFmpeg.Video;
using S.Media.NDI;
using S.Media.NDI.Video;
using S.Media.SDL3;

namespace HaPlay.Playback;

/// <summary>
/// Shows the fallback image on selected video outputs when no <see cref="MediaPlayer"/> session is open
/// (NDI + SDL3 OpenGL, same routing rules as playback).
/// </summary>
internal sealed class IdleLogoSlateSession : IDisposable
{
    private readonly List<LogoFallbackVideoSink> _logos = new();
    private readonly Dictionary<Guid, NDIOutput> _ndiByDefinitionId = new();
    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _frameDuration;
    private long _frameIndex;
    private bool _disposed;

    private IdleLogoSlateSession(IReadOnlyList<LogoFallbackVideoSink> logos, Dictionary<Guid, NDIOutput> ndiById,
        TimeSpan frameDuration)
    {
        _logos.AddRange(logos);
        foreach (var kv in ndiById)
            _ndiByDefinitionId[kv.Key] = kv.Value;
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
        foreach (var logo in _logos)
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
            .Where(static l => l.UseInMediaPlayer && l.SupportsMediaPlayerRouting)
            .ToList();

        if (lines.Count == 0)
        {
            errorMessage = "Select at least one video output (NDI or SDL3) for the slate.";
            return false;
        }

        var hasNdi = lines.Exists(static l =>
            l.Definition is NdiOutputDefinition nd && nd.StreamMode != NdiOutputStreamMode.AudioOnly);

        VideoFrame? ndiProto = null;
        var ndiVideoFormat = new VideoFormat(1920, 1080, PixelFormat.Bgra32, new Rational(60, 1));
        if (hasNdi)
        {
            var (w, h) = DefaultNdiSlateSize(lines);
            ndiVideoFormat = new VideoFormat(w, h, PixelFormat.Bgra32, new Rational(60, 1));
            ndiProto = FallbackImageLoader.TryBuildHoldCpuFrame(ndiVideoFormat, imagePath);
            if (ndiProto is null)
            {
                errorMessage = "Could not load or convert the image for NDI slate resolution.";
                return false;
            }
        }

        var ndiById = new Dictionary<Guid, NDIOutput>();
        foreach (var line in lines)
        {
            if (line.Definition is not NdiOutputDefinition nd)
                continue;
            if (nd.StreamMode == NdiOutputStreamMode.AudioOnly)
                continue;
            if (ndiById.ContainsKey(nd.Id))
                continue;
            ndiById[nd.Id] = CreateNdiOutput(nd);
        }

        var logos = new List<LogoFallbackVideoSink>();
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
                        var sdl = new SDL3GLVideoSink(lv.DisplayName, sw, sh);
                        var logo = new LogoFallbackVideoSink(sdl, disposeInnerOnDispose: true);
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

                        logos.Add(logo);
                        break;
                    }
                    case NdiOutputDefinition nd when nd.StreamMode != NdiOutputStreamMode.AudioOnly:
                    {
                        var ndi = ndiById[nd.Id];
                        var pump = new VideoSinkPump(ndi.VideoSink, maxQueuedFrames: 8, name: $"idle-ndi-{nd.Id:N}",
                            log: null, disposeInnerOnDispose: false);
                        var logo = new LogoFallbackVideoSink(pump, disposeInnerOnDispose: true);
                        logo.Configure(ndiVideoFormat);
                        logo.TrySetHoldTemplate(FallbackImageLoader.CloneHoldTemplate(ndiProto!));
                        logos.Add(logo);
                        break;
                    }
                }
            }

            if (logos.Count == 0)
            {
                errorMessage = "No NDI or SDL3 video outputs are selected.";
                foreach (var ndi in ndiById.Values)
                {
                    try
                    {
                        ndi.Dispose();
                    }
                    catch
                    {
                        /* best effort */
                    }
                }

                ndiProto?.Dispose();
                return false;
            }

            ndiProto?.Dispose();
            session = new IdleLogoSlateSession(logos, ndiById, TimeSpan.FromSeconds(1.0 / 30.0));
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            foreach (var logo in logos)
            {
                try
                {
                    logo.Dispose();
                }
                catch
                {
                    /* best effort */
                }
            }

            foreach (var ndi in ndiById.Values)
            {
                try
                {
                    ndi.Dispose();
                }
                catch
                {
                    /* best effort */
                }
            }

            ndiProto?.Dispose();
            return false;
        }
    }

    /// <summary>Stable key so the UI can skip rebuilding when nothing relevant changed.</summary>
    public static string BuildSignature(
        bool holdFallback,
        string? fallbackPath,
        IEnumerable<OutputLineViewModel> outputs)
    {
        var sb = new StringBuilder();
        sb.Append(holdFallback ? '1' : '0').Append('|').Append(fallbackPath).Append('|');
        foreach (var line in outputs)
        {
            if (!line.SupportsMediaPlayerRouting)
                continue;
            sb.Append(line.Definition.Id).Append('=').Append(line.UseInMediaPlayer ? '1' : '0').Append(';');
        }

        return sb.ToString();
    }

    private static (int W, int H) DefaultNdiSlateSize(List<OutputLineViewModel> lines)
    {
        foreach (var line in lines)
        {
            if (line.Definition is LocalVideoOutputDefinition lv && lv.Engine == VideoOutputEngine.SdlOpenGl)
                return InitialSdlSize(lv);
        }

        return (1920, 1080);
    }

    private static (int W, int H) InitialSdlSize(LocalVideoOutputDefinition d)
    {
        if (d.SurfaceMode == VideoSurfaceMode.Windowed && d.WindowWidth is { } w && d.WindowHeight is { } h)
            return (w, h);
        return (1280, 720);
    }

    private static NDIOutput CreateNdiOutput(NdiOutputDefinition nd)
    {
        var mode = nd.StreamMode;
        var clockV = mode != NdiOutputStreamMode.AudioOnly;
        var clockA = mode != NdiOutputStreamMode.VideoOnly;
        var tc = mode == NdiOutputStreamMode.VideoAndAudio
            ? NDIVideoTimecodeMode.PresentationRelativeTicks
            : NDIVideoTimecodeMode.Synthesize;
        var groups = string.IsNullOrWhiteSpace(nd.Groups) ? null : nd.Groups;
        return new NDIOutput(nd.SourceName, groups, clockV, clockA, null, tc);
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

        foreach (var logo in _logos)
        {
            try
            {
                logo.Dispose();
            }
            catch
            {
                /* best effort */
            }
        }

        _logos.Clear();

        foreach (var ndi in _ndiByDefinitionId.Values)
        {
            try
            {
                ndi.Dispose();
            }
            catch
            {
                /* best effort */
            }
        }

        _ndiByDefinitionId.Clear();
    }
}
