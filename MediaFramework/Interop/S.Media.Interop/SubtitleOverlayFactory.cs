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
        // Streaming decode: the header + embedded fonts are ready at open, so the overlay goes live
        // immediately and a background pump appends events as the demux sweep passes them. The old
        // whole-file FFmpegSubtitleDecoder.Decode here meant a multi-GB movie showed NO subtitles
        // until the entire container had been demuxed (tens of seconds to minutes).
        var reader = FFmpegSubtitleStreamReader.Open(path, streamIndex);
        try
        {
            var fonts = reader.Fonts.Select(f => new AssFontAttachment(f.Name, f.Data)).ToList();

            // The first batch loads synchronously so a caller can render immediately after construction
            // (sidecar files fit entirely in it and keep their old fully-loaded semantics - no pump at
            // all). Only when the stream has MORE events does the background pump take over.
            var events = new List<DecodedSubtitleEvent>(StreamingAssOverlaySource.BatchSize);
            var more = reader.ReadBatch(events, StreamingAssOverlaySource.BatchSize);
            if (!more && events.Count == 0)
            {
                reader.Dispose();
                return null; // no decodable events at all - same contract as the old whole-file decode
            }

            var source = new AssSubtitleLayerSource(width, height, reader.Header, [], fonts, style: style);
            if (!more)
            {
                // Fully loaded in one batch: append directly (original ReadOrders - no seeks can ever
                // collide them) and run without a pump or an open reader.
                source.AppendEvents(events.Select(e => new AssEventChunk(e.Body, e.StartMs, e.DurationMs)).ToList());
                reader.Dispose();
                return source;
            }

            return new StreamingAssOverlaySource(source, reader, events); // takes ownership of both
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Wraps a live <see cref="AssSubtitleLayerSource"/> plus the background pump that feeds it from a
    /// <see cref="FFmpegSubtitleStreamReader"/>. The pump is seek-aware: every render reports its position,
    /// and when one lands outside the swept coverage (<see cref="SubtitleSweepCoverage"/>) the pump seeks
    /// the demuxer near it (minus a preroll) instead of sweeping the whole file sequentially - a playhead
    /// jump deep into a large movie gets its subtitles within the next batches. Because sweeps can overlap
    /// after seeks, the pump dedupes events by content and assigns its own ReadOrder sequence (libass
    /// dedupes on ReadOrder ALONE, and FFmpeg's converted-format decoders reset their counter on the seek
    /// flush - container-assigned or reset ReadOrders would wrongly drop distinct events). Dispose stops
    /// the pump at its next batch boundary; the pump owns the reader and closes it on exit, or earlier the
    /// moment coverage merges to the whole file.
    /// </summary>
    private sealed class StreamingAssOverlaySource : IVideoOverlaySource
    {
        internal const int BatchSize = 256;
        private const long PrerollMs = 30_000;   // re-demux margin before a seek target: events that
                                                 // started up to this much earlier still get caught
        private const long NearAheadMs = 60_000; // positions this close past the frontier just wait for
                                                 // the sweep (MUST exceed PrerollMs or a fresh seek
                                                 // target would keep re-requesting itself)

        private readonly AssSubtitleLayerSource _source;
        private readonly SubtitleSweepCoverage _coverage = new(NearAheadMs);
        private readonly CancellationTokenSource _cts = new();
        // Never disposed on purpose: RenderAt may race pump shutdown, and an un-disposed SemaphoreSlim
        // (without AvailableWaitHandle) holds no unmanaged state.
        private readonly SemaphoreSlim _wake = new(0, 1);
        private long _pendingSeekMs = -1;

        // Pump-only state (the ctor's initial ingest runs before the pump starts, so no contention).
        private readonly HashSet<(long StartMs, long DurationMs, ulong BodyHash)> _seen = [];
        private int _nextReadOrder;

        public StreamingAssOverlaySource(
            AssSubtitleLayerSource source, FFmpegSubtitleStreamReader reader, List<DecodedSubtitleEvent> firstBatch)
        {
            _source = source;
            Ingest(firstBatch);
            _coverage.BeginSweep(0);
            _coverage.AdvanceFrontier(reader.PositionMs);
            _ = Task.Run(() => Pump(reader, _cts.Token));
        }

        private void Pump(FFmpegSubtitleStreamReader reader, CancellationToken cancel)
        {
            try
            {
                var events = new List<DecodedSubtitleEvent>(BatchSize);
                var atEnd = false;
                while (!cancel.IsCancellationRequested)
                {
                    var request = Interlocked.Exchange(ref _pendingSeekMs, -1);
                    if (request >= 0 && !_coverage.IsCovered(request))
                    {
                        if (reader.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, request - PrerollMs))))
                        {
                            _coverage.BeginSweep(Math.Max(0, request - PrerollMs));
                            atEnd = false;
                        }
                        // Seek failure (unseekable container): keep sweeping sequentially.
                    }
                    else if (atEnd)
                    {
                        if (_coverage.IsFullyCovered)
                            return; // nothing can ever be uncovered again - release the reader for good
                        try
                        {
                            _wake.Wait(cancel);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                        continue;
                    }

                    var more = reader.ReadBatch(events, BatchSize);
                    if (events.Count > 0)
                    {
                        Ingest(events);
                        events.Clear();
                    }
                    _coverage.AdvanceFrontier(reader.PositionMs);
                    if (!more)
                    {
                        _coverage.CompleteToEnd();
                        atEnd = true;
                    }
                }
            }
            catch
            {
                // A failing background sweep must not take the process down - the overlay simply stops
                // growing (already-appended events keep rendering).
            }
            finally
            {
                reader.Dispose();
            }
        }

        /// <summary>Dedupes by content (start/duration/body-past-ReadOrder), stamps a fresh ReadOrder,
        /// and appends to the live source (a no-op once the source is disposed).</summary>
        private void Ingest(List<DecodedSubtitleEvent> events)
        {
            List<AssEventChunk>? chunks = null;
            foreach (var e in events)
            {
                if (!_seen.Add((e.StartMs, e.DurationMs, HashBodyPastReadOrder(e.Body))))
                    continue;
                (chunks ??= new List<AssEventChunk>(events.Count))
                    .Add(new AssEventChunk(WithReadOrder(e.Body, _nextReadOrder++), e.StartMs, e.DurationMs));
            }

            if (chunks is not null)
                _source.AppendEvents(chunks);
        }

        /// <summary>FNV-1a over the body EXCLUDING the leading ReadOrder field - converted-format decoders
        /// restart that counter after a seek flush, so the same event can reappear under a different one.</summary>
        private static ulong HashBodyPastReadOrder(byte[] body)
        {
            var start = Array.IndexOf(body, (byte)',') + 1; // 0 (whole body) when there is no comma
            var hash = 14695981039346656037ul;
            for (var i = start; i < body.Length; i++)
            {
                hash ^= body[i];
                hash *= 1099511628211ul;
            }

            return hash;
        }

        /// <summary>Replaces the body's leading ReadOrder field with <paramref name="readOrder"/>.</summary>
        private static byte[] WithReadOrder(byte[] body, int readOrder)
        {
            var comma = Array.IndexOf(body, (byte)',');
            if (comma < 0)
                return body;
            var prefix = System.Text.Encoding.ASCII.GetBytes(
                readOrder.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var result = new byte[prefix.Length + body.Length - comma];
            prefix.CopyTo(result, 0);
            Array.Copy(body, comma, result, prefix.Length, body.Length - comma);
            return result;
        }

        public VideoFrame? RenderAt(TimeSpan position)
        {
            var ms = (long)position.TotalMilliseconds;
            if (ms >= 0 && !_coverage.IsCovered(ms))
            {
                Interlocked.Exchange(ref _pendingSeekMs, ms);
                if (_wake.CurrentCount == 0)
                {
                    try
                    {
                        _wake.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                        // benign race with another releaser - the pump is awake either way
                    }
                }
            }

            return _source.RenderAt(position);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
            }

            _source.Dispose();
        }
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
