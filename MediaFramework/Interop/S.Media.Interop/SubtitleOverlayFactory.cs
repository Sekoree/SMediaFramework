using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.Subtitles;

namespace S.Media.Interop;

/// <summary>
/// The unified subtitle factory: builds an <see cref="IVideoOverlaySource"/> from any subtitle source - a sidecar
/// file (SRT/VTT/MicroDVD/SAMI/SubViewer/ASS/…) or a media container carrying a subtitle stream - all rendered
/// through libass. Sidecar ASS/SSA goes straight to libass; every other text format and in-container stream is
/// decoded to ASS events by FFmpeg first.
/// </summary>
/// <remarks>
/// Host glue: it pairs the FFmpeg decoder with the libass renderer so the session and the subtitle library stay
/// decoupled (neither references the other). Wire <see cref="FromFile"/> into <c>ShowSession</c> as its subtitle
/// factory delegate so a clip's selected subtitle streams auto-attach as layers. Use
/// <see cref="FromFileDeferred"/> on a session dispatcher so container scanning does not block cue startup.
/// </remarks>
public static class SubtitleOverlayFactory
{
    /// <summary>
    /// Creates an overlay source for <paramref name="path"/> at the composition canvas size, or <c>null</c> when
    /// the file is missing, carries no decodable text subtitle, or is a bitmap subtitle.
    /// </summary>
    public static IVideoOverlaySource? FromFile(string path, int width, int height, int streamIndex = -1,
        SubtitleStyleOverride? style = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        // Sidecar ASS/SSA renders directly - no FFmpeg round-trip.
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (streamIndex < 0 && ext is (".ass" or ".ssa"))
            return SubtitleSourceFactory.FromFile(path, width, height, style);

        try
        {
            return FFmpegSubtitleStreamProbe.Probe(path, streamIndex) switch
            {
                FFmpegSubtitleStreamKind.Text => OpenText(path, width, height, streamIndex, style),
                FFmpegSubtitleStreamKind.Bitmap => OpenBitmap(path, streamIndex),
                _ => null,
            };
        }
        catch
        {
            return null; // unopenable, or no subtitle stream
        }
    }

    /// <summary>
    /// Creates an overlay whose probe/decode work runs on the thread pool. Until loading completes,
    /// <see cref="IVideoOverlaySource.RenderAt"/> returns no frame.
    /// </summary>
    public static IVideoOverlaySource? FromFileDeferred(
        string path, int width, int height, int streamIndex = -1, SubtitleStyleOverride? style = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        return new DeferredSubtitleOverlaySource(() => FromFile(path, width, height, streamIndex, style));
    }

    private static IVideoOverlaySource? OpenText(string path, int width, int height, int streamIndex,
        SubtitleStyleOverride? style)
    {
        var track = FFmpegSubtitleDecoder.Decode(path, streamIndex);
        if (track.Events.Count == 0)
            return null;

        var events = track.Events.Select(e => new AssEventChunk(e.Body, e.StartMs, e.DurationMs)).ToList();
        var fonts = track.Fonts.Select(f => new AssFontAttachment(f.Name, f.Data)).ToList();
        return new AssSubtitleLayerSource(width, height, track.Header, events, fonts, style: style);
    }

    private static IVideoOverlaySource? OpenBitmap(string path, int streamIndex)
    {
        var bitmap = FFmpegBitmapSubtitleDecoder.Decode(path, streamIndex);
        return bitmap.Cues.Count > 0 ? new BitmapSubtitleLayerSource(bitmap) : null;
    }

    private sealed class DeferredSubtitleOverlaySource : IVideoOverlaySource
    {
        private readonly object _gate = new();
        private IVideoOverlaySource? _source;
        private bool _disposed;

        public DeferredSubtitleOverlaySource(Func<IVideoOverlaySource?> load)
        {
            _ = Task.Run(load).ContinueWith(
                CompleteLoad,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public VideoFrame? RenderAt(TimeSpan position)
        {
            lock (_gate)
                return _disposed ? null : _source?.RenderAt(position);
        }

        public void Dispose()
        {
            IVideoOverlaySource? source;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                source = _source;
                _source = null;
            }
            source?.Dispose();
        }

        private void CompleteLoad(Task<IVideoOverlaySource?> task)
        {
            if (!task.IsCompletedSuccessfully)
            {
                _ = task.Exception;
                return;
            }

            IVideoOverlaySource? dispose = null;
            lock (_gate)
            {
                if (_disposed)
                    dispose = task.Result;
                else
                    _source = task.Result;
            }
            dispose?.Dispose();
        }
    }
}
