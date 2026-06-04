using HaPlay.Models;

namespace HaPlay.ControlGraph;

public sealed class ControlScriptRuntimeSession
{
    private readonly ControlSystemConfig _config;
    private readonly BufferingControlScriptCommandSink _commandSink;
    private readonly ControlScriptRuntime _runtime;
    private readonly ControlScriptOscCommandRouter _oscRouter;
    private readonly Dictionary<Guid, DateTimeOffset> _lastPeriodicDispatch = new();

    public ControlScriptRuntimeSession(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        IControlOscSender oscSender,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        ArgumentNullException.ThrowIfNull(sourceProvider);
        ArgumentNullException.ThrowIfNull(oscSender);

        OscCache = new ControlValueCache();
        _commandSink = new BufferingControlScriptCommandSink();
        var services = new ControlScriptRuntimeServices(_commandSink, OscCache);
        _runtime = new ControlScriptRuntime(_config, sourceProvider, services, instructionLimit);
        _oscRouter = new ControlScriptOscCommandRouter(_config, oscSender, OscCache);
    }

    public ControlValueCache OscCache { get; }

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

        var routes = await FlushScriptOscCommandsAsync(cancellationToken).ConfigureAwait(false);
        return new ControlScriptRuntimeSessionResult(invocations, diagnostics, routes);
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
        var routes = await FlushScriptOscCommandsAsync(cancellationToken).ConfigureAwait(false);
        return new ControlScriptRuntimeSessionResult(dispatch.Invocations, dispatch.Diagnostics, routes);
    }

    private ValueTask<IReadOnlyList<ControlScriptOscCommandRouteResult>> FlushScriptOscCommandsAsync(
        CancellationToken cancellationToken)
    {
        var messages = _commandSink.DrainOscMessages();
        return messages.Count == 0
            ? ValueTask.FromResult<IReadOnlyList<ControlScriptOscCommandRouteResult>>([])
            : _oscRouter.SendAllAsync(messages, cancellationToken);
    }
}

public sealed record ControlScriptRuntimeSessionResult(
    IReadOnlyList<ControlScriptInvocationRecord> Invocations,
    IReadOnlyList<ControlScriptRuntimeDiagnostic> Diagnostics,
    IReadOnlyList<ControlScriptOscCommandRouteResult> OscRoutes);
