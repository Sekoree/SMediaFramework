using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// One lazy timer loop for all session completion checks. Work producers only signal it; the supplied poll
/// reconciles every active transport/preview/voice in one dispatcher command and returns whether work remains.
/// </summary>
internal sealed class SessionCompletionMonitor
{
    private readonly TimeSpan _interval;
    private readonly Func<Task<bool>> _poll;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _workSignal = new(0, 1);
    private readonly Task _loop;
    private int _stopped;

    public SessionCompletionMonitor(TimeSpan interval, Func<Task<bool>> poll)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "must be positive");
        _interval = interval;
        _poll = poll ?? throw new ArgumentNullException(nameof(poll));
        using (ExecutionContext.SuppressFlow())
            _loop = Task.Run(RunAsync);
    }

    public Task Completion => _loop;

    public void NotifyWorkAvailable()
    {
        if (Volatile.Read(ref _stopped) != 0)
            return;
        try { _workSignal.Release(); }
        catch (SemaphoreFullException) { /* the loop already has a pending wake-up */ }
        catch (ObjectDisposedException) { /* shutdown won the race */ }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        _cts.Cancel();
        try { _workSignal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _workSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                while (!_cts.IsCancellationRequested)
                {
                    await Task.Delay(_interval, _cts.Token).ConfigureAwait(false);
                    bool hasWork;
                    try
                    {
                        hasWork = await _poll().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        MediaDiagnostics.LogWarning(
                            "ShowSession: consolidated completion poll failed ({0}); retrying.", ex.Message);
                        hasWork = true;
                    }

                    if (!hasWork)
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        finally
        {
            _workSignal.Dispose();
            _cts.Dispose();
        }
    }
}
