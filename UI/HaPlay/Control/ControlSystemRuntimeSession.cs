using HaPlay.Models;

namespace HaPlay.ControlGraph;

public sealed class ControlSystemRuntimeSession : IAsyncDisposable, IDisposable
{
    private bool _disposed;

    public ControlSystemRuntimeSession(
        ControlSystemConfig config,
        IControlScriptSourceProvider sourceProvider,
        IControlOscSender oscSender,
        IControlMidiSender? midiSender = null,
        IControlMonitorSink? monitor = null,
        int instructionLimit = ControlScriptFileHost.DefaultInstructionLimit)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sourceProvider);
        ArgumentNullException.ThrowIfNull(oscSender);

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

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return OscListeners.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default) =>
        OscListeners.StopAsync(cancellationToken);

    public async ValueTask<ControlSystemRuntimeTickResult> TickAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var scriptResult = await ScriptSession.TickPeriodicAsync(utcNow, cancellationToken).ConfigureAwait(false);
        var periodicOscResults = await PeriodicOscSends.TickAsync(utcNow, cancellationToken).ConfigureAwait(false);
        return new ControlSystemRuntimeTickResult(scriptResult, periodicOscResults);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await OscListeners.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        OscListeners.Dispose();
    }
}

public sealed record ControlSystemRuntimeTickResult(
    ControlScriptRuntimeSessionResult ScriptResult,
    IReadOnlyList<ControlPeriodicOscSendResult> PeriodicOscResults);
