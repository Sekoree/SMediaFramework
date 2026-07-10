using OSCLib;

namespace S.Control;

public sealed class ControlSystemRuntimeSession : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(100);

    private readonly ControlSystemConfig _config;
    private readonly IControlMIDIDeviceSessionRunner? _midiSessions;
    private readonly IControlOSCReceiver? _oscReceiver;
    private readonly TimeSpan _tickInterval;
    private CancellationTokenSource? _tickCts;
    private Task? _tickTask;
    private bool _disposed;

    public ControlSystemRuntimeSession(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        IControlOSCSender oscSender,
        IControlMIDISender? midiSender = null,
        IControlMonitorSink? monitor = null,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit,
        TimeSpan? tickInterval = null,
        IControlMIDIDeviceSessionRunner? midiSessions = null,
        ControlMeterBlobDecoderRegistry? meterBlobDecoders = null,
        IControlShowActions? showActions = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sourceProvider);
        ArgumentNullException.ThrowIfNull(oscSender);

        _config = config;
        _midiSessions = midiSessions;
        _tickInterval = tickInterval is { } interval && interval > TimeSpan.Zero ? interval : DefaultTickInterval;
        Monitor = monitor ?? NullControlMonitorSink.Instance;
        ScriptSession = new ControlScriptRuntimeSession(
            config,
            sourceProvider,
            oscSender,
            instructionLimit,
            Monitor,
            midiSender,
            meterBlobDecoders,
            showActions);
        EventQueue = new ControlEventQueue(ScriptSession, Monitor);
        OSCListeners = new ControlOSCListenerManager(config, EventQueue, Monitor);
        MIDIDevices = new ControlMIDIDeviceManager(config, EventQueue, Monitor);
        PeriodicOSCSends = new ControlPeriodicOSCSendManager(config, oscSender, Monitor);

        // The X32 (OSC server) replies to the source port of our requests - i.e. the OSC client's own
        // connected socket, not a separate listener. Route those replies into the same dispatch the
        // listener uses (cache update, triggers, monitor) so requested/streamed values are received.
        if (oscSender is IControlOSCReceiver receiver)
        {
            _oscReceiver = receiver;
            receiver.MessageReceived += OnOSCReplyReceived;
        }
    }

    public IControlMonitorSink Monitor { get; }

    public ControlScriptRuntimeSession ScriptSession { get; }

    public ControlEventQueue EventQueue { get; }

    public ControlOSCListenerManager OSCListeners { get; }

    public ControlMIDIDeviceManager MIDIDevices { get; }

    public ControlPeriodicOSCSendManager PeriodicOSCSends { get; }

    /// <summary>True while the background tick loop is running.</summary>
    public bool IsTicking => _tickTask is { IsCompleted: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await OSCListeners.StartAsync(cancellationToken).ConfigureAwait(false);
            _midiSessions?.Start(MIDIDevices, cancellationToken);
            StartTickLoop(cancellationToken);

            // Fire LayerEnabled for the initially-active layer so layer-scoped scripts' LayerEnabled
            // handlers run on arm. The active layer is seeded in the runtime constructor without an event,
            // so without this a layer surface couldn't load the console's state until the operator switched
            // layers - which is why setups previously needed a periodic re-sync. Runs after the OSC/MIDI
            // sessions are up so the handler can request values and drive feedback. Script faults are
            // captured in the dispatch result (not thrown), so they won't abort the arm.
            if (ScriptSession.ActiveLayerId is { } activeLayerId)
                await EventQueue.DispatchLayerEnabledAsync(activeLayerId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await StopTickLoopAsync(cancellationToken).ConfigureAwait(false);
        _midiSessions?.Stop();
        await OSCListeners.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ControlSystemRuntimeTickResult> TickAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scriptResult = await EventQueue.TickPeriodicAsync(utcNow, cancellationToken).ConfigureAwait(false);
        var periodicOSCResults = await PeriodicOSCSends.TickAsync(utcNow, cancellationToken).ConfigureAwait(false);
        return new ControlSystemRuntimeTickResult(scriptResult, periodicOSCResults);
    }

    private void OnOSCReplyReceived(ControlOSCReceivedMessage message)
    {
        if (_disposed)
            return;

        // Resolve which device this reply belongs to (by the host/port we sent to) and dispatch it
        // directly - replies arrive on the client's own socket, so no app-level listener is required.
        var device = _config.Devices.FirstOrDefault(d =>
            d.Protocol == ControlDeviceProtocol.OSC
            && d.IsEnabled
            && d.Binding.OSCPort == message.Port
            && string.Equals(d.Binding.OSCHost, message.Host, StringComparison.OrdinalIgnoreCase));
        if (device is null)
            return;

        _ = DispatchReplyAsync(device, message.Context);
    }

    private async Task DispatchReplyAsync(ControlDeviceInstanceConfig device, OSCMessageContext context)
    {
        try
        {
            await OSCListeners.DispatchDeviceMessageAsync(device, context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.OSC,
                Result = ControlMonitorResult.Failed,
                Message = "OSC reply dispatch failed.",
                ErrorMessage = ex.Message,
            });
        }
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
        if (_oscReceiver is not null)
            _oscReceiver.MessageReceived -= OnOSCReplyReceived;
        await StopTickLoopAsync(CancellationToken.None).ConfigureAwait(false);
        _midiSessions?.Stop();
        if (_midiSessions is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (_midiSessions is IDisposable disposable)
            disposable.Dispose();
        await OSCListeners.DisposeAsync().ConfigureAwait(false);
        await EventQueue.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_oscReceiver is not null)
            _oscReceiver.MessageReceived -= OnOSCReplyReceived;
        _tickCts?.Cancel();
        _tickCts?.Dispose();
        _tickCts = null;
        _tickTask = null;
        _midiSessions?.Stop();
        if (_midiSessions is IDisposable disposable)
            disposable.Dispose();
        OSCListeners.Dispose();
        EventQueue.Dispose();
    }
}

public sealed record ControlSystemRuntimeTickResult(
    ControlScriptRuntimeSessionResult ScriptResult,
    IReadOnlyList<ControlPeriodicOSCSendResult> PeriodicOSCResults);
