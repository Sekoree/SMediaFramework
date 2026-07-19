using Mond;
using OSCLib;

namespace S.Control;

public sealed class ControlScriptRuntime
{
    private const int MaxRetainedDiagnostics = 4096;
    private const int DiagnosticTrimBatch = 512;

    private readonly ControlSystemConfig _config;
    private readonly IControlDeviceProfileRepository _profiles;
    private readonly ControlScriptFileHost _host;
    private readonly Dictionary<Guid, ControlDeviceInstanceConfig> _devices;
    private readonly Dictionary<Guid, ControlLayerConfig> _layers;
    private readonly Dictionary<Guid, ScriptRuntimeState> _scripts;
    private readonly List<ControlScriptRuntimeDiagnostic> _diagnostics = new();
    private long _diagnosticSequence;
    private long _droppedDiagnostics;
    private Guid? _activeLayerId;
    private bool _faulted;

    public ControlScriptRuntime(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        ControlScriptRuntimeServices? runtimeServices = null,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(sourceProvider);
        _profiles = CompositeControlDeviceProfileRepository.ForProject(config);

        _host = new ControlScriptFileHost(sourceProvider, instructionLimit, runtimeServices);
        _devices = config.Devices.ToDictionary(d => d.Id);
        _layers = config.Layers.ToDictionary(l => l.Id);
        _scripts = config.Scripts.ToDictionary(s => s.Id, s => new ScriptRuntimeState(s));

        // Layers are mutually exclusive: at most one active at a time. Seed from the config's enabled
        // layer (highest priority wins), then SetActiveLayer drives switching at runtime.
        _activeLayerId = config.Layers
            .Where(l => l.IsEnabled)
            .OrderByDescending(l => l.Priority)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefault();
        RuntimeServices.ActiveLayerProvider = () => _activeLayerId;

        LoadEnabledScripts();
    }

    /// <summary>The currently active layer (mutually exclusive), or null when none is active.</summary>
    public Guid? ActiveLayerId => _activeLayerId;

    /// <summary>Recent diagnostics, bounded so a failing keep-running script cannot grow for the session lifetime.</summary>
    public IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics => _diagnostics;

    public long DroppedDiagnostics => _droppedDiagnostics;

    public IReadOnlyList<ControlScriptRuntimeScriptStatus> ScriptStatuses =>
        _scripts.Values.Select(s => s.ToStatus()).ToArray();

    public ControlScriptRuntimeServices RuntimeServices => _host.RuntimeServices;

    public ControlScriptDispatchResult DispatchControlEvent(ControlEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var cacheChanges = UpdateCaches(evt);

        var kind = evt switch
        {
            MIDIControlEvent => ControlScriptTriggerKind.MIDIControlChange,
            MIDINoteControlEvent => ControlScriptTriggerKind.MIDINote,
            MIDIMessageControlEvent => ControlScriptTriggerKind.MIDIMessage,
            OSCControlEvent => ControlScriptTriggerKind.OSCMessage,
            _ => ControlScriptTriggerKind.Manual,
        };

        var dispatch = Dispatch(kind, evt, evt.OriginId, endpointId: evt.SourceNodeId, layerId: null, triggerId: null);

        if (cacheChanges.Count == 0)
            return dispatch with { CacheChanges = cacheChanges };

        var invocations = new List<ControlScriptInvocationRecord>(dispatch.Invocations);
        var diagnostics = new List<ControlScriptRuntimeDiagnostic>(dispatch.Diagnostics);
        foreach (var change in cacheChanges)
        {
            var cacheEvent = ToCacheChangedEvent(evt, change);
            var cacheDispatch = Dispatch(
                ControlScriptTriggerKind.OSCCacheChanged,
                cacheEvent,
                evt.OriginId,
                endpointId: evt.SourceNodeId,
                layerId: null,
                triggerId: null);
            invocations.AddRange(cacheDispatch.Invocations);
            diagnostics.AddRange(cacheDispatch.Diagnostics);
        }

        return new ControlScriptDispatchResult(invocations, diagnostics) { CacheChanges = cacheChanges };
    }

    public ControlScriptDispatchResult DispatchDeviceEnabled(Guid deviceInstanceId) =>
        DispatchLifecycle(ControlScriptTriggerKind.DeviceEnabled, deviceInstanceId, layerId: null);

    public ControlScriptDispatchResult DispatchDeviceDisabled(Guid deviceInstanceId)
    {
        RuntimeServices.OSCCache.MarkDeviceStale(deviceInstanceId.ToString());
        if (_devices.TryGetValue(deviceInstanceId, out var device))
        {
            MarkDeviceCacheAliasesStale(device);
        }

        return DispatchLifecycle(ControlScriptTriggerKind.DeviceDisabled, deviceInstanceId, layerId: null);
    }

    public ControlScriptDispatchResult DispatchDeviceHealthChanged(
        Guid deviceInstanceId,
        ControlSessionHealth health,
        ControlSessionState? previousState = null)
    {
        ArgumentNullException.ThrowIfNull(health);

        var evt = new DeviceHealthChangedControlEvent(
            health.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : health.UpdatedAtUtc,
            deviceInstanceId,
            deviceInstanceId,
            Guid.NewGuid(),
            deviceInstanceId,
            health.State,
            previousState,
            health.Detail);
        return Dispatch(ControlScriptTriggerKind.DeviceHealthChanged, evt, deviceInstanceId, endpointId: null, layerId: null, triggerId: null);
    }

    public ControlScriptDispatchResult DispatchLayerEnabled(Guid layerId) =>
        DispatchLifecycle(ControlScriptTriggerKind.LayerEnabled, deviceInstanceId: null, layerId);

    public ControlScriptDispatchResult DispatchLayerDisabled(Guid layerId) =>
        DispatchLifecycle(ControlScriptTriggerKind.LayerDisabled, deviceInstanceId: null, layerId);

    /// <summary>
    /// Switches the active layer (mutually exclusive). Deactivating the previous layer fires its
    /// <see cref="ControlScriptTriggerKind.LayerDisabled"/> triggers; activating the new one fires its
    /// <see cref="ControlScriptTriggerKind.LayerEnabled"/> triggers. Pass null to deactivate all layers.
    /// No-op when the layer is already active or the id is unknown.
    /// </summary>
    public ControlScriptDispatchResult SetActiveLayer(Guid? layerId)
    {
        if (layerId is { } requested && !_layers.ContainsKey(requested))
            return CreateResult([], _diagnosticSequence);
        if (_activeLayerId == layerId)
            return CreateResult([], _diagnosticSequence);

        var diagnosticsStart = _diagnosticSequence;
        var previous = _activeLayerId;
        _activeLayerId = layerId;

        var invocations = new List<ControlScriptInvocationRecord>();
        var cacheChanges = new List<ControlValueCacheChange>();

        if (previous is { } prev)
        {
            var disabled = DispatchLayerDisabled(prev);
            invocations.AddRange(disabled.Invocations);
            cacheChanges.AddRange(disabled.CacheChanges);
        }

        if (layerId is { } next)
        {
            var enabled = DispatchLayerEnabled(next);
            invocations.AddRange(enabled.Invocations);
            cacheChanges.AddRange(enabled.CacheChanges);
        }

        return new ControlScriptDispatchResult(invocations, DiagnosticsSince(diagnosticsStart))
        {
            CacheChanges = cacheChanges,
        };
    }

    public ControlScriptDispatchResult DispatchManual(Guid? scriptId = null, Guid? triggerId = null) =>
        Dispatch(ControlScriptTriggerKind.Manual, evt: null, deviceInstanceId: null, endpointId: null, layerId: null, scriptId, triggerId);

    public ControlScriptDispatchResult DispatchPeriodic(Guid? scriptId = null, Guid? triggerId = null) =>
        Dispatch(ControlScriptTriggerKind.Periodic, evt: null, deviceInstanceId: null, endpointId: null, layerId: null, scriptId, triggerId);

    private ControlScriptDispatchResult DispatchLifecycle(
        ControlScriptTriggerKind kind,
        Guid? deviceInstanceId,
        Guid? layerId) =>
        Dispatch(kind, evt: null, deviceInstanceId, endpointId: null, layerId, scriptId: null, triggerId: null);

    private ControlScriptDispatchResult Dispatch(
        ControlScriptTriggerKind kind,
        ControlEvent? evt,
        Guid? deviceInstanceId,
        Guid? endpointId,
        Guid? layerId,
        Guid? scriptId = null,
        Guid? triggerId = null)
    {
        var diagnosticsStart = _diagnosticSequence;
        var invocations = new List<ControlScriptInvocationRecord>();

        if (!_config.IsArmed || _faulted)
            return CreateResult(invocations, diagnosticsStart);

        foreach (var scriptState in _scripts.Values)
        {
            if (!CanRunScript(scriptState, scriptId, kind))
                continue;

            foreach (var trigger in scriptState.Config.Triggers)
            {
                if (!TriggerMatches(scriptState.Config, trigger, kind, evt, deviceInstanceId, endpointId, layerId, triggerId))
                    continue;

                InvokeTrigger(scriptState, trigger, kind, evt, deviceInstanceId, endpointId, layerId, invocations);
            }
        }

        return CreateResult(invocations, diagnosticsStart);
    }

    private ControlScriptDispatchResult CreateResult(
        IReadOnlyList<ControlScriptInvocationRecord> invocations,
        long diagnosticsStart) =>
        new(invocations, DiagnosticsSince(diagnosticsStart));

    private IReadOnlyList<ControlScriptRuntimeDiagnostic> DiagnosticsSince(long diagnosticsStart)
    {
        var added = Math.Min(Math.Max(0, _diagnosticSequence - diagnosticsStart), _diagnostics.Count);
        if (added == 0)
            return [];

        var result = new ControlScriptRuntimeDiagnostic[(int)added];
        _diagnostics.CopyTo(_diagnostics.Count - result.Length, result, 0, result.Length);
        return result;
    }

    private void LoadEnabledScripts()
    {
        foreach (var scriptState in _scripts.Values)
        {
            var script = scriptState.Config;
            if (!script.IsEnabled)
                continue;

            if (string.IsNullOrWhiteSpace(script.ScriptPath))
            {
                AddDiagnostic(script.Id, null, ControlScriptDiagnosticStage.Compile, "Script path is required.");
                scriptState.HasCompileError = true;
                continue;
            }

            try
            {
                scriptState.Module = _host.LoadModule(script.ScriptPath);
                ValidateTriggerBindings(scriptState);
            }
            catch (MondCompilerException ex)
            {
                AddCompileFailure(scriptState, ex.Message);
            }
            catch (MondRuntimeException ex)
            {
                AddCompileFailure(scriptState, ex.Message);
            }
            catch (ControlScriptException ex)
            {
                AddCompileFailure(scriptState, ex.Message);
            }
        }
    }

    private void ValidateTriggerBindings(ScriptRuntimeState scriptState)
    {
        if (scriptState.Module is null)
            return;

        foreach (var trigger in scriptState.Config.Triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.FunctionName))
            {
                scriptState.InvalidTriggerIds.Add(trigger.Id);
                AddDiagnostic(scriptState.Config.Id, trigger.Id, ControlScriptDiagnosticStage.Compile, "Trigger function name is required.");
                continue;
            }

            if (!scriptState.Module.TryGetExportedFunction(trigger.FunctionName, out _))
            {
                scriptState.InvalidTriggerIds.Add(trigger.Id);
                AddDiagnostic(
                    scriptState.Config.Id,
                    trigger.Id,
                    ControlScriptDiagnosticStage.Compile,
                    $"Script '{scriptState.Config.ScriptPath}' does not export function '{trigger.FunctionName}'.");
            }
        }
    }

    private void AddCompileFailure(ScriptRuntimeState scriptState, string message)
    {
        scriptState.HasCompileError = true;
        scriptState.LastError = message;
        AddDiagnostic(scriptState.Config.Id, null, ControlScriptDiagnosticStage.Compile, message);
    }

    private bool CanRunScript(ScriptRuntimeState scriptState, Guid? requestedScriptId, ControlScriptTriggerKind kind)
    {
        var script = scriptState.Config;
        if (requestedScriptId.HasValue && script.Id != requestedScriptId.Value)
            return false;
        if (!script.IsEnabled || scriptState.DisabledByFailure || scriptState.HasCompileError || scriptState.Module is null)
            return false;
        if (!IsScopeEnabled(script, kind))
            return false;

        return true;
    }

    private bool IsScopeEnabled(ControlScriptConfig script, ControlScriptTriggerKind kind)
    {
        if (script.DeviceInstanceId.HasValue
            && kind != ControlScriptTriggerKind.DeviceDisabled
            && !IsDeviceEnabled(script.DeviceInstanceId.Value))
            return false;
        if (script.LayerId.HasValue
            && kind != ControlScriptTriggerKind.LayerDisabled
            && !IsLayerEnabled(script.LayerId.Value))
            return false;

        return script.Scope switch
        {
            ControlScriptScope.Device => !script.DeviceInstanceId.HasValue
                || kind == ControlScriptTriggerKind.DeviceDisabled
                || IsDeviceEnabled(script.DeviceInstanceId.Value),
            // A layer-scoped script needs a layer to belong to: with no layer assigned it is
            // unconfigured and stays inert rather than firing globally.
            ControlScriptScope.Layer => script.LayerId.HasValue
                && (kind == ControlScriptTriggerKind.LayerDisabled
                    || IsLayerEnabled(script.LayerId.Value)),
            _ => true,
        };
    }

    private bool TriggerMatches(
        ControlScriptConfig script,
        ControlScriptTriggerConfig trigger,
        ControlScriptTriggerKind kind,
        ControlEvent? evt,
        Guid? deviceInstanceId,
        Guid? endpointId,
        Guid? layerId,
        Guid? requestedTriggerId)
    {
        if (requestedTriggerId.HasValue && trigger.Id != requestedTriggerId.Value)
            return false;
        if (!TriggerKindMatches(trigger.Kind, kind, evt))
        {
            return false;
        }

        if (_scripts[script.Id].InvalidTriggerIds.Contains(trigger.Id))
            return false;

        var effectiveDeviceId = deviceInstanceId ?? trigger.DeviceInstanceId ?? script.DeviceInstanceId;
        if (trigger.DeviceInstanceId.HasValue && deviceInstanceId.HasValue && trigger.DeviceInstanceId.Value != deviceInstanceId.Value)
            return false;
        if (script.DeviceInstanceId.HasValue && deviceInstanceId.HasValue && script.DeviceInstanceId.Value != deviceInstanceId.Value)
            return false;
        if (effectiveDeviceId.HasValue
            && kind != ControlScriptTriggerKind.DeviceDisabled
            && !IsDeviceEnabled(effectiveDeviceId.Value))
            return false;

        var effectiveEndpointId = endpointId ?? trigger.EndpointInstanceId ?? script.EndpointInstanceId;
        if (trigger.EndpointInstanceId.HasValue && endpointId.HasValue && trigger.EndpointInstanceId.Value != endpointId.Value)
            return false;
        if (script.EndpointInstanceId.HasValue && endpointId.HasValue && script.EndpointInstanceId.Value != endpointId.Value)
            return false;
        if (endpointId is null
            && effectiveEndpointId.HasValue
            && kind is not (ControlScriptTriggerKind.Manual or ControlScriptTriggerKind.Periodic))
            return false;

        var effectiveLayerId = layerId ?? trigger.LayerId ?? script.LayerId;
        if (trigger.LayerId.HasValue && layerId.HasValue && trigger.LayerId.Value != layerId.Value)
            return false;
        if (script.LayerId.HasValue && layerId.HasValue && script.LayerId.Value != layerId.Value)
            return false;
        if (effectiveLayerId.HasValue
            && kind != ControlScriptTriggerKind.LayerDisabled
            && !IsLayerEnabled(effectiveLayerId.Value))
            return false;

        return evt switch
        {
            MIDIControlEvent midi => MIDITriggerMatches(trigger, kind, midi),
            MIDINoteControlEvent midi => MIDITriggerMatches(trigger, kind, midi),
            MIDIMessageControlEvent midi => MIDITriggerMatches(trigger, kind, midi.Message),
            OSCControlEvent osc => OSCTriggerMatches(trigger, osc),
            OSCCacheChangedControlEvent cacheChanged => kind == ControlScriptTriggerKind.OSCCacheChanged
                && OSCAddressMatches(trigger.OSCAddressPattern, cacheChanged.Address),
            DeviceHealthChangedControlEvent => kind == ControlScriptTriggerKind.DeviceHealthChanged,
            null => true,
            _ => kind == ControlScriptTriggerKind.Manual,
        };
    }

    private static bool TriggerKindMatches(
        ControlScriptTriggerKind triggerKind,
        ControlScriptTriggerKind eventKind,
        ControlEvent? evt)
    {
        if (triggerKind == eventKind)
            return true;

        if (triggerKind == ControlScriptTriggerKind.MIDIMessage
            && evt is MIDIControlEvent or MIDINoteControlEvent or MIDIMessageControlEvent)
            return true;

        return triggerKind switch
        {
            ControlScriptTriggerKind.MIDIControlChange => evt is MIDIMessageControlEvent
            {
                Message.MessageType: ControlMIDIMessageType.ControlChange
            },
            ControlScriptTriggerKind.MIDINote => evt is MIDIMessageControlEvent
            {
                Message.MessageType: ControlMIDIMessageType.NoteOn or ControlMIDIMessageType.NoteOff
            },
            _ => false,
        };
    }

    private static bool MIDITriggerMatches(ControlScriptTriggerConfig trigger, ControlScriptTriggerKind kind, MIDIControlEvent midi)
    {
        if (kind is not (ControlScriptTriggerKind.MIDIControlChange or ControlScriptTriggerKind.MIDIMessage))
            return false;
        if (trigger.MIDIMessageType.HasValue && trigger.MIDIMessageType.Value != ControlMIDIMessageType.ControlChange)
            return false;
        if (trigger.MIDIChannel.HasValue && trigger.MIDIChannel.Value != midi.Channel)
            return false;
        if (trigger.MIDIController.HasValue && trigger.MIDIController.Value != midi.Controller)
            return false;
        if (!MIDIValueMatches(trigger, midi.Value))
            return false;
        if (trigger.MIDIParameter.HasValue)
            return false;

        return true;
    }

    private static bool MIDITriggerMatches(ControlScriptTriggerConfig trigger, ControlScriptTriggerKind kind, MIDINoteControlEvent midi)
    {
        if (kind is not (ControlScriptTriggerKind.MIDINote or ControlScriptTriggerKind.MIDIMessage))
            return false;
        if (trigger.MIDIMessageType.HasValue)
        {
            var expected = midi.IsNoteOn ? ControlMIDIMessageType.NoteOn : ControlMIDIMessageType.NoteOff;
            if (trigger.MIDIMessageType.Value != expected)
                return false;
        }
        if (trigger.MIDIChannel.HasValue && trigger.MIDIChannel.Value != midi.Channel)
            return false;
        if (trigger.MIDINote.HasValue && trigger.MIDINote.Value != midi.Note)
            return false;
        if (!MIDIValueMatches(trigger, midi.Velocity))
            return false;
        if (trigger.MIDIParameter.HasValue)
            return false;

        return true;
    }

    private static bool MIDITriggerMatches(
        ControlScriptTriggerConfig trigger,
        ControlScriptTriggerKind kind,
        ControlMIDIMessagePayload midi)
    {
        if (kind is not (ControlScriptTriggerKind.MIDIMessage
            or ControlScriptTriggerKind.MIDIControlChange
            or ControlScriptTriggerKind.MIDINote))
            return false;
        if (kind == ControlScriptTriggerKind.MIDIControlChange
            && midi.MessageType != ControlMIDIMessageType.ControlChange)
            return false;
        if (kind == ControlScriptTriggerKind.MIDINote
            && midi.MessageType is not (ControlMIDIMessageType.NoteOn or ControlMIDIMessageType.NoteOff))
            return false;
        if (trigger.MIDIMessageType.HasValue && trigger.MIDIMessageType.Value != midi.MessageType)
            return false;
        if (trigger.MIDIChannel.HasValue && trigger.MIDIChannel.Value != midi.Channel)
            return false;
        if (trigger.MIDIController.HasValue && trigger.MIDIController.Value != midi.Controller)
            return false;
        if (trigger.MIDINote.HasValue && trigger.MIDINote.Value != midi.Note)
            return false;
        if (!MIDIValueMatches(trigger, midi.Value))
            return false;
        if (trigger.MIDIParameter.HasValue && trigger.MIDIParameter.Value != midi.Parameter)
            return false;

        return true;
    }

    private static bool MIDIValueMatches(ControlScriptTriggerConfig trigger, int? value)
    {
        if (trigger.MIDIValue.HasValue)
            return value.HasValue && trigger.MIDIValue.Value == value.Value;
        if (trigger.MIDIValueMin.HasValue && (!value.HasValue || value.Value < trigger.MIDIValueMin.Value))
            return false;
        if (trigger.MIDIValueMax.HasValue && (!value.HasValue || value.Value > trigger.MIDIValueMax.Value))
            return false;
        return true;
    }

    private static bool OSCTriggerMatches(ControlScriptTriggerConfig trigger, OSCControlEvent osc) =>
        OSCAddressMatches(trigger.OSCAddressPattern, osc.Address);

    private void InvokeTrigger(
        ScriptRuntimeState scriptState,
        ControlScriptTriggerConfig trigger,
        ControlScriptTriggerKind kind,
        ControlEvent? evt,
        Guid? deviceInstanceId,
        Guid? endpointId,
        Guid? layerId,
        List<ControlScriptInvocationRecord> invocations)
    {
        var script = scriptState.Config;
        RuntimeServices.StateStore.BeginInvocation(script.Id, deviceInstanceId ?? script.DeviceInstanceId);
        try
        {
            var eventObject = evt is not null
                ? ToMondEvent(scriptState.Module!.State, evt)
                : ToMondLifecycleEvent(scriptState.Module!.State, kind, deviceInstanceId, layerId);
            var contextObject = ToMondContext(scriptState.Module.State, script, trigger, deviceInstanceId, endpointId, layerId);
            scriptState.Module.Invoke(trigger.FunctionName, eventObject, contextObject);
            scriptState.ConsecutiveFailures = 0;
            scriptState.LastError = null;
            invocations.Add(new ControlScriptInvocationRecord(script.Id, trigger.Id, trigger.FunctionName, Succeeded: true));
        }
        catch (MondCompilerException ex)
        {
            RecordRuntimeFailure(scriptState, trigger, ex.Message, invocations);
        }
        catch (MondRuntimeException ex)
        {
            RecordRuntimeFailure(scriptState, trigger, ex.Message, invocations);
        }
        catch (ControlScriptException ex)
        {
            RecordRuntimeFailure(scriptState, trigger, ex.Message, invocations);
        }
        finally
        {
            RuntimeServices.StateStore.EndInvocation();
        }
    }

    private void RecordRuntimeFailure(
        ScriptRuntimeState scriptState,
        ControlScriptTriggerConfig trigger,
        string message,
        List<ControlScriptInvocationRecord> invocations)
    {
        var script = scriptState.Config;
        scriptState.ConsecutiveFailures += 1;
        scriptState.LastError = message;
        AddDiagnostic(script.Id, trigger.Id, ControlScriptDiagnosticStage.Runtime, message);
        ApplyFailurePolicy(scriptState);
        invocations.Add(new ControlScriptInvocationRecord(script.Id, trigger.Id, trigger.FunctionName, Succeeded: false));
    }

    private void ApplyFailurePolicy(ScriptRuntimeState failedScript)
    {
        var policy = failedScript.Config.FailurePolicy;
        var maxFailures = Math.Max(1, policy.MaxConsecutiveFailures);
        if (failedScript.ConsecutiveFailures < maxFailures)
            return;

        switch (policy.Mode)
        {
            case ControlScriptFailureMode.KeepRunning:
                break;
            case ControlScriptFailureMode.DisableScript:
                failedScript.DisabledByFailure = true;
                break;
            case ControlScriptFailureMode.DisableScope:
                DisableScope(failedScript.Config);
                break;
            case ControlScriptFailureMode.FaultControlSystem:
                _faulted = true;
                failedScript.DisabledByFailure = true;
                break;
        }
    }

    private void DisableScope(ControlScriptConfig failedScript)
    {
        foreach (var scriptState in _scripts.Values)
        {
            var script = scriptState.Config;
            if (script.Scope != failedScript.Scope)
                continue;
            if (script.DeviceInstanceId != failedScript.DeviceInstanceId)
                continue;
            if (script.EndpointInstanceId != failedScript.EndpointInstanceId)
                continue;
            if (script.LayerId != failedScript.LayerId)
                continue;

            scriptState.DisabledByFailure = true;
        }
    }

    private IReadOnlyList<ControlValueCacheChange> UpdateCaches(ControlEvent evt)
    {
        if (evt is not OSCControlEvent osc)
            return [];

        var changes = new List<ControlValueCacheChange>();
        var deviceKeys = GetDeviceCacheKeys(evt.OriginId).ToArray();

        for (var i = 0; i < osc.Arguments.Count; i++)
        {
            var argument = osc.Arguments[i];

            if (argument.Type == OSCArgumentType.Blob && ShouldDecodeMeterBlob(evt.OriginId))
            {
                // The resolved decoder owns its address convention (e.g. X32's /meters) and returns no readings
                // for blobs it doesn't recognize, in which case we fall through to normal caching.
                var meterChanges = ApplyMeterBlobCache(deviceKeys, osc, argument, i, evt);
                if (meterChanges.Count > 0)
                {
                    changes.AddRange(meterChanges);
                    continue;
                }
            }

            // Each incoming argument is mirrored to every device alias key (id, name,
            // alias, profile) so scripts can read it by any of them, but a single
            // logical change should only notify once. Use the canonical id key (first)
            // for change detection and ignore the duplicate writes to the aliases.
            ControlValueCacheChange? canonicalChange = null;
            for (var k = 0; k < deviceKeys.Length; k++)
            {
                var change = SetCacheValue(deviceKeys[k], osc.Address, argument, i, evt);
                if (k == 0)
                    canonicalChange = change;
            }

            if (canonicalChange is not null)
                changes.Add(canonicalChange);
        }

        return changes;
    }

    private bool ShouldDecodeMeterBlob(Guid deviceInstanceId)
    {
        if (!_devices.TryGetValue(deviceInstanceId, out var device))
            return false;

        return ResolveMeterDecoder(deviceInstanceId) is not null;
    }

    private IControlMeterBlobDecoder? ResolveMeterDecoder(Guid deviceInstanceId)
    {
        if (!_devices.TryGetValue(deviceInstanceId, out var device))
            return null;

        var profile = _profiles.FindById(device.ProfileId ?? string.Empty);
        return RuntimeServices.MeterBlobDecoders.Resolve(profile?.Behaviors?.MeterBlobDecoder);
    }

    private IReadOnlyList<ControlValueCacheChange> ApplyMeterBlobCache(
        string[] deviceKeys,
        OSCControlEvent osc,
        OSCArgument argument,
        int argumentIndex,
        ControlEvent evt)
    {
        var decoder = ResolveMeterDecoder(evt.OriginId);
        if (decoder is null)
            return [];

        const ControlValueCacheSource source = ControlValueCacheSource.Incoming;
        var cache = RuntimeServices.OSCCache;
        var changes = new List<ControlValueCacheChange>();

        foreach (var entry in decoder.Decode(osc.Address, osc.Arguments, argumentIndex, argument.AsBlob()))
        {
            ControlValueCacheChange? canonicalChange = null;
            for (var k = 0; k < deviceKeys.Length; k++)
            {
                var change = cache.SetNumber(
                    deviceKeys[k],
                    entry.Address,
                    entry.Value,
                    source,
                    argumentIndex: 0,
                    evt.CorrelationId,
                    evt.Timestamp);
                if (k == 0)
                    canonicalChange = change;
            }

            if (canonicalChange is not null)
                changes.Add(canonicalChange);
        }

        return changes;
    }

    private ControlValueCacheChange? SetCacheValue(
        string deviceKey,
        string address,
        OSCArgument argument,
        int argumentIndex,
        ControlEvent evt)
    {
        const ControlValueCacheSource source = ControlValueCacheSource.Incoming;
        var cache = RuntimeServices.OSCCache;
        return argument.Type switch
        {
            OSCArgumentType.Float32 => cache.SetNumber(deviceKey, address, argument.AsFloat32(), source, argumentIndex, evt.CorrelationId, evt.Timestamp),
            OSCArgumentType.Double64 => cache.SetNumber(deviceKey, address, argument.AsDouble64(), source, argumentIndex, evt.CorrelationId, evt.Timestamp),
            OSCArgumentType.Int32 => cache.SetNumber(deviceKey, address, argument.AsInt32(), source, argumentIndex, evt.CorrelationId, evt.Timestamp),
            OSCArgumentType.Int64 => cache.SetNumber(deviceKey, address, argument.AsInt64(), source, argumentIndex, evt.CorrelationId, evt.Timestamp),
            OSCArgumentType.String or OSCArgumentType.Symbol => cache.SetString(deviceKey, address, argument.AsString(), source, argumentIndex, evt.CorrelationId, evt.Timestamp),
            OSCArgumentType.True => cache.SetBoolean(deviceKey, address, true, source, argumentIndex, evt.CorrelationId, evt.Timestamp),
            OSCArgumentType.False => cache.SetBoolean(deviceKey, address, false, source, argumentIndex, evt.CorrelationId, evt.Timestamp),
            _ => null,
        };
    }

    private static OSCCacheChangedControlEvent ToCacheChangedEvent(ControlEvent evt, ControlValueCacheChange change) =>
        new(
            change.Timestamp,
            evt.SourceNodeId,
            evt.OriginId,
            change.CorrelationId ?? evt.CorrelationId,
            change.Key.DeviceKey,
            change.Key.Address,
            change.Key.ArgumentIndex,
            change.Value,
            change.Source);

    private IEnumerable<string> GetDeviceCacheKeys(Guid deviceInstanceId)
    {
        yield return deviceInstanceId.ToString();

        if (!_devices.TryGetValue(deviceInstanceId, out var device))
            yield break;

        if (!string.IsNullOrWhiteSpace(device.Name))
            yield return device.Name;
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            yield return device.Binding.Alias;
        if (!string.IsNullOrWhiteSpace(device.ProfileId))
            yield return device.ProfileId;
    }

    private void MarkDeviceCacheAliasesStale(ControlDeviceInstanceConfig device)
    {
        RuntimeServices.OSCCache.MarkDeviceStale(device.Id.ToString());
        if (!string.IsNullOrWhiteSpace(device.Name))
            RuntimeServices.OSCCache.MarkDeviceStale(device.Name);
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            RuntimeServices.OSCCache.MarkDeviceStale(device.Binding.Alias);
        if (!string.IsNullOrWhiteSpace(device.ProfileId))
            RuntimeServices.OSCCache.MarkDeviceStale(device.ProfileId);
    }

    private bool IsDeviceEnabled(Guid deviceInstanceId) =>
        _devices.TryGetValue(deviceInstanceId, out var device) && device.IsEnabled;

    private bool IsLayerEnabled(Guid layerId) =>
        _activeLayerId == layerId;

    private void AddDiagnostic(Guid scriptId, Guid? triggerId, ControlScriptDiagnosticStage stage, string message)
    {
        if (_diagnostics.Count >= MaxRetainedDiagnostics)
        {
            var remove = Math.Min(DiagnosticTrimBatch, _diagnostics.Count);
            _diagnostics.RemoveRange(0, remove);
            _droppedDiagnostics += remove;
        }

        _diagnostics.Add(new ControlScriptRuntimeDiagnostic(scriptId, triggerId, stage, message));
        _diagnosticSequence++;
    }

    private static MondValue ToMondLifecycleEvent(MondState state, ControlScriptTriggerKind kind, Guid? deviceInstanceId, Guid? layerId)
    {
        var obj = MondValue.Object(state);
        obj["type"] = ToTriggerName(kind);
        obj["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
        if (deviceInstanceId.HasValue)
            obj["deviceInstanceId"] = deviceInstanceId.Value.ToString();
        if (layerId.HasValue)
            obj["layerId"] = layerId.Value.ToString();
        return obj;
    }

    private static MondValue ToMondContext(
        MondState state,
        ControlScriptConfig script,
        ControlScriptTriggerConfig trigger,
        Guid? deviceInstanceId,
        Guid? endpointId,
        Guid? layerId)
    {
        var context = MondValue.Object(state);
        context["scriptId"] = script.Id.ToString();
        context["scriptName"] = script.Name;
        context["triggerId"] = trigger.Id.ToString();
        context["triggerKind"] = ToTriggerName(trigger.Kind);
        context["scope"] = script.Scope.ToString();
        if (deviceInstanceId.HasValue)
            context["deviceInstanceId"] = deviceInstanceId.Value.ToString();
        if (endpointId.HasValue)
            context["endpointInstanceId"] = endpointId.Value.ToString();
        if (layerId.HasValue)
            context["layerId"] = layerId.Value.ToString();
        return context;
    }

    private static MondValue ToMondEvent(MondState state, ControlEvent evt)
    {
        var obj = MondValue.Object(state);
        obj["timestamp"] = evt.Timestamp.ToString("O");
        obj["sourceNodeId"] = evt.SourceNodeId.ToString();
        obj["originId"] = evt.OriginId.ToString();
        obj["correlationId"] = evt.CorrelationId.ToString();

        switch (evt)
        {
            case MIDIControlEvent midi:
                obj["type"] = "midi";
                var midiObj = MondValue.Object(state);
                midiObj["message"] = "controlChange";
                midiObj["channel"] = midi.Channel;
                midiObj["controller"] = midi.Controller;
                midiObj["value"] = midi.Value;
                midiObj["highResolution14Bit"] = midi.HighResolution14Bit;
                obj["midi"] = midiObj;
                obj["value"] = midi.Value;
                break;
            case MIDINoteControlEvent midi:
                obj["type"] = "midi";
                var midiNoteObj = MondValue.Object(state);
                midiNoteObj["message"] = midi.IsNoteOn ? "noteOn" : "noteOff";
                midiNoteObj["channel"] = midi.Channel;
                midiNoteObj["note"] = midi.Note;
                midiNoteObj["velocity"] = midi.Velocity;
                midiNoteObj["isNoteOn"] = midi.IsNoteOn;
                obj["midi"] = midiNoteObj;
                obj["value"] = midi.Velocity;
                break;
            case MIDIMessageControlEvent midi:
                obj["type"] = "midi";
                AddMIDIPayload(state, obj, midi.Message);
                break;
            case OSCControlEvent osc:
                obj["type"] = "osc";
                var oscObj = MondValue.Object(state);
                oscObj["address"] = osc.Address;
                oscObj["args"] = MondValue.Array(osc.Arguments.Select(ToMondOSCArgument));
                obj["osc"] = oscObj;
                if (TryGetOSCScalar(osc.Arguments.FirstOrDefault(), out var value))
                    obj["value"] = value;
                break;
            case OSCCacheChangedControlEvent cacheChanged:
                obj["type"] = "oscCacheChanged";
                obj["deviceKey"] = cacheChanged.DeviceKey;
                obj["source"] = cacheChanged.Source.ToString();
                var cacheObj = MondValue.Object(state);
                cacheObj["address"] = cacheChanged.Address;
                cacheObj["argumentIndex"] = cacheChanged.ArgumentIndex;
                obj["osc"] = cacheObj;
                obj["value"] = ToMondCachedValue(cacheChanged.Value);
                break;
            case DeviceHealthChangedControlEvent health:
                obj["type"] = "deviceHealthChanged";
                obj["deviceInstanceId"] = health.DeviceInstanceId.ToString();
                obj["state"] = health.State.ToString();
                if (health.PreviousState.HasValue)
                    obj["previousState"] = health.PreviousState.Value.ToString();
                obj["detail"] = health.Detail;
                break;
            case ScalarControlEvent scalar:
                obj["type"] = "scalar";
                obj["value"] = scalar.Value;
                break;
            case TextControlEvent text:
                obj["type"] = "text";
                obj["value"] = text.Value;
                break;
            default:
                obj["type"] = "unknown";
                break;
        }

        return obj;
    }

    private static void AddMIDIPayload(MondState state, MondValue eventObject, ControlMIDIMessagePayload midi)
    {
        var midiObj = MondValue.Object(state);
        midiObj["message"] = ToMIDIMessageName(midi.MessageType);
        midiObj["messageType"] = midi.MessageType.ToString();
        if (midi.Channel.HasValue)
            midiObj["channel"] = midi.Channel.Value;
        if (midi.Controller.HasValue)
            midiObj["controller"] = midi.Controller.Value;
        if (midi.Note.HasValue)
            midiObj["note"] = midi.Note.Value;
        if (midi.Value.HasValue)
        {
            midiObj["value"] = midi.Value.Value;
            eventObject["value"] = midi.Value.Value;
        }
        if (midi.Velocity.HasValue)
            midiObj["velocity"] = midi.Velocity.Value;
        if (midi.IsNoteOn.HasValue)
            midiObj["isNoteOn"] = midi.IsNoteOn.Value;
        if (midi.HighResolution14Bit)
            midiObj["highResolution14Bit"] = true;
        if (midi.Program.HasValue)
            midiObj["program"] = midi.Program.Value;
        if (midi.Pressure.HasValue)
            midiObj["pressure"] = midi.Pressure.Value;
        if (midi.PitchBend.HasValue)
            midiObj["pitchBend"] = midi.PitchBend.Value;
        if (midi.SongPosition.HasValue)
            midiObj["songPosition"] = midi.SongPosition.Value;
        if (midi.Song.HasValue)
            midiObj["song"] = midi.Song.Value;
        if (midi.DataByte.HasValue)
            midiObj["dataByte"] = midi.DataByte.Value;
        if (midi.Parameter.HasValue)
            midiObj["parameter"] = midi.Parameter.Value;
        if (midi.Data is { Length: > 0 } data)
        {
            midiObj["data"] = MondValue.Array(data.Select(b => (MondValue)(double)b));
            midiObj["length"] = data.Length;
            eventObject["value"] = data.Length;
        }

        eventObject["midi"] = midiObj;
    }

    private static MondValue ToMondCachedValue(ControlCachedValue value) =>
        value.Kind switch
        {
            ControlCachedValueKind.Number => value.NumberValue,
            ControlCachedValueKind.String => value.StringValue ?? string.Empty,
            ControlCachedValueKind.Boolean => value.BooleanValue ? MondValue.True : MondValue.False,
            _ => MondValue.Undefined,
        };

    private static MondValue ToMondOSCArgument(OSCArgument argument) =>
        argument.Type switch
        {
            OSCArgumentType.Float32 => argument.AsFloat32(),
            OSCArgumentType.Double64 => argument.AsDouble64(),
            OSCArgumentType.Int32 => argument.AsInt32(),
            OSCArgumentType.Int64 => argument.AsInt64(),
            OSCArgumentType.String or OSCArgumentType.Symbol => argument.AsString(),
            OSCArgumentType.True => MondValue.True,
            OSCArgumentType.False => MondValue.False,
            _ => MondValue.Undefined,
        };

    private static bool TryGetOSCScalar(OSCArgument argument, out double value)
    {
        switch (argument.Type)
        {
            case OSCArgumentType.Float32:
                value = argument.AsFloat32();
                return true;
            case OSCArgumentType.Double64:
                value = argument.AsDouble64();
                return true;
            case OSCArgumentType.Int32:
                value = argument.AsInt32();
                return true;
            case OSCArgumentType.Int64:
                value = argument.AsInt64();
                return true;
            case OSCArgumentType.True:
                value = 1;
                return true;
            case OSCArgumentType.False:
                value = 0;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    private static bool OSCAddressMatches(string? pattern, string address) =>
        ControlOSCAddressPattern.Matches(pattern, address);

    private static string ToTriggerName(ControlScriptTriggerKind kind) =>
        kind.ToString();

    private static string ToMIDIMessageName(ControlMIDIMessageType type) =>
        type switch
        {
            ControlMIDIMessageType.NRPN => "nrpn",
            ControlMIDIMessageType.RPN => "rpn",
            ControlMIDIMessageType.NoteOff => "noteOff",
            ControlMIDIMessageType.NoteOn => "noteOn",
            ControlMIDIMessageType.PolyphonicAftertouch => "polyphonicAftertouch",
            ControlMIDIMessageType.ControlChange => "controlChange",
            ControlMIDIMessageType.ProgramChange => "programChange",
            ControlMIDIMessageType.ChannelAftertouch => "channelAftertouch",
            ControlMIDIMessageType.PitchBend => "pitchBend",
            ControlMIDIMessageType.SysEx => "sysEx",
            ControlMIDIMessageType.MIDITimeCode => "midiTimeCode",
            ControlMIDIMessageType.SongPosition => "songPosition",
            ControlMIDIMessageType.SongSelect => "songSelect",
            ControlMIDIMessageType.TuneRequest => "tuneRequest",
            ControlMIDIMessageType.TimingClock => "timingClock",
            ControlMIDIMessageType.Start => "start",
            ControlMIDIMessageType.Continue => "continue",
            ControlMIDIMessageType.Stop => "stop",
            ControlMIDIMessageType.ActiveSensing => "activeSensing",
            ControlMIDIMessageType.Reset => "reset",
            _ => "unknown",
        };

    private sealed class ScriptRuntimeState
    {
        public ScriptRuntimeState(ControlScriptConfig config)
        {
            Config = config;
        }

        public ControlScriptConfig Config { get; }

        public ControlScriptModule? Module { get; set; }

        public HashSet<Guid> InvalidTriggerIds { get; } = new();

        public int ConsecutiveFailures { get; set; }

        public bool DisabledByFailure { get; set; }

        public bool HasCompileError { get; set; }

        public string? LastError { get; set; }

        public ControlScriptRuntimeScriptStatus ToStatus() =>
            new(
                Config.Id,
                Config.Name,
                Config.IsEnabled && !DisabledByFailure && !HasCompileError,
                ConsecutiveFailures,
                DisabledByFailure,
                HasCompileError,
                LastError);
    }
}

public sealed record ControlScriptDispatchResult(
    IReadOnlyList<ControlScriptInvocationRecord> Invocations,
    IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics)
{
    public IReadOnlyList<ControlValueCacheChange> CacheChanges { get; init; } = [];
}

public sealed record ControlScriptInvocationRecord(
    Guid ScriptId,
    Guid TriggerId,
    string FunctionName,
    bool Succeeded);

public sealed record ControlScriptRuntimeDiagnostic(
    Guid ScriptId,
    Guid? TriggerId,
    ControlScriptDiagnosticStage Stage,
    string Message);

public sealed record ControlScriptRuntimeScriptStatus(
    Guid ScriptId,
    string Name,
    bool IsRunnable,
    int ConsecutiveFailures,
    bool DisabledByFailure,
    bool HasCompileError,
    string? LastError);
