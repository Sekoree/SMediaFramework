
namespace S.Control;

public sealed class ControlScriptRuntimeSession : IControlScriptDispatcher
{
    private readonly ControlSystemConfig _config;
    private readonly BufferingControlScriptCommandSink _commandSink;
    private readonly ControlScriptRuntime _runtime;
    private readonly ControlScriptOSCCommandRouter _oscRouter;
    private readonly ControlScriptMIDICommandRouter _midiRouter;
    private readonly IControlMonitorSink _monitor;
    private readonly ControlDeviceHealthRegistry _deviceHealth = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastPeriodicDispatch = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    private static readonly ControlScriptRuntimeSessionResult EmptyResult =
        new([], [], [], [], []);

    public ControlScriptRuntimeSession(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        IControlOSCSender oscSender,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit,
        IControlMonitorSink? monitor = null,
        IControlMIDISender? midiSender = null,
        ControlMeterBlobDecoderRegistry? meterBlobDecoders = null,
        IControlShowActions? showActions = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(sourceProvider);
        ArgumentNullException.ThrowIfNull(oscSender);

        _monitor = monitor ?? NullControlMonitorSink.Instance;
        OSCCache = new ControlValueCache();
        _commandSink = new BufferingControlScriptCommandSink();
        var services = new ControlScriptRuntimeServices(
            _commandSink,
            OSCCache,
            monitor: _monitor,
            devices: _config.Devices,
            deviceHealth: _deviceHealth,
            layers: _config.Layers,
            profiles: ResolveProfiles(_config),
            meterBlobDecoders: meterBlobDecoders,
            showActions: showActions);
        _runtime = new ControlScriptRuntime(_config, sourceProvider, services, instructionLimit);
        _oscRouter = new ControlScriptOSCCommandRouter(_config, oscSender, OSCCache);
        _midiRouter = new ControlScriptMIDICommandRouter(_config, midiSender);
    }

    // The profiles scripts can read command data from: built-in JSON profiles plus any config overrides (by id).
    private static IReadOnlyList<ControlDeviceProfile> ResolveProfiles(ControlSystemConfig config)
    {
        var byId = new Dictionary<string, ControlDeviceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in BuiltInProfileLoader.Load())
            byId[profile.Id] = profile;
        foreach (var profile in config.DeviceProfileOverrides)
            byId[profile.Id] = profile;
        return byId.Values.ToArray();
    }

    public ControlValueCache OSCCache { get; }

    /// <summary>Latest reported device-session health, also surfaced to scripts via <c>HaPlay.Devices</c>.</summary>
    public ControlDeviceHealthRegistry DeviceHealth => _deviceHealth;

    public IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics => _runtime.Diagnostics;

    public IReadOnlyList<ControlScriptRuntimeScriptStatus> ScriptStatuses => _runtime.ScriptStatuses;

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchControlEventAsync(
        ControlEvent evt,
        CancellationToken cancellationToken = default) =>
        DispatchSerializedAsync(() => _runtime.DispatchControlEvent(evt), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceEnabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default) =>
        DispatchSerializedAsync(() => _runtime.DispatchDeviceEnabled(deviceInstanceId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceDisabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default) =>
        DispatchSerializedAsync(() => _runtime.DispatchDeviceDisabled(deviceInstanceId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerEnabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default) =>
        DispatchSerializedAsync(() => _runtime.DispatchLayerEnabled(layerId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerDisabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default) =>
        DispatchSerializedAsync(() => _runtime.DispatchLayerDisabled(layerId), cancellationToken);

    /// <summary>The currently active layer (mutually exclusive), or null.</summary>
    public Guid? ActiveLayerId => _runtime.ActiveLayerId;

    /// <summary>
    /// Switches the active layer (mutually exclusive), firing <c>LayerDisabled</c> for the previous layer
    /// and <c>LayerEnabled</c> for the new one, then flushing any OSC/MIDI those handlers queued.
    /// </summary>
    public ValueTask<ControlScriptRuntimeSessionResult> SetActiveLayerAsync(
        Guid? layerId,
        CancellationToken cancellationToken = default) =>
        DispatchSerializedAsync(() => _runtime.SetActiveLayer(layerId), cancellationToken);

    public ValueTask<ControlScriptRuntimeSessionResult> DispatchManualAsync(
        Guid? scriptId = null,
        Guid? triggerId = null,
        CancellationToken cancellationToken = default) =>
        DispatchSerializedAsync(() => _runtime.DispatchManual(scriptId, triggerId), cancellationToken);

    /// <summary>
    /// Reports a device session's current health. Device-health-changed triggers fire (and a monitor
    /// row is recorded) only when the session <see cref="ControlSessionState"/> actually transitions, so
    /// callers can report health on every poll without spamming scripts on detail-only updates.
    /// </summary>
    public async ValueTask<ControlScriptRuntimeSessionResult> ReportDeviceHealthAsync(
        Guid deviceInstanceId,
        ControlSessionHealth health,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(health);

        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previousState = _deviceHealth.TryGet(deviceInstanceId)?.State;
            _deviceHealth.Report(deviceInstanceId, health);
            if (previousState == health.State)
                return EmptyResult;

            RecordDeviceHealth(deviceInstanceId, health, previousState);
            return await CompleteDispatchAsync(
                    _runtime.DispatchDeviceHealthChanged(deviceInstanceId, health, previousState),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _dispatchGate.Release();
        }
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
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var invocations = new List<ControlScriptInvocationRecord>();
            var diagnostics = new List<ControlScriptRuntimeDiagnostic>();
            var cacheChanges = new List<ControlValueCacheChange>();

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

            var (oscRoutes, midiRoutes) = await FlushAndApplyLayerSwitchesAsync(invocations, diagnostics, cacheChanges, cancellationToken)
                .ConfigureAwait(false);
            RecordDispatch(invocations, diagnostics, oscRoutes, midiRoutes, cacheChanges);
            return new ControlScriptRuntimeSessionResult(invocations, diagnostics, oscRoutes, midiRoutes, cacheChanges);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private bool IsPeriodicDue(ControlScriptTriggerConfig trigger, DateTimeOffset utcNow)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, trigger.IntervalMs ?? 1));
        return !_lastPeriodicDispatch.TryGetValue(trigger.Id, out var last)
            || utcNow - last >= interval;
    }

    /// <summary>Bounds runaway layer switching (e.g. a LayerEnabled handler that activates another layer).</summary>
    private const int MaxLayerSwitchDepth = 8;

    private async ValueTask<ControlScriptRuntimeSessionResult> DispatchSerializedAsync(
        Func<ControlScriptDispatchResult> dispatch,
        CancellationToken cancellationToken)
    {
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CompleteDispatchAsync(dispatch(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async ValueTask<ControlScriptRuntimeSessionResult> CompleteDispatchAsync(
        ControlScriptDispatchResult dispatch,
        CancellationToken cancellationToken)
    {
        var invocations = new List<ControlScriptInvocationRecord>(dispatch.Invocations);
        var diagnostics = new List<ControlScriptRuntimeDiagnostic>(dispatch.Diagnostics);
        var cacheChanges = new List<ControlValueCacheChange>(dispatch.CacheChanges);

        var (oscRoutes, midiRoutes) = await FlushAndApplyLayerSwitchesAsync(invocations, diagnostics, cacheChanges, cancellationToken)
            .ConfigureAwait(false);
        RecordDispatch(invocations, diagnostics, oscRoutes, midiRoutes, cacheChanges);
        return new ControlScriptRuntimeSessionResult(invocations, diagnostics, oscRoutes, midiRoutes, cacheChanges);
    }

    /// <summary>
    /// Drains and routes queued script OSC/MIDI, then applies any queued layer switches (which fire more
    /// scripts that may queue more commands), looping until quiet. Mutates the accumulator lists with the
    /// extra invocations/diagnostics/cache changes the layer switches produce.
    /// </summary>
    private async ValueTask<(IReadOnlyList<ControlScriptOSCCommandRouteResult> OSC, IReadOnlyList<ControlScriptMIDICommandRouteResult> MIDI)>
        FlushAndApplyLayerSwitchesAsync(
            List<ControlScriptInvocationRecord> invocations,
            List<ControlScriptRuntimeDiagnostic> diagnostics,
            List<ControlValueCacheChange> cacheChanges,
            CancellationToken cancellationToken)
    {
        var oscRoutes = new List<ControlScriptOSCCommandRouteResult>();
        var midiRoutes = new List<ControlScriptMIDICommandRouteResult>();

        for (var depth = 0; ; depth++)
        {
            oscRoutes.AddRange(await FlushScriptOSCCommandsAsync(cancellationToken).ConfigureAwait(false));
            midiRoutes.AddRange(await FlushScriptMIDICommandsAsync(cancellationToken).ConfigureAwait(false));

            var requests = _commandSink.DrainLayerActivations();
            if (requests.Count == 0 || depth >= MaxLayerSwitchDepth)
                break;

            foreach (var key in requests)
            {
                if (!TryResolveLayer(key, out var layerId))
                    continue;

                var switched = _runtime.SetActiveLayer(layerId);
                invocations.AddRange(switched.Invocations);
                diagnostics.AddRange(switched.Diagnostics);
                cacheChanges.AddRange(switched.CacheChanges);
            }
        }

        return (oscRoutes, midiRoutes);
    }

    private bool TryResolveLayer(string key, out Guid? layerId)
    {
        layerId = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (Guid.TryParse(key, out var id) && _config.Layers.Any(l => l.Id == id))
        {
            layerId = id;
            return true;
        }

        var byName = _config.Layers.FirstOrDefault(l => string.Equals(l.Name, key, StringComparison.OrdinalIgnoreCase));
        if (byName is null)
            return false;

        layerId = byName.Id;
        return true;
    }

    private ValueTask<IReadOnlyList<ControlScriptOSCCommandRouteResult>> FlushScriptOSCCommandsAsync(
        CancellationToken cancellationToken)
    {
        var messages = _commandSink.DrainOSCMessages();
        return messages.Count == 0
            ? ValueTask.FromResult<IReadOnlyList<ControlScriptOSCCommandRouteResult>>([])
            : _oscRouter.SendAllAsync(messages, cancellationToken);
    }

    private ValueTask<IReadOnlyList<ControlScriptMIDICommandRouteResult>> FlushScriptMIDICommandsAsync(
        CancellationToken cancellationToken)
    {
        var messages = _commandSink.DrainMIDIMessages();
        return messages.Count == 0
            ? ValueTask.FromResult<IReadOnlyList<ControlScriptMIDICommandRouteResult>>([])
            : _midiRouter.SendAllAsync(messages, cancellationToken);
    }

    private void RecordDispatch(
        IReadOnlyList<ControlScriptInvocationRecord> invocations,
        IReadOnlyList<ControlScriptRuntimeDiagnostic> diagnostics,
        IReadOnlyList<ControlScriptOSCCommandRouteResult> oscRoutes,
        IReadOnlyList<ControlScriptMIDICommandRouteResult> midiRoutes,
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
                OSCArguments = [ControlMonitorOSCArgumentRecord.FromCachedValue(change.Value)],
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
                Protocol = ControlMonitorProtocol.OSC,
                Result = route.Succeeded ? ControlMonitorResult.Sent : ControlMonitorResult.Failed,
                DeviceInstanceId = route.DeviceInstanceId,
                DeviceKey = route.Message.DeviceKey,
                Endpoint = route.Host is not null && route.Port.HasValue ? $"{route.Host}:{route.Port}" : null,
                RemoteHost = route.Host,
                RemotePort = route.Port,
                Address = route.Message.Address,
                OSCArguments = route.Message.Arguments.Select(ControlMonitorOSCArgumentRecord.FromScriptArgument).ToList(),
                ErrorMessage = route.ErrorMessage,
            });
        }

        foreach (var route in midiRoutes)
        {
            _monitor.Record(new ControlMonitorRecord
            {
                Direction = route.Succeeded ? ControlMonitorDirection.Output : ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.MIDI,
                Result = route.Succeeded ? ControlMonitorResult.Sent : ControlMonitorResult.Failed,
                DeviceInstanceId = route.DeviceInstanceId,
                DeviceKey = route.Message.DeviceKey,
                Endpoint = route.OutputDeviceName ?? route.OutputDeviceId?.ToString(),
                MIDIChannel = route.Message.Channel,
                MIDIMessageType = ToMIDIMessageType(route.Message.Kind),
                MIDIController = route.Message.Controller,
                MIDINote = route.Message.Note,
                MIDIValue = route.Message.Value ?? route.Message.Velocity ?? route.Message.Data?.Length,
                MIDIParameter = route.Message.Parameter,
                MIDIHighResolution14Bit = route.Message.HighResolution14Bit,
                Message = route.Message.Kind.ToString(),
                ErrorMessage = route.ErrorMessage,
                RawBytes = route.Message.Data,
            });
        }
    }

    private static ControlMIDIMessageType ToMIDIMessageType(ControlScriptMIDIMessageKind kind) =>
        kind switch
        {
            ControlScriptMIDIMessageKind.ControlChange => ControlMIDIMessageType.ControlChange,
            ControlScriptMIDIMessageKind.NoteOn => ControlMIDIMessageType.NoteOn,
            ControlScriptMIDIMessageKind.NoteOff => ControlMIDIMessageType.NoteOff,
            ControlScriptMIDIMessageKind.ProgramChange => ControlMIDIMessageType.ProgramChange,
            ControlScriptMIDIMessageKind.PitchBend => ControlMIDIMessageType.PitchBend,
            ControlScriptMIDIMessageKind.PolyphonicAftertouch => ControlMIDIMessageType.PolyphonicAftertouch,
            ControlScriptMIDIMessageKind.ChannelAftertouch => ControlMIDIMessageType.ChannelAftertouch,
            ControlScriptMIDIMessageKind.SysEx => ControlMIDIMessageType.SysEx,
            ControlScriptMIDIMessageKind.MIDITimeCode => ControlMIDIMessageType.MIDITimeCode,
            ControlScriptMIDIMessageKind.SongPosition => ControlMIDIMessageType.SongPosition,
            ControlScriptMIDIMessageKind.SongSelect => ControlMIDIMessageType.SongSelect,
            ControlScriptMIDIMessageKind.TuneRequest => ControlMIDIMessageType.TuneRequest,
            ControlScriptMIDIMessageKind.TimingClock => ControlMIDIMessageType.TimingClock,
            ControlScriptMIDIMessageKind.Start => ControlMIDIMessageType.Start,
            ControlScriptMIDIMessageKind.Continue => ControlMIDIMessageType.Continue,
            ControlScriptMIDIMessageKind.Stop => ControlMIDIMessageType.Stop,
            ControlScriptMIDIMessageKind.ActiveSensing => ControlMIDIMessageType.ActiveSensing,
            ControlScriptMIDIMessageKind.Reset => ControlMIDIMessageType.Reset,
            ControlScriptMIDIMessageKind.NRPN => ControlMIDIMessageType.NRPN,
            ControlScriptMIDIMessageKind.RPN => ControlMIDIMessageType.RPN,
            _ => ControlMIDIMessageType.Unknown,
        };
}

public sealed record ControlScriptRuntimeSessionResult(
    IReadOnlyList<ControlScriptInvocationRecord> Invocations,
    IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics,
    IReadOnlyList<ControlScriptOSCCommandRouteResult> OSCRoutes,
    IReadOnlyList<ControlScriptMIDICommandRouteResult> MIDIRoutes,
    IReadOnlyList<ControlValueCacheChange> CacheUpdates);
