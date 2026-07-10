using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace S.Media.Routing;

public sealed partial class AudioRouter
{
    // --- per-output pump -----------------------------------------------------

    /// <summary>
    /// Bounded SPSC chunk queue + drainer thread per output. Producer is the
    /// router's run loop; consumer is the pump's <see cref="DrainLoop"/> which
    /// calls <see cref="IAudioOutput.Submit"/>. On overflow the oldest queued
    /// chunk is dropped (output can't keep up).
    /// </summary>
    /// <remarks>
    /// Zero-copy producer path: the router fills <see cref="WorkingBuffer"/>
    /// in place and calls <see cref="Commit"/> to publish it. The pump
    /// rotates the working buffer with one from its free-pool on every
    /// commit; if both the pool and consumer queue are empty, the mixed chunk
    /// is dropped in place (no fresh allocation) and the same buffer is reused for the next mix.
    /// </remarks>
    private sealed class OutputPump : IDisposable
    {
        private readonly AudioRouter _router;
        private readonly string _sinkId;
        private readonly IAudioOutput _sink;
        private readonly BlockingCollection<float[]> _ready;
        private readonly ConcurrentQueue<float[]> _free = new();
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _floatsPerChunk;
        private readonly int _pumpCapacityChunks;
        /// <summary>Upper bound on how long the primary-output producer waits for the drainer to recycle
        /// a buffer (backpressure) before falling back to dropping - keeps a dead device from hanging the
        /// router thread while being far longer than any healthy drainer scheduling gap.</summary>
        private const int BackpressureCapMs = 1000;
        private const double SlowSubmitWarningMs = 100;
        // Start/dispose are serialized so a late EnsureStarted can never launch the
        // drainer after Dispose has completed the collection / disposed the cts -
        // that would crash the freshly started thread (disposed _ready / _cts.Token).
        private readonly object _startGate = new();
        private bool _started;
        private float[] _working;
        private long _enqueued;
        private long _processed;
        private long _dropped;
        // Enqueued chunks evicted from _ready in Commit (pool-exhaustion drop) - they leave the queue without
        // ever being delivered, so WaitForIdle counts them as "no longer in flight" without inflating the
        // delivered (_processed) count that per-output pacing relies on.
        private long _readyEvictions;
        private long _lastBackpressureWarnTicks;
        private long _lastSlowSubmitWarnTicks;
        private int _stuck;
        private volatile bool _disposed;

        public OutputPumpStats Stats => new(
            Volatile.Read(ref _enqueued),
            Volatile.Read(ref _processed),
            Volatile.Read(ref _dropped),
            _pumpCapacityChunks)
        {
            IsStuck = Volatile.Read(ref _stuck) != 0,
        };

        /// <summary>
        /// Producer-thread scratch - the buffer the router currently writes
        /// into. <see cref="Commit"/> publishes it and rotates a fresh one in.
        /// Only the producer thread should access this.
        /// </summary>
        public float[] WorkingBuffer => _working;

        public OutputPump(AudioRouter router, IAudioOutput output, int capacityChunks, int floatsPerChunk, string outputId)
        {
            _router = router;
            _sinkId = outputId;
            _sink = output;
            _floatsPerChunk = floatsPerChunk;
            _pumpCapacityChunks = capacityChunks;
            _ready = new BlockingCollection<float[]>(boundedCapacity: capacityChunks);
            for (var i = 0; i < capacityChunks; i++)
                _free.Enqueue(new float[floatsPerChunk]);
            _working = new float[floatsPerChunk];

            // Create the managed Thread object now (cheap), but do NOT Start() it here.
            // Start() is what allocates the OS thread + stack; starting one per output at
            // AddOutput time - even for outputs whose router never runs - is the source of
            // the suite-level thread pressure / intermittent OOM. The drainer is launched
            // lazily via EnsureStarted() when the router actually starts (or when an output
            // is added to an already-running router).
            _thread = new Thread(() => DrainLoop(_cts.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = $"OutputPump:{outputId}",
            };
        }

        /// <summary>
        /// Idempotently launches the drainer thread. Called when the router starts (for every
        /// registered pump) and when an output is added to an already-running router. A no-op
        /// after <see cref="Dispose"/> or once already started.
        /// </summary>
        public void EnsureStarted()
        {
            lock (_startGate)
            {
                if (_disposed || _started) return;
                _started = true;
                _thread.Start();
            }
        }

        private long RecordDrop()
        {
            var total = Interlocked.Increment(ref _dropped);
            _router.RaisePumpPressure(_sinkId, total);
            return total;
        }

        /// <summary>
        /// Publish <see cref="WorkingBuffer"/> to the consumer queue and rotate in a fresh buffer for
        /// the next chunk. On pool exhaustion (consumer behind): when <paramref name="applyBackpressure"/>
        /// is <see langword="false"/> (non-primary outputs), evict the oldest queued chunk and count the
        /// drop so a slow output never stalls the shared router. When <see langword="true"/> (the pacing
        /// primary output, which is the master clock), wait briefly for the drainer to recycle a buffer
        /// instead - a dropped chunk there permanently desyncs A/V (the played sample count keeps
        /// advancing while the audio content skips), and the pump exists to absorb that jitter.
        /// </summary>
        public void Commit(bool applyBackpressure = false)
        {
            if (_disposed) return;

            var buf = _working;
            if (!_free.TryDequeue(out var next))
            {
                if (applyBackpressure && WaitForFreeBuffer(out next))
                {
                    // Recycled a buffer via backpressure - the drainer took one from _ready, so there
                    // is room below; fall through to publish without dropping.
                }
                else if (_ready.TryTake(out next!))
                {
                    // The evicted chunk was already counted in _enqueued; it leaves _ready here without ever
                    // reaching the drainer. Record it as a ready-queue eviction so WaitForIdle stops treating
                    // it as still-in-flight - otherwise processed < enqueued forever after any drop and every
                    // pause/stop/seek spins the full WaitForIdle timeout (e.g. the _audio_discard sink). We do
                    // NOT bump _processed here: that counter means "delivered to the sink" (per-output pacing).
                    Interlocked.Increment(ref _readyEvictions);
                    RecordDrop();
                }
                else
                {
                    // Pool and consumer queue are empty: drop this mixed chunk and
                    // reuse the same buffer next iteration - avoids allocating on the producer thread.
                    RecordDrop();
                    return;
                }
            }
            _working = next!;

            try
            {
                if (_ready.TryAdd(buf))
                {
                    Interlocked.Increment(ref _enqueued);
                }
                else
                {
                    _free.Enqueue(buf);
                    RecordDrop();
                }
            }
            catch (ObjectDisposedException)   { RecordDrop(); }
            catch (InvalidOperationException) { RecordDrop(); }
        }

        /// <summary>
        /// Pace the producer to the drainer for the primary output: wait (bounded) for the drainer to
        /// recycle a buffer into the free pool rather than dropping. The drainer's <c>Submit</c> is
        /// non-blocking, so while the device is healthy it always drains <c>_ready</c> and recycles a
        /// buffer within ~one chunk; the cap is a safety valve so a wedged/closed device can never hang
        /// the shared router thread (it falls back to the drop path).
        /// </summary>
        private bool WaitForFreeBuffer([NotNullWhen(true)] out float[]? buf)
        {
            var deadline = Environment.TickCount64 + BackpressureCapMs;
            var spin = new SpinWait();
            while (!_disposed && !_cts.IsCancellationRequested && Environment.TickCount64 < deadline)
            {
                if (_free.TryDequeue(out buf))
                    return true;
                spin.SpinOnce(); // escalates to Thread.Yield/Sleep - never a hot busy-spin
            }
            buf = null;
            MaybeLogBackpressureTimeout();
            return false;
        }

        /// <summary>
        /// Discard every chunk currently queued. Does <em>not</em> interrupt an
        /// in-flight <see cref="IAudioOutput.Submit"/> on the drainer thread -
        /// the caller should follow with <see cref="WaitForIdle"/> if they
        /// need quiescence guarantees.
        /// </summary>
        public void AbandonQueue()
        {
            while (_ready.TryTake(out var buf))
            {
                _free.Enqueue(buf);
                // Each chunk we take here was counted in _enqueued and will never reach the
                // drainer, so count it as processed too - otherwise WaitForIdle sees
                // processed < enqueued forever and blocks for the full timeout after a flush.
                Interlocked.Increment(ref _processed);
                RecordDrop();
            }
        }

        /// <summary>
        /// Block until <c>processed == enqueued</c> (drainer has caught up) or
        /// <paramref name="timeout"/> elapses.
        /// </summary>
        public void WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var started = Stopwatch.GetTimestamp();
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            // A chunk is "in flight" until it leaves _ready - either delivered (_processed, incl. AbandonQueue)
            // or evicted in Commit (_readyEvictions). Counting evictions here is what keeps a dropped chunk
            // from wedging WaitForIdle at processed < enqueued forever.
            while (Volatile.Read(ref _processed) + Volatile.Read(ref _readyEvictions) < Volatile.Read(ref _enqueued))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Environment.TickCount64 > deadline)
                {
                    Trace.LogWarning(
                        "AudioRouter.OutputPump {OutputId}: WaitForIdle timed out after {ElapsedMs:0.00}ms (timeout={TimeoutMs:0.00}ms, enqueued={Enqueued}, processed={Processed}, evicted={Evicted}, dropped={Dropped})",
                        _sinkId,
                        MediaDiagnostics.ElapsedMillisecondsSince(started),
                        timeout.TotalMilliseconds,
                        Volatile.Read(ref _enqueued),
                        Volatile.Read(ref _processed),
                        Volatile.Read(ref _readyEvictions),
                        Volatile.Read(ref _dropped));
                    return;
                }
                Thread.Sleep(1);
            }
        }

        private void DrainLoop(CancellationToken token)
        {
            try
            {
                foreach (var buf in _ready.GetConsumingEnumerable(token))
                {
                    var submitStarted = Trace.IsEnabled(LogLevel.Warning) ? Stopwatch.GetTimestamp() : 0;
                    try { _sink.Submit(buf); }
                    catch (Exception ex) { _router.RaiseOutputErrored(_sinkId, ex); }
                    finally
                    {
                        if (submitStarted != 0)
                            MaybeLogSlowSubmit(submitStarted);
                    }
                    Interlocked.Increment(ref _processed);
                    _free.Enqueue(buf);
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }

        private void MaybeLogBackpressureTimeout()
        {
            if (!Trace.IsEnabled(LogLevel.Warning) || !TryUpdateThrottle(ref _lastBackpressureWarnTicks, TimeSpan.FromSeconds(2)))
                return;

            Trace.LogWarning(
                "AudioRouter.OutputPump {OutputId}: primary-output backpressure waited {TimeoutMs}ms without a recycled buffer; dropping chunk (enqueued={Enqueued}, processed={Processed}, dropped={Dropped})",
                _sinkId,
                BackpressureCapMs,
                Volatile.Read(ref _enqueued),
                Volatile.Read(ref _processed),
                Volatile.Read(ref _dropped));
        }

        private void MaybeLogSlowSubmit(long started)
        {
            var elapsedMs = MediaDiagnostics.ElapsedMillisecondsSince(started);
            if (elapsedMs < SlowSubmitWarningMs ||
                !TryUpdateThrottle(ref _lastSlowSubmitWarnTicks, TimeSpan.FromSeconds(2)))
                return;

            Trace.LogWarning(
                "AudioRouter.OutputPump {OutputId}: Submit took {ElapsedMs:0.00}ms (threshold={ThresholdMs:0.00}ms, enqueued={Enqueued}, processed={Processed}, dropped={Dropped})",
                _sinkId,
                elapsedMs,
                SlowSubmitWarningMs,
                Volatile.Read(ref _enqueued),
                Volatile.Read(ref _processed),
                Volatile.Read(ref _dropped));
        }

        private static bool TryUpdateThrottle(ref long ticksSlot, TimeSpan interval)
        {
            var now = Stopwatch.GetTimestamp();
            var prev = Volatile.Read(ref ticksSlot);
            if (prev != 0 && Stopwatch.GetElapsedTime(prev, now) < interval)
                return false;
            return Interlocked.CompareExchange(ref ticksSlot, now, prev) == prev;
        }

        public void Dispose()
        {
            lock (_startGate)
            {
                if (_disposed) return;
                _disposed = true;
                // Block any concurrent EnsureStarted from here on. If the thread was never
                // started, the joins below are no-ops (JoinThread/IsAlive guard on IsAlive,
                // which is false for an unstarted Thread).
            }
            MediaDiagnostics.SwallowDisposeErrors(_ready.CompleteAdding, "OutputPump.CompleteAdding");

            CooperativePlaybackJoin.JoinThread(_thread, TimeSpan.FromSeconds(2));
            if (_thread.IsAlive)
            {
                try { _cts.Cancel(); }
                catch (Exception ex)
                {
#if DEBUG
                    MediaDiagnostics.LogError(ex, "OutputPump.Dispose: CancellationTokenSource.Cancel");
#else
                    _ = ex;
#endif
                }

                CooperativePlaybackJoin.JoinThread(_thread, TimeSpan.FromSeconds(1));
            }

            if (_thread.IsAlive)
            {
                Volatile.Write(ref _stuck, 1);
                _router.MarkOutputPumpStuck(_sinkId);
                Trace.LogError(
                    "AudioRouter.OutputPump '{OutputId}': drainer did not exit within the join cap; leaking pump state to avoid use-after-dispose.",
                    _sinkId);
                return;
            }

            _ready.Dispose();
            _cts.Dispose();
        }
    }
}
