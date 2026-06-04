using HaPlay.Models;
using Mond;
using OSCLib;

namespace HaPlay.ControlGraph;

public sealed class ControlScriptRuntime
{
    private readonly ControlSystemConfig _config;
    private readonly ControlScriptFileHost _host;
    private readonly Dictionary<Guid, ControlDeviceInstanceConfig> _devices;
    private readonly Dictionary<Guid, ControlLayerConfig> _layers;
    private readonly Dictionary<Guid, ScriptRuntimeState> _scripts;
    private readonly List<ControlScriptRuntimeDiagnostic> _diagnostics = new();
    private bool _faulted;

    public ControlScriptRuntime(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        ControlScriptRuntimeServices? runtimeServices = null,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(sourceProvider);

        _host = new ControlScriptFileHost(sourceProvider, instructionLimit, runtimeServices);
        _devices = config.Devices.ToDictionary(d => d.Id);
        _layers = config.Layers.ToDictionary(l => l.Id);
        _scripts = config.Scripts.ToDictionary(s => s.Id, s => new ScriptRuntimeState(s));

        LoadEnabledScripts();
    }

    public IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics => _diagnostics;

    public IReadOnlyList<ControlScriptRuntimeScriptStatus> ScriptStatuses =>
        _scripts.Values.Select(s => s.ToStatus()).ToArray();

    public ControlScriptRuntimeServices RuntimeServices => _host.RuntimeServices;

    public ControlScriptDispatchResult DispatchControlEvent(ControlEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        UpdateCaches(evt);

        var kind = evt switch
        {
            MidiControlEvent => ControlScriptTriggerKind.MidiControlChange,
            OscControlEvent => ControlScriptTriggerKind.OscMessage,
            _ => ControlScriptTriggerKind.Manual,
        };

        return Dispatch(kind, evt, evt.OriginId, layerId: null, triggerId: null);
    }

    public ControlScriptDispatchResult DispatchDeviceEnabled(Guid deviceInstanceId) =>
        DispatchLifecycle(ControlScriptTriggerKind.DeviceEnabled, deviceInstanceId, layerId: null);

    public ControlScriptDispatchResult DispatchDeviceDisabled(Guid deviceInstanceId)
    {
        RuntimeServices.OscCache.MarkDeviceStale(deviceInstanceId.ToString());
        if (_devices.TryGetValue(deviceInstanceId, out var device))
        {
            MarkDeviceCacheAliasesStale(device);
        }

        return DispatchLifecycle(ControlScriptTriggerKind.DeviceDisabled, deviceInstanceId, layerId: null);
    }

    public ControlScriptDispatchResult DispatchLayerEnabled(Guid layerId) =>
        DispatchLifecycle(ControlScriptTriggerKind.LayerEnabled, deviceInstanceId: null, layerId);

    public ControlScriptDispatchResult DispatchLayerDisabled(Guid layerId) =>
        DispatchLifecycle(ControlScriptTriggerKind.LayerDisabled, deviceInstanceId: null, layerId);

    public ControlScriptDispatchResult DispatchManual(Guid? scriptId = null, Guid? triggerId = null) =>
        Dispatch(ControlScriptTriggerKind.Manual, evt: null, deviceInstanceId: null, layerId: null, scriptId, triggerId);

    public ControlScriptDispatchResult DispatchPeriodic(Guid? scriptId = null, Guid? triggerId = null) =>
        Dispatch(ControlScriptTriggerKind.Periodic, evt: null, deviceInstanceId: null, layerId: null, scriptId, triggerId);

    private ControlScriptDispatchResult DispatchLifecycle(
        ControlScriptTriggerKind kind,
        Guid? deviceInstanceId,
        Guid? layerId) =>
        Dispatch(kind, evt: null, deviceInstanceId, layerId, scriptId: null, triggerId: null);

    private ControlScriptDispatchResult Dispatch(
        ControlScriptTriggerKind kind,
        ControlEvent? evt,
        Guid? deviceInstanceId,
        Guid? layerId,
        Guid? scriptId = null,
        Guid? triggerId = null)
    {
        var diagnosticsStart = _diagnostics.Count;
        var invocations = new List<ControlScriptInvocationRecord>();

        if (!_config.IsArmed || _faulted)
            return CreateResult(invocations, diagnosticsStart);

        foreach (var scriptState in _scripts.Values)
        {
            if (!CanRunScript(scriptState, scriptId, kind))
                continue;

            foreach (var trigger in scriptState.Config.Triggers)
            {
                if (!TriggerMatches(scriptState.Config, trigger, kind, evt, deviceInstanceId, layerId, triggerId))
                    continue;

                InvokeTrigger(scriptState, trigger, kind, evt, deviceInstanceId, layerId, invocations);
            }
        }

        return CreateResult(invocations, diagnosticsStart);
    }

    private ControlScriptDispatchResult CreateResult(
        IReadOnlyList<ControlScriptInvocationRecord> invocations,
        int diagnosticsStart) =>
        new(invocations, _diagnostics.Skip(diagnosticsStart).ToArray());

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
            ControlScriptScope.Layer => !script.LayerId.HasValue
                || kind == ControlScriptTriggerKind.LayerDisabled
                || IsLayerEnabled(script.LayerId.Value),
            _ => true,
        };
    }

    private bool TriggerMatches(
        ControlScriptConfig script,
        ControlScriptTriggerConfig trigger,
        ControlScriptTriggerKind kind,
        ControlEvent? evt,
        Guid? deviceInstanceId,
        Guid? layerId,
        Guid? requestedTriggerId)
    {
        if (requestedTriggerId.HasValue && trigger.Id != requestedTriggerId.Value)
            return false;
        if (trigger.Kind != kind
            && !(kind == ControlScriptTriggerKind.MidiControlChange
                && trigger.Kind == ControlScriptTriggerKind.MidiMessage
                && evt is MidiControlEvent))
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
            MidiControlEvent midi => MidiTriggerMatches(trigger, kind, midi),
            OscControlEvent osc => OscTriggerMatches(trigger, osc),
            null => true,
            _ => kind == ControlScriptTriggerKind.Manual,
        };
    }

    private static bool MidiTriggerMatches(ControlScriptTriggerConfig trigger, ControlScriptTriggerKind kind, MidiControlEvent midi)
    {
        if (kind is not (ControlScriptTriggerKind.MidiControlChange or ControlScriptTriggerKind.MidiMessage))
            return false;
        if (trigger.MidiChannel.HasValue && trigger.MidiChannel.Value != midi.Channel)
            return false;
        if (kind == ControlScriptTriggerKind.MidiControlChange && trigger.MidiController.HasValue && trigger.MidiController.Value != midi.Controller)
            return false;

        return true;
    }

    private static bool OscTriggerMatches(ControlScriptTriggerConfig trigger, OscControlEvent osc) =>
        OscAddressMatches(trigger.OscAddressPattern, osc.Address);

    private void InvokeTrigger(
        ScriptRuntimeState scriptState,
        ControlScriptTriggerConfig trigger,
        ControlScriptTriggerKind kind,
        ControlEvent? evt,
        Guid? deviceInstanceId,
        Guid? layerId,
        List<ControlScriptInvocationRecord> invocations)
    {
        var script = scriptState.Config;
        try
        {
            var eventObject = evt is not null
                ? ToMondEvent(scriptState.Module!.State, evt)
                : ToMondLifecycleEvent(scriptState.Module!.State, kind, deviceInstanceId, layerId);
            var contextObject = ToMondContext(scriptState.Module.State, script, trigger, deviceInstanceId, layerId);
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

    private void UpdateCaches(ControlEvent evt)
    {
        if (evt is not OscControlEvent osc)
            return;

        for (var i = 0; i < osc.Arguments.Count; i++)
        {
            var argument = osc.Arguments[i];
            var source = ControlValueCacheSource.Incoming;
            foreach (var deviceKey in GetDeviceCacheKeys(evt.OriginId))
            {
                switch (argument.Type)
                {
                    case OSCArgumentType.Float32:
                        RuntimeServices.OscCache.SetNumber(deviceKey, osc.Address, argument.AsFloat32(), source, i, evt.CorrelationId, evt.Timestamp);
                        break;
                    case OSCArgumentType.Double64:
                        RuntimeServices.OscCache.SetNumber(deviceKey, osc.Address, argument.AsDouble64(), source, i, evt.CorrelationId, evt.Timestamp);
                        break;
                    case OSCArgumentType.Int32:
                        RuntimeServices.OscCache.SetNumber(deviceKey, osc.Address, argument.AsInt32(), source, i, evt.CorrelationId, evt.Timestamp);
                        break;
                    case OSCArgumentType.Int64:
                        RuntimeServices.OscCache.SetNumber(deviceKey, osc.Address, argument.AsInt64(), source, i, evt.CorrelationId, evt.Timestamp);
                        break;
                    case OSCArgumentType.String or OSCArgumentType.Symbol:
                        RuntimeServices.OscCache.SetString(deviceKey, osc.Address, argument.AsString(), source, i, evt.CorrelationId, evt.Timestamp);
                        break;
                    case OSCArgumentType.True:
                        RuntimeServices.OscCache.SetBoolean(deviceKey, osc.Address, true, source, i, evt.CorrelationId, evt.Timestamp);
                        break;
                    case OSCArgumentType.False:
                        RuntimeServices.OscCache.SetBoolean(deviceKey, osc.Address, false, source, i, evt.CorrelationId, evt.Timestamp);
                        break;
                }
            }
        }
    }

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
        RuntimeServices.OscCache.MarkDeviceStale(device.Id.ToString());
        if (!string.IsNullOrWhiteSpace(device.Name))
            RuntimeServices.OscCache.MarkDeviceStale(device.Name);
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            RuntimeServices.OscCache.MarkDeviceStale(device.Binding.Alias);
        if (!string.IsNullOrWhiteSpace(device.ProfileId))
            RuntimeServices.OscCache.MarkDeviceStale(device.ProfileId);
    }

    private bool IsDeviceEnabled(Guid deviceInstanceId) =>
        _devices.TryGetValue(deviceInstanceId, out var device) && device.IsEnabled;

    private bool IsLayerEnabled(Guid layerId) =>
        _layers.TryGetValue(layerId, out var layer) && layer.IsEnabled;

    private void AddDiagnostic(Guid scriptId, Guid? triggerId, ControlScriptDiagnosticStage stage, string message) =>
        _diagnostics.Add(new ControlScriptRuntimeDiagnostic(scriptId, triggerId, stage, message));

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
            case MidiControlEvent midi:
                obj["type"] = "midi";
                var midiObj = MondValue.Object(state);
                midiObj["channel"] = midi.Channel;
                midiObj["controller"] = midi.Controller;
                midiObj["value"] = midi.Value;
                midiObj["highResolution14Bit"] = midi.HighResolution14Bit;
                obj["midi"] = midiObj;
                obj["value"] = midi.Value;
                break;
            case OscControlEvent osc:
                obj["type"] = "osc";
                var oscObj = MondValue.Object(state);
                oscObj["address"] = osc.Address;
                oscObj["args"] = MondValue.Array(osc.Arguments.Select(ToMondOscArgument));
                obj["osc"] = oscObj;
                if (TryGetOscScalar(osc.Arguments.FirstOrDefault(), out var value))
                    obj["value"] = value;
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

    private static MondValue ToMondOscArgument(OSCArgument argument) =>
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

    private static bool TryGetOscScalar(OSCArgument argument, out double value)
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

    private static bool OscAddressMatches(string? pattern, string address)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
            return true;
        if (string.Equals(pattern, address, StringComparison.Ordinal))
            return true;

        var starIndex = pattern.IndexOf('*', StringComparison.Ordinal);
        if (starIndex < 0)
            return false;

        var prefix = pattern[..starIndex];
        var suffix = pattern[(starIndex + 1)..];
        return address.StartsWith(prefix, StringComparison.Ordinal)
            && address.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static string ToTriggerName(ControlScriptTriggerKind kind) =>
        kind.ToString();

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
    IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics);

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
