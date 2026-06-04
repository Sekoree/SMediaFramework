using HaPlay.Models;

namespace HaPlay.ControlGraph;

public sealed class ControlSystemRuntimeSession : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(100);

    private readonly TimeSpan _tickInterval;
    private CancellationTokenSource? _tickCts;
    private Task? _tickTask;
    private bool _disposed;

    public ControlSystemRuntimeSession(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        IControlOscSender oscSender,
        IControlMidiSender? midiSender = null,
        IControlMonitorSink? monitor = null,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit,
        TimeSpan? tickInterval = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sourceProvider);
        ArgumentNullException.ThrowIfNull(oscSender);

        _tickInterval = tickInterval is { } interval && interval > TimeSpan.Zero ? interval : DefaultTickInterval;
        Monitor = monitor ?? NullControlMonitorSink.Instance;
        ScriptSession = new ControlScriptRuntimeSession(
            config,
            sourceProvider,
            oscSender,
            instructionLimit,
            Monitor,
            midiSender);
        OscListeners = new ControlOscListenerManager(config, ScriptSession, Monitor);
        MidiDevices = new ControlMidiDeviceManager(config, ScriptSession, Monitor);
        PeriodicOscSends = new ControlPeriodicOscSendManager(config, oscSender, Monitor);
    }

    public IControlMonitorSink Monitor { get; }

    public ControlScriptRuntimeSession ScriptSession { get; }

    public ControlOscListenerManager OscListeners { get; }

    public ControlMidiDeviceManager MidiDevices { get; }

    public ControlPeriodicOscSendManager PeriodicOscSends { get; }

    /// <summary>True while the background tick loop is running.</summary>
    public bool IsTicking => _tickTask is { IsCompleted: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await OscListeners.StartAsync(cancellationToken).ConfigureAwait(false);
        StartTickLoop(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await StopTickLoopAsync(cancellationToken).ConfigureAwait(false);
        await OscListeners.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ControlSystemRuntimeTickResult> TickAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scriptResult = await ScriptSession.TickPeriodicAsync(utcNow, cancellationToken).ConfigureAwait(false);
        var periodicOscResults = await PeriodicOscSends.TickAsync(utcNow, cancellationToken).ConfigureAwait(false);
        return new ControlSystemRuntimeTickResult(scriptResult, periodicOscResults);
    }

    private void StartTickLoop(CancellationToken cancellationToken)
    {
        if (IsTicking)
            return;

        _tickCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tickTask = Task.Run(() => TickLoopAsync(_tickCts.Token), CancellationToken.None);
    }

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A faulted tick must not kill the loop; surface it and keep ticking.
                Monitor.Record(new ControlMonitorRecord
                {
                    Direction = ControlMonitorDirection.Error,
                    Protocol = ControlMonitorProtocol.Runtime,
                    Result = ControlMonitorResult.Failed,
                    Message = "Periodic tick failed.",
                    ErrorMessage = ex.Message,
                });
            }

            try
            {
                await Task.Delay(_tickInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task StopTickLoopAsync(CancellationToken cancellationToken)
    {
        var task = _tickTask;
        var cts = _tickCts;
        if (task is null)
            return;

        cts?.Cancel();
        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts?.Dispose();
            _tickCts = null;
            _tickTask = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopTickLoopAsync(CancellationToken.None).ConfigureAwait(false);
        await OscListeners.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _tickCts?.Cancel();
        _tickCts?.Dispose();
        _tickCts = null;
        _tickTask = null;
        OscListeners.Dispose();
    }
}

public sealed record ControlSystemRuntimeTickResult(
    ControlScriptRuntimeSessionResult ScriptResult,
    IReadOnlyList<ControlPeriodicOscSendResult> PeriodicOscResults);
