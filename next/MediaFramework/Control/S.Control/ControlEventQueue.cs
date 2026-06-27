using System.Threading.Channels;

namespace S.Control;

public sealed class ControlEventQueue : IControlScriptDispatcher, IAsyncDisposable, IDisposable
{
    private readonly ControlScriptRuntimeSession _session;
    private readonly Channel<ControlEventQueueItem> _queue;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly IControlMonitorSink _monitor;
    private readonly Task _worker;
    private int _pendingCount;
    private int _disposeSignaled;

    public ControlEventQueue(ControlScriptRuntimeSession session, IControlMonitorSink? monitor = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _monitor = monitor ?? NullControlMonitorSink.Instance;
        _queue = Channel.CreateUnbounded<ControlEventQueueItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(RunWorkerAsync);
    }

    public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    public Guid? ActiveLayerId => _session.ActiveLayerId;

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchControlEventAsync(
        ControlEvent evt,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            "control event",
            ct => _session.DispatchControlEventAsync(evt, ct),
            cancellationToken);

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

    private ValueTask<ControlScriptRuntimeSessionResult> EnqueueAsync(
        string operationName,
        Func<CancellationToken, ValueTask<ControlScriptRuntimeSessionResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (Volatile.Read(ref _disposeSignaled) != 0)
            return ValueTask.FromException<ControlScriptRuntimeSessionResult>(
                new ObjectDisposedException(nameof(ControlEventQueue)));

        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<ControlScriptRuntimeSessionResult>(cancellationToken);

        var completion = new TaskCompletionSource<ControlScriptRuntimeSessionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new ControlEventQueueItem(operationName, operation, completion, cancellationToken);
        Interlocked.Increment(ref _pendingCount);

        if (_queue.Writer.TryWrite(item))
            return new ValueTask<ControlScriptRuntimeSessionResult>(completion.Task);

        Interlocked.Decrement(ref _pendingCount);
        return ValueTask.FromException<ControlScriptRuntimeSessionResult>(
            new ObjectDisposedException(nameof(ControlEventQueue)));
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
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
        while (_queue.Reader.TryRead(out var item))
        {
            item.Completion.TrySetCanceled(_shutdownCts.Token);
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;

        _queue.Writer.TryComplete();
        _shutdownCts.Cancel();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdownCts.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeSignaled, 1) != 0)
            return;

        _queue.Writer.TryComplete();
        _shutdownCts.Cancel();
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdownCts.Dispose();
        }
    }

    private sealed record ControlEventQueueItem(
        string OperationName,
        Func<CancellationToken, ValueTask<ControlScriptRuntimeSessionResult>> Operation,
        TaskCompletionSource<ControlScriptRuntimeSessionResult> Completion,
        CancellationToken CancellationToken);
}
