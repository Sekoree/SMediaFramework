using HaPlay.Models;

namespace HaPlay.ControlGraph;

public sealed class ControlScriptRuntimeSession
{
    private readonly ControlSystemConfig _config;
    private readonly BufferingControlScriptCommandSink _commandSink;
    private readonly ControlScriptRuntime _runtime;
    private readonly ControlScriptOscCommandRouter _oscRouter;
    private readonly ControlScriptMidiCommandRouter _midiRouter;
    private readonly IControlMonitorSink _monitor;
    private readonly ControlDeviceHealthRegistry _deviceHealth = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastPeriodicDispatch = new();

    private static readonly ControlScriptRuntimeSessionResult EmptyResult =
        new([], [], [], [], []);

    public ControlScriptRuntimeSession(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        IControlOscSender oscSender,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit,
        IControlMonitorSink? monitor = null,
        IControlMidiSender? midiSender = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(sourceProvider);
        ArgumentNullException.ThrowIfNull(oscSender);

        _monitor = monitor ?? NullControlMonitorSink.Instance;
        OscCache = new ControlValueCache();
        _commandSink = new BufferingControlScriptCommandSink();
        var services = new ControlScriptRuntimeServices(
            _commandSink,
            OscCache,
            monitor: _monitor,
            devices: _config.Devices,
            deviceHealth: _deviceHealth);
        _runtime = new ControlScriptRuntime(_config, sourceProvider, services, instructionLimit);
        _oscRouter = new ControlScriptOscCommandRouter(_config, oscSender, OscCache);
        _midiRouter = new ControlScriptMidiCommandRouter(_config, midiSender);
    }

    public ControlValueCache OscCache { get; }

    /// <summary>Latest reported device-session health, also surfaced to scripts via <c>HaPlay.Devices</c>.</summary>
    public ControlDeviceHealthRegistry DeviceHealth => _deviceHealth;

    public IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics => _runtime.Diagnostics;

    public IReadOnlyList<ControlScriptRuntimeScriptStatus> ScriptStatuses => _runtime.ScriptStatuses;

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchControlEventAsync(
        ControlEvent evt,
        CancellationToken cancellationToken = default) =>
        CompleteDispatchAsync(_runtime.DispatchControlEvent(evt), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceEnabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default) =>
        CompleteDispatchAsync(_runtime.DispatchDeviceEnabled(deviceInstanceId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceDisabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default) =>
        CompleteDispatchAsync(_runtime.DispatchDeviceDisabled(deviceInstanceId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerEnabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default) =>
        CompleteDispatchAsync(_runtime.DispatchLayerEnabled(layerId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerDisabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default) =>
        CompleteDispatchAsync(_runtime.DispatchLayerDisabled(layerId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchManualAsync(
        Guid? scriptId = null,
        Guid? triggerId = null,
        CancellationToken cancellationToken = default) =>
        CompleteDispatchAsync(_runtime.DispatchManual(scriptId, triggerId), cancellationToken);

    /// <summary>
    /// Reports a device session's current health. Device-health-changed triggers fire (and a monitor
    /// row is recorded) only when the session <see cref="ControlSessionState"/> actually transitions, so
    /// callers can report health on every poll without spamming scripts on detail-only updates.
    /// </summary>
    public ValueTask<ControlScriptRuntimeSessionResult> ReportDeviceHealthAsync(
        Guid deviceInstanceId,
        ControlSessionHealth health,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(health);

        var previousState = _deviceHealth.TryGet(deviceInstanceId)?.State;
        _deviceHealth.Report(deviceInstanceId, health);
        if (previousState == health.State)
            return ValueTask.FromResult(EmptyResult);

        RecordDeviceHealth(deviceInstanceId, health, previousState);
        return CompleteDispatchAsync(
            _runtime.DispatchDeviceHealthChanged(deviceInstanceId, health, previousState),
            cancellationToken);
    }

    private void RecordDeviceHealth(
        Guid deviceInstanceId,
        ControlSessionHealth health,
        ControlSessionState? previousState)
    {
        var faulted = health.State == ControlSessionState.Faulted;
        _monitor.Record(new ControlMonitorRecord
        {
            TimestampUtc = health.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : health.UpdatedAtUtc,
            Direction = faulted ? ControlMonitorDirection.Error : ControlMonitorDirection.Internal,
            Protocol = ControlMonitorProtocol.Runtime,
            Result = faulted ? ControlMonitorResult.Failed : ControlMonitorResult.Received,
            DeviceInstanceId = deviceInstanceId,
            Message = previousState.HasValue ? $"{previousState} -> {health.State}" : health.State.ToString(),
            ErrorMessage = faulted && !string.IsNullOrWhiteSpace(health.Detail) ? health.Detail : null,
        });
    }

    public async ValueTask<ControlScriptRuntimeSessionResult> TickPeriodicAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        var invocations = new List<ControlScriptInvocationRecord>();
        var diagnostics = new List<ControlScriptRuntimeDiagnostic>();

        foreach (var script in _config.Scripts)
        {
            if (!script.IsEnabled)
                continue;

            foreach (var trigger in script.Triggers.Where(t => t.Kind == ControlScriptTriggerKind.Periodic))
            {
                if (!IsPeriodicDue(trigger, utcNow))
                    continue;

                var dispatch = _runtime.DispatchPeriodic(script.Id, trigger.Id);
                invocations.AddRange(dispatch.Invocations);
                diagnostics.AddRange(dispatch.Diagnostics);
                if (dispatch.Invocations.Count > 0)
                    _lastPeriodicDispatch[trigger.Id] = utcNow;
            }
        }

        var oscRoutes = await FlushScriptOscCommandsAsync(cancellationToken).ConfigureAwait(false);
        var midiRoutes = await FlushScriptMidiCommandsAsync(cancellationToken).ConfigureAwait(false);
        RecordDispatch(invocations, diagnostics, oscRoutes, midiRoutes, cacheChanges: []);
        return new ControlScriptRuntimeSessionResult(invocations, diagnostics, oscRoutes, midiRoutes, CacheUpdates: []);
    }

    private bool IsPeriodicDue(ControlScriptTriggerConfig trigger, DateTimeOffset utcNow)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, trigger.IntervalMs ?? 1));
        return !_lastPeriodicDispatch.TryGetValue(trigger.Id, out var last)
            || utcNow - last >= interval;
    }

    private async ValueTask<ControlScriptRuntimeSessionResult> CompleteDispatchAsync(
        ControlScriptDispatchResult dispatch,
        CancellationToken cancellationToken)
    {
        var oscRoutes = await FlushScriptOscCommandsAsync(cancellationToken).ConfigureAwait(false);
        var midiRoutes = await FlushScriptMidiCommandsAsync(cancellationToken).ConfigureAwait(false);
        RecordDispatch(dispatch.Invocations, dispatch.Diagnostics, oscRoutes, midiRoutes, dispatch.CacheChanges);
        return new ControlScriptRuntimeSessionResult(
            dispatch.Invocations,
            dispatch.Diagnostics,
            oscRoutes,
            midiRoutes,
            dispatch.CacheChanges);
    }

    private ValueTask<IReadOnlyList<ControlScriptOscCommandRouteResult>> FlushScriptOscCommandsAsync(
        CancellationToken cancellationToken)
    {
        var messages = _commandSink.DrainOscMessages();
        return messages.Count == 0
            ? ValueTask.FromResult<IReadOnlyList<ControlScriptOscCommandRouteResult>>([])
            : _oscRouter.SendAllAsync(messages, cancellationToken);
    }

    private ValueTask<IReadOnlyList<ControlScriptMidiCommandRouteResult>> FlushScriptMidiCommandsAsync(
        CancellationToken cancellationToken)
    {
        var messages = _commandSink.DrainMidiMessages();
        return messages.Count == 0
            ? ValueTask.FromResult<IReadOnlyList<ControlScriptMidiCommandRouteResult>>([])
            : _midiRouter.SendAllAsync(messages, cancellationToken);
    }

    private void RecordDispatch(
        IReadOnlyList<ControlScriptInvocationRecord> invocations,
        IReadOnlyList<ControlScriptRuntimeDiagnostic> diagnostics,
        IReadOnlyList<ControlScriptOscCommandRouteResult> oscRoutes,
        IReadOnlyList<ControlScriptMidiCommandRouteResult> midiRoutes,
        IReadOnlyList<ControlValueCacheChange> cacheChanges)
    {
        foreach (var change in cacheChanges)
        {
            _monitor.Record(new ControlMonitorRecord
            {
                TimestampUtc = change.Timestamp,
                Direction = ControlMonitorDirection.Internal,
                Protocol = ControlMonitorProtocol.Cache,
                Result = ControlMonitorResult.Cached,
                DeviceInstanceId = Guid.TryParse(change.Key.DeviceKey, out var deviceId) ? deviceId : null,
                DeviceKey = change.Key.DeviceKey,
                Address = change.Key.Address,
                CorrelationId = change.CorrelationId,
                OscArguments = [ControlMonitorOscArgumentRecord.FromCachedValue(change.Value)],
                Message = change.Source.ToString(),
            });
        }

        foreach (var invocation in invocations)
        {
            _monitor.Record(new ControlMonitorRecord
            {
                Direction = invocation.Succeeded ? ControlMonitorDirection.Internal : ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Script,
                Result = invocation.Succeeded ? ControlMonitorResult.Invoked : ControlMonitorResult.Failed,
                ScriptId = invocation.ScriptId,
                TriggerId = invocation.TriggerId,
                Message = invocation.FunctionName,
            });
        }

        foreach (var diagnostic in diagnostics)
        {
            _monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Script,
                Result = ControlMonitorResult.Failed,
                ScriptId = diagnostic.ScriptId,
                TriggerId = diagnostic.TriggerId,
                Message = diagnostic.Stage.ToString(),
                ErrorMessage = diagnostic.Message,
            });
        }

        foreach (var route in oscRoutes)
        {
            _monitor.Record(new ControlMonitorRecord
            {
                Direction = route.Succeeded ? ControlMonitorDirection.Output : ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Osc,
                Result = route.Succeeded ? ControlMonitorResult.Sent : ControlMonitorResult.Failed,
                DeviceInstanceId = route.DeviceInstanceId,
                DeviceKey = route.Message.DeviceKey,
                Endpoint = route.Host is not null && route.Port.HasValue ? $"{route.Host}:{route.Port}" : null,
                RemoteHost = route.Host,
                RemotePort = route.Port,
                Address = route.Message.Address,
                OscArguments = route.Message.Arguments.Select(ControlMonitorOscArgumentRecord.FromScriptArgument).ToList(),
                ErrorMessage = route.ErrorMessage,
            });
        }

        foreach (var route in midiRoutes)
        {
            _monitor.Record(new ControlMonitorRecord
            {
                Direction = route.Succeeded ? ControlMonitorDirection.Output : ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.Midi,
                Result = route.Succeeded ? ControlMonitorResult.Sent : ControlMonitorResult.Failed,
                DeviceInstanceId = route.DeviceInstanceId,
                DeviceKey = route.Message.DeviceKey,
                Endpoint = route.OutputDeviceName ?? route.OutputDeviceId?.ToString(),
                MidiChannel = route.Message.Channel,
                MidiController = route.Message.Controller,
                MidiNote = route.Message.Note,
                MidiValue = route.Message.Value ?? route.Message.Velocity,
                MidiHighResolution14Bit = route.Message.HighResolution14Bit,
                Message = route.Message.Kind.ToString(),
                ErrorMessage = route.ErrorMessage,
            });
        }
    }
}

public sealed record ControlScriptRuntimeSessionResult(
    IReadOnlyList<ControlScriptInvocationRecord> Invocations,
    IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics,
    IReadOnlyList<ControlScriptOscCommandRouteResult> OscRoutes,
    IReadOnlyList<ControlScriptMidiCommandRouteResult> MidiRoutes,
    IReadOnlyList<ControlValueCacheChange> CacheUpdates);
