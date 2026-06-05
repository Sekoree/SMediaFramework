using HaPlay.Models;

namespace HaPlay.ControlGraph;

public interface IControlScriptDispatcher
{
    Guid? ActiveLayerId { get; }

    ValueTask<ControlScriptRuntimeSessionResult> DispatchControlEventAsync(
        ControlEvent evt,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceEnabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> DispatchDeviceDisabledAsync(
        Guid deviceInstanceId,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerEnabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> DispatchLayerDisabledAsync(
        Guid layerId,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> SetActiveLayerAsync(
        Guid? layerId,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> DispatchManualAsync(
        Guid? scriptId = null,
        Guid? triggerId = null,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> ReportDeviceHealthAsync(
        Guid deviceInstanceId,
        ControlSessionHealth health,
        CancellationToken cancellationToken = default);

    ValueTask<ControlScriptRuntimeSessionResult> TickPeriodicAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}
