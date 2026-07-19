namespace S.Media.Session;

/// <summary>
/// The session's fire sequencing: owns the fire-lock (fires/GOs never interleave - the app drives GO
/// serially) and the in-flight fire's cancellation source, and runs every cue fire OFF the serial dispatcher
/// (NXT-03) so a pre-wait or media open never parks the loop - STOP/LOAD/DISPOSE preempt it through
/// <see cref="CancelActiveFire"/>. State reads/commits (GO's cue selection and cursor advance) stay on
/// <see cref="ShowSession"/> as internal dispatcher-marshaled operations; this class owns only the
/// when/ordering. Split out of the session along its ownership seam (review Part-5 #2); the session's public
/// fire/GO API delegates here.
/// </summary>
internal sealed class CueFireOrchestrator
{
    private readonly ShowSession _session;

    // The cancellation source of the in-flight cue fire (its pre/post-wait + open + auto-continue chain). Set
    // while a fire runs; read off-dispatcher by CancelActiveFire so STOP/LOAD/DISPOSE can abort it (NXT-03).
    private volatile CancellationTokenSource? _activeFireCts;

    // Off-dispatcher fire model (NXT-03): a cue fire runs OFF the serial dispatcher (its pre-wait + media open
    // no longer park the loop, so STOP/seek/load/queries stay responsive), re-entering only for short state
    // commits. _fireLock serializes fires; the session's show generation lets a fire whose open straddled a
    // reload discard its (now-stale) clip at commit instead of corrupting the newer show.
    private readonly SemaphoreSlim _fireLock = new(1, 1);

    public CueFireOrchestrator(ShowSession session) => _session = session;

    /// <summary>See <see cref="ShowSession.FireCueAsync"/> (the public doc lives there).</summary>
    public async Task<CueExecutionStatus> FireCueAsync(string cueId)
    {
        await _fireLock.WaitAsync().ConfigureAwait(false);
        try { return await FireCoreAsync(cueId).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CueExecutionStatus.Failed; } // cancelled by stop/load/dispose
        finally { _fireLock.Release(); }
    }

    /// <summary>The lock-free fire core (the caller MUST hold <see cref="_fireLock"/>): runs the cue graph fire OFF
    /// the serial dispatcher (NXT-03) - its pre/post-wait and media open no longer park the loop; only the short
    /// state commits re-enter it. The fire's cancellation source is published to <see cref="_activeFireCts"/> so
    /// <see cref="CancelActiveFire"/> aborts it; cancellation propagates as
    /// <see cref="OperationCanceledException"/> (callers map it to a non-advancing result).</summary>
    private async Task<CueExecutionStatus> FireCoreAsync(string cueId)
    {
        using var cts = new CancellationTokenSource();
        _activeFireCts = cts;
        try { return await _session.FireOnGraphAsync(cueId, cts.Token).ConfigureAwait(false); }
        finally { _activeFireCts = null; }
    }

    /// <summary>See <see cref="ShowSession.FireCuesAsync"/> (the public doc lives there).</summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesAsync(IReadOnlyList<string> cueIds)
    {
        if (cueIds.Count == 0)
            return [];
        if (cueIds.Count == 1)
            return [await FireCueAsync(cueIds[0]).ConfigureAwait(false)];

        await _fireLock.WaitAsync().ConfigureAwait(false);
        using var cts = new CancellationTokenSource();
        _activeFireCts = cts;
        try
        {
            var fires = new Task<CueExecutionStatus>[cueIds.Count];
            for (var i = 0; i < cueIds.Count; i++)
                fires[i] = FireForGroupAsync(cueIds[i], cts.Token);
            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        finally
        {
            _activeFireCts = null;
            _fireLock.Release();
        }
    }

    /// <summary>Fires several clip-bound cues concurrently, each on the caller-provided runtime transport group.
    /// This is used when authored siblings must remain active together: assigning a distinct runtime group prevents
    /// one sibling's commit from replacing another while the shared fire lock still keeps an unrelated GO/fire from
    /// interleaving with the batch.</summary>
    public async Task<IReadOnlyList<CueExecutionStatus>> FireCuesIndependentAsync(
        IReadOnlyList<(string CueId, string RuntimeGroupId)> targets,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return [];

        await _fireLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeFireCts = cts;
        try
        {
            var startBarrier = new CoordinatedFireBarrier(targets.Count);
            var fires = new Task<CueExecutionStatus>[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                fires[i] = FireIndependentForGroupAsync(
                    target.CueId, target.RuntimeGroupId, startBarrier, cts.Token);
            }

            return await Task.WhenAll(fires).ConfigureAwait(false);
        }
        finally
        {
            _activeFireCts = null;
            _fireLock.Release();
        }
    }

    /// <summary>One cue's fire within a <see cref="FireCuesAsync"/> group: maps cancellation to a non-throwing
    /// <see cref="CueExecutionStatus.Failed"/> (so one cancelled cue doesn't fault the whole <c>WhenAll</c>); a
    /// <see cref="CueFaultPolicy.StopShow"/> fault still propagates, matching single-cue fire.</summary>
    private async Task<CueExecutionStatus> FireForGroupAsync(string cueId, CancellationToken token)
    {
        try { return await _session.FireOnGraphAsync(cueId, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return CueExecutionStatus.Failed; }
    }

    private async Task<CueExecutionStatus> FireIndependentForGroupAsync(
        string cueId,
        string runtimeGroupId,
        CoordinatedFireBarrier startBarrier,
        CancellationToken token)
    {
        var reachedBarrier = false;
        try
        {
            return await _session.FireCueIndependentAtBarrierAsync(
                    cueId,
                    runtimeGroupId,
                    async () =>
                    {
                        reachedBarrier = true;
                        await startBarrier.SignalAndWaitAsync(token).ConfigureAwait(false);
                    },
                    token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CueExecutionStatus.Failed;
        }
        finally
        {
            // Invalid/unbound/failed-to-open cues never reach the arm barrier. Count them as arrived so one bad
            // sibling cannot strand every successfully armed clip waiting forever.
            if (!reachedBarrier)
                startBarrier.Signal();
        }
    }

    private sealed class CoordinatedFireBarrier(int participantCount)
    {
        private int _remaining = participantCount;
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Signal()
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
                _released.TrySetResult();
        }

        public async Task SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            Signal();
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels the in-flight cue fire, if any, WITHOUT marshaling onto the dispatcher - so a stop/load/
    /// dispose can unblock the serial loop that a long pre-wait or open is parking, then run promptly (NXT-03).
    /// A no-op when nothing is firing. Note: a synchronous, uninterruptible native open still runs to completion;
    /// this preempts the (common) pre/post-wait and any cancellable stage.</summary>
    public void CancelActiveFire()
    {
        var cts = _activeFireCts;
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* the fire already finished and disposed it */ }
    }

    /// <summary>See <see cref="ShowSession.GoAsync"/> (the public doc lives there).</summary>
    public async Task<CueExecutionStatus> GoAsync(string groupId)
    {
        // Hold the fire-lock across select → fire → advance, so a concurrent GO (e.g. two rapid remote commands)
        // can't read the same cursor and double-fire the same cue. The fire itself still runs off the dispatcher.
        await _fireLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Selection on the dispatcher (reads the cue graph + the group cursor).
            var (next, generation) = await _session.SelectNextGoCueAsync(groupId).ConfigureAwait(false);
            if (next is null)
                return CueExecutionStatus.NotReady;

            // Fire OFF the dispatcher (we already hold the fire-lock - FireCoreAsync is the lock-free core).
            CueExecutionStatus status;
            try { status = await FireCoreAsync(next.Id).ConfigureAwait(false); }
            catch (OperationCanceledException) { return CueExecutionStatus.Failed; } // cancelled - do NOT advance

            // Advance the cursor on the dispatcher - only when the cue actually ran (or faulted), never a skip/cancel.
            if (status is CueExecutionStatus.Fired or CueExecutionStatus.Failed)
                await _session.AdvanceGoCursorAsync(groupId, next.Number, generation).ConfigureAwait(false);
            _ = _session.WarmUpcomingAsync(groupId); // pre-roll the next cue(s) in the background so the next GO is instant
            return status;
        }
        finally
        {
            _fireLock.Release();
        }
    }
}
