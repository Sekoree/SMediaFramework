namespace S.Control;

/// <summary>
/// Single-consumer dispatch queue for control-script work. Every enqueue returns a task the caller awaits
/// for the script result, so the queue is request/response, not fire-and-forget.
/// </summary>
/// <remarks>
/// <para><strong>Bounded (CTRL-01).</strong> The backing buffer is bounded to <see cref="_capacity"/>. A
/// native MIDI/UDP receive callback that floods the queue, or a slow/hung script that stops draining it, can
/// no longer grow memory or latency without limit. Enqueue never blocks the calling (receive-callback)
/// thread — it either appends, coalesces, or drops synchronously and returns immediately.</para>
/// <para><strong>Pressure relief.</strong> Under normal load (the script keeps up) the queue is a plain FIFO:
/// every value is delivered in order, so relative encoders and rapid distinct commands are never lost. Only
/// when the buffer is <em>full</em> does relief kick in:</para>
/// <list type="bullet">
///   <item>A newer value of a <em>continuous</em> control (MIDI CC / pitch-bend / aftertouch / scalar meter)
///   coalesces onto the pending one — only the latest value matters for an absolute control
///   (<see cref="CoalescedCount"/>).</item>
///   <item>Otherwise the oldest <em>continuous</em> item is dropped to make room, so button/note and
///   lifecycle edges survive; only when the buffer is entirely edges is the oldest edge dropped as a last
///   resort (<see cref="DroppedCount"/>).</item>
/// </list>
/// <para><strong>Shutdown bound (CTRL-02).</strong> <see cref="DisposeAsync"/> is the primary path and waits
/// for the worker only up to <see cref="_shutdownTimeout"/>; a script that will not cooperatively cancel is
/// abandoned (it finishes on its background task) and recorded to the monitor rather than blocking teardown
/// forever. <see cref="Dispose"/> is a bounded synchronous adapter over the same policy.</para>
/// </remarks>
public sealed class ControlEventQueue : IControlScriptDispatcher, IAsyncDisposable, IDisposable
{
    /// <summary>Default upper bound on queued (not-yet-executed) control-script work items.</summary>
    public const int DefaultCapacity = 4096;

    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly ControlScriptRuntimeSessionResult EmptyResult = new([], [], [], [], []);

    private readonly ControlScriptRuntimeSession _session;
    private readonly IControlMonitorSink _monitor;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _worker;
    private readonly int _capacity;
    private readonly TimeSpan _shutdownTimeout;

    private readonly Lock _gate = new();
    private readonly LinkedList<ControlEventQueueItem> _fifo = new();
    private readonly Dictionary<string, LinkedListNode<ControlEventQueueItem>> _coalesceIndex = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _available = new(0);

    private int _pendingCount;
    private int _disposeSignaled;
    private long _dropped;
    private long _coalesced;

    public ControlEventQueue(
        ControlScriptRuntimeSession session,
        IControlMonitorSink? monitor = null,
        int capacity = DefaultCapacity,
        TimeSpan? shutdownTimeout = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _shutdownTimeout = shutdownTimeout is { } t && t > TimeSpan.Zero ? t : DefaultShutdownTimeout;
        _worker = Task.Run(RunWorkerAsync);
    }

    public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    /// <summary>Continuous-control values superseded by a newer value while the queue was full (CTRL-01).</summary>
    public long CoalescedCount => Interlocked.Read(ref _coalesced);

    /// <summary>Items discarded because the bounded queue was full and could not be coalesced (CTRL-01).</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public Guid? ActiveLayerId => _session.ActiveLayerId;

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchControlEventAsync(
        ControlEvent evt,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "control event",
            ct => _session.DispatchControlEventAsync(evt, ct),
            cancellationToken,
            CoalesceKeyFor(evt));

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceEnabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "device enabled",
            ct => _session.DispatchDeviceEnabledAsync(deviceInstanceId, ct),
            cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceDisabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "device disabled",
            ct => _session.DispatchDeviceDisabledAsync(deviceInstanceId, ct),
            cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerEnabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "layer enabled",
            ct => _session.DispatchLayerEnabledAsync(layerId, ct),
            cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerDisabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "layer disabled",
            ct => _session.DispatchLayerDisabledAsync(layerId, ct),
            cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> SetActiveLayerAsync(
        Guid? layerId,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "set active layer",
            ct => _session.SetActiveLayerAsync(layerId, ct),
            cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchManualAsync(
        Guid? scriptId = null,
        Guid? triggerId = null,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "manual trigger",
            ct => _session.DispatchManualAsync(scriptId, triggerId, ct),
            cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> ReportDeviceHealthAsync(
        Guid deviceInstanceId,
        ControlSessionHealth health,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "device health",
            ct => _session.ReportDeviceHealthAsync(deviceInstanceId, health, ct),
            cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> TickPeriodicAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "periodic script tick",
            ct => _session.TickPeriodicAsync(utcNow, ct),
            cancellationToken);

    /// <summary>
    /// Coalesce key for an event, or null when the event must be preserved (button/note edges, transport,
    /// program change, OSC, device/layer/health lifecycle, text, blob). Only absolute continuous controls,
    /// where solely the newest value matters, are coalescable — and coalescing only ever happens under
    /// pressure (see the type remarks), so relative encoders survive normal load.
    /// </summary>
    private static string? CoalesceKeyFor(ControlEvent evt) => evt switch
    {
        MIDIControlEvent cc => $"m.cc:{cc.SourceNodeId:N}:{cc.Channel}:{cc.Controller}",
        ScalarControlEvent s => $"m.scalar:{s.SourceNodeId:N}:{s.OriginId:N}",
        MIDIMessageControlEvent { Message: { } msg } m => ContinuousMessageKey(m.SourceNodeId, msg),
        _ => null,
    };

    private static string? ContinuousMessageKey(Guid source, ControlMIDIMessagePayload msg) => msg.MessageType switch
    {
        ControlMIDIMessageType.ControlChange => $"m.msg.cc:{source:N}:{msg.Channel}:{msg.Controller}",
        ControlMIDIMessageType.PitchBend => $"m.msg.pb:{source:N}:{msg.Channel}",
        ControlMIDIMessageType.ChannelAftertouch => $"m.msg.cat:{source:N}:{msg.Channel}",
        ControlMIDIMessageType.PolyphonicAftertouch => $"m.msg.pat:{source:N}:{msg.Channel}:{msg.Note}",
        _ => null,
    };

    private ValueTask<ControlScriptRuntimeSessionResult> EnqueueAsync(
        string operationName,
        Func<CancellationToken, ValueTask<ControlScriptRuntimeSessionResult>> operation,
        CancellationToken cancellationToken,
        string? coalesceKey = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _disposeSignaled) != 0)
            return ValueTask.FromException<ControlScriptRuntimeSessionResult>(
                new ObjectDisposedException(nameof(ControlEventQueue)));

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<ControlScriptRuntimeSessionResult>(cancellationToken);

        var completion = new TaskCompletionSource<ControlScriptRuntimeSessionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new ControlEventQueueItem(operationName, operation, completion, cancellationToken, coalesceKey);

        ControlEventQueueItem? superseded = null;
        ControlEventQueueItem? droppedItem = null;
        var releasePermit = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposeSignaled) != 0)
                return ValueTask.FromException<ControlScriptRuntimeSessionResult>(
                    new ObjectDisposedException(nameof(ControlEventQueue)));

            if (_fifo.Count < _capacity)
            {
                // Normal path: plain FIFO append. No coalescing — every value is preserved (relative
                // encoders, distinct rapid commands). Continuous controls are indexed so a later
                // full-queue enqueue can coalesce onto them.
                var node = _fifo.AddLast(item);
                IndexIfContinuous(coalesceKey, node);
                releasePermit = true; // one item added → one permit (permit count == fifo count invariant)
            }
            else if (coalesceKey is not null && _coalesceIndex.TryGetValue(coalesceKey, out var existing))
            {
                // Full + continuous with a pending value for this control: coalesce onto it (keep the
                // newest value at the existing FIFO slot). Count unchanged → no permit change.
                superseded = existing.Value;
                existing.Value = item;
                Interlocked.Increment(ref _coalesced);
            }
            else if (TryEvictOneLocked(out droppedItem))
            {
                // Full, cannot coalesce: a stale item was evicted (prefer continuous) to make room.
                // Evict removed one and we add one → count unchanged → no permit change.
                var node = _fifo.AddLast(item);
                IndexIfContinuous(coalesceKey, node);
                Interlocked.Increment(ref _dropped);
            }
            else
            {
                // Should be unreachable (full implies at least one evictable item), but never block the
                // caller: drop the incoming item itself as a final safety valve.
                droppedItem = item;
                Interlocked.Increment(ref _dropped);
            }

            Interlocked.Increment(ref _pendingCount);
        }

        // Resolve superseded/evicted callers OUTSIDE the lock. Coalesced/dropped work did not run, so an
        // empty result (no invocations, no routes) truthfully reflects "superseded before execution".
        if (superseded is not null)
        {
            superseded.Completion.TrySetResult(EmptyResult);
            Interlocked.Decrement(ref _pendingCount);
        }

        if (droppedItem is not null && !ReferenceEquals(droppedItem, item))
        {
            droppedItem.Completion.TrySetResult(EmptyResult);
            Interlocked.Decrement(ref _pendingCount);
        }

        if (droppedItem is not null && ReferenceEquals(droppedItem, item))
        {
            // The incoming item itself was dropped (safety valve): complete it and don't hand back a task
            // that never resolves.
            item.Completion.TrySetResult(EmptyResult);
            Interlocked.Decrement(ref _pendingCount);
            return new ValueTask<ControlScriptRuntimeSessionResult>(completion.Task);
        }

        if (releasePermit)
            _available.Release();

        return new ValueTask<ControlScriptRuntimeSessionResult>(completion.Task);
    }

    private void IndexIfContinuous(string? coalesceKey, LinkedListNode<ControlEventQueueItem> node)
    {
        if (coalesceKey is not null)
            _coalesceIndex[coalesceKey] = node; // most-recent pending node for this control
    }

    /// <summary>Removes one item to make room: the oldest continuous (coalescable) item when any exists —
    /// so button/lifecycle edges are preserved — otherwise the oldest item overall. Caller holds <c>_gate</c>.</summary>
    private bool TryEvictOneLocked(out ControlEventQueueItem evicted)
    {
        for (var node = _fifo.First; node is not null; node = node.Next)
        {
            if (node.Value.CoalesceKey is null)
                continue;
            evicted = node.Value;
            RemoveNodeLocked(node);
            return true;
        }

        var first = _fifo.First;
        if (first is not null)
        {
            evicted = first.Value;
            RemoveNodeLocked(first);
            return true;
        }

        evicted = default!;
        return false;
    }

    private void RemoveNodeLocked(LinkedListNode<ControlEventQueueItem> node)
    {
        if (node.Value.CoalesceKey is { } key
            && _coalesceIndex.TryGetValue(key, out var indexed)
            && ReferenceEquals(indexed, node))
            _coalesceIndex.Remove(key);
        _fifo.Remove(node);
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
                ControlEventQueueItem item;
                lock (_gate)
                {
                    var node = _fifo.First!;
                    item = node.Value; // freshest value (a coalesce may have replaced it in place)
                    RemoveNodeLocked(node);
                }

                await ExecuteItemAsync(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        finally
        {
            CancelPendingItems();
        }
    }

    private async ValueTask ExecuteItemAsync(ControlEventQueueItem item)
    {
        try
        {
            if (item.CancellationToken.IsCancellationRequested)
            {
                item.Completion.TrySetCanceled(item.CancellationToken);
                return;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _shutdownCts.Token,
                item.CancellationToken);
            var result = await item.Operation(linkedCts.Token).ConfigureAwait(false);
            item.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (item.CancellationToken.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(item.CancellationToken);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            item.Completion.TrySetCanceled(_shutdownCts.Token);
        }
        catch (Exception ex)
        {
            item.Completion.TrySetException(ex);
            _monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Runtime,
                Result = ControlMonitorResult.Failed,
                Message = $"Queued {item.OperationName} failed.",
                ErrorMessage = ex.Message,
            });
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    private void CancelPendingItems()
    {
        List<ControlEventQueueItem> pending;
        lock (_gate)
        {
            if (_fifo.Count == 0)
                return;
            pending = new List<ControlEventQueueItem>(_fifo);
            _fifo.Clear();
            _coalesceIndex.Clear();
        }

        foreach (var item in pending)
        {
            item.Completion.TrySetCanceled(_shutdownCts.Token);
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;

        _shutdownCts.Cancel();
        var finished = false;
        try
        {
            await _worker.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
            finished = true;
        }
        catch (TimeoutException)
        {
            RecordAbandonedShutdown();
        }
        catch (OperationCanceledException)
        {
            finished = true;
        }

        CancelPendingItems();
        FinalizeShutdown(finished);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;

        _shutdownCts.Cancel();
        var finished = false;
        try
        {
            // Bounded synchronous adapter: never block teardown forever on a non-cooperative script.
            finished = _worker.Wait(_shutdownTimeout);
        }
        catch
        {
            finished = true; // worker faulted/observed; treat as done for cleanup purposes
        }

        if (!finished)
            RecordAbandonedShutdown();

        CancelPendingItems();
        FinalizeShutdown(finished);
    }

    /// <summary>Disposes the shutdown token once the worker no longer uses it. When the worker was abandoned
    /// (non-cooperative script still running), defer disposal until it actually completes to avoid pulling
    /// the token out from under it. <see cref="_available"/> is intentionally left undisposed (SemaphoreSlim
    /// without a wait handle needs no disposal) so an abandoned worker's final wait cannot fault.</summary>
    private void FinalizeShutdown(bool workerFinished)
    {
        if (workerFinished)
        {
            _shutdownCts.Dispose();
            return;
        }

        _ = _worker.ContinueWith(
            static (_, state) =>
            {
                try { ((CancellationTokenSource)state!).Dispose(); }
                catch (ObjectDisposedException) { }
            },
            _shutdownCts,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RecordAbandonedShutdown() =>
        _monitor.Record(new ControlMonitorRecord
        {
            Direction = ControlMonitorDirection.Error,
            Protocol = ControlMonitorProtocol.Runtime,
            Result = ControlMonitorResult.Failed,
            Message = "Control script did not stop within the shutdown timeout; abandoned.",
            ErrorMessage = $"shutdownTimeout={_shutdownTimeout}",
        });

    private sealed record ControlEventQueueItem(
        string OperationName,
        Func<CancellationToken, ValueTask<ControlScriptRuntimeSessionResult>> Operation,
        TaskCompletionSource<ControlScriptRuntimeSessionResult> Completion,
        CancellationToken CancellationToken,
        string? CoalesceKey);
}
