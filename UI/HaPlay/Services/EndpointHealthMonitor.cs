using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Services;

/// <summary>
/// APP-02: owns the endpoint-health polling lifecycle that used to live inline in <c>MainViewModel</c> - the
/// background <see cref="DispatcherTimer"/>, the single-flight guard, the per-run <see cref="CancellationTokenSource"/>,
/// and the timed-operation logging. The view model keeps only the domain probe loop, handed in as
/// <paramref name="probeAll"/>; this service decides <em>when</em> and <em>how often</em> it runs and guarantees a
/// clean, explicit lifetime (<see cref="Dispose"/> stops the timer and cancels any in-flight run).
/// </summary>
/// <remarks>
/// APP-01 behaviour is preserved: the timer only ticks while <paramref name="pendingCount"/> reports work to do,
/// and a run with nothing to probe neither starts a timed operation nor logs a "probed=0" sweep.
/// </remarks>
public sealed class EndpointHealthMonitor : IDisposable
{
    private readonly Func<int> _pendingCount;
    private readonly Func<CancellationToken, Task<int>> _probeAll;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _cts;
    private int _refreshInFlight;
    private volatile bool _disposed;

    public EndpointHealthMonitor(
        TimeSpan interval,
        Func<int> pendingCount,
        Func<CancellationToken, Task<int>> probeAll,
        ILogger log)
    {
        _pendingCount = pendingCount ?? throw new ArgumentNullException(nameof(pendingCount));
        _probeAll = probeAll ?? throw new ArgumentNullException(nameof(probeAll));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => _ = RunAsync("timer"))
        {
            IsEnabled = false,
        };
    }

    /// <summary>APP-01: enable the poll only while there are endpoints to probe. Call after the endpoint set changes.</summary>
    public void SyncEnabled()
    {
        if (!_disposed)
            _timer.IsEnabled = _pendingCount() > 0;
    }

    /// <summary>Runs one health sweep now (startup, endpoint-set change, or manual). Coalesces with any in-flight
    /// run and cancels a superseded one - the same single-flight semantics the view model had inline.</summary>
    public Task RefreshAsync() => RunAsync("manual");

    private async Task RunAsync(string reason)
    {
        // Nothing to probe → don't even start a timed operation (avoids the old "probed=0" sweep log).
        if (_disposed || _pendingCount() == 0)
            return;

        using var timing = MediaDiagnostics.BeginTimedOperation(_log, "EndpointHealthMonitor.Refresh", slowWarningMs: 1500);
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            timing?.SetOutcome("already-in-flight");
            return;
        }

        // Cancel a prior run and take a fresh token for this one.
        CancellationTokenSource newCts;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = newCts = new CancellationTokenSource();

        try
        {
            var probed = await _probeAll(newCts.Token).ConfigureAwait(false);
            timing?.SetOutcome($"reason={reason} probed={probed}");
        }
        catch (OperationCanceledException)
        {
            timing?.SetOutcome($"reason={reason} cancelled");
        }
        catch (Exception ex)
        {
            timing?.SetOutcome($"reason={reason} failed={ex.GetType().Name}");
            _log.LogWarning(ex, "EndpointHealthMonitor: {Reason} health sweep failed", reason);
        }
        finally
        {
            // Keep the latest CTS for the next run to cancel; only dispose this one if it was already replaced.
            if (!ReferenceEquals(_cts, newCts))
            {
                try { newCts.Dispose(); }
                catch { /* best effort */ }
            }

            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.IsEnabled = false;
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { /* best effort */ }
    }
}
