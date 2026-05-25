using System.Threading;
using HaPlay.Models;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.PortAudio;

namespace HaPlay.OutputPreview;

/// <summary>
/// Owns one <see cref="PortAudioOutput"/> for the lifetime of a PortAudio <see cref="OutputDefinition"/>
/// line. The stream is opened once (at the line's declared sample rate / channel count) and stays open
/// across playback sessions — receivers don't see Pa_OpenStream cost on every track change, and the
/// PA callback keeps draining silence between sessions so ALSA/PulseAudio doesn't release the device.
/// Sessions acquire the output via <see cref="AcquireForPlayback"/> and release it on Dispose; the
/// stream is only closed when the line is removed.
/// </summary>
internal sealed class PortAudioOutputRuntime : IDisposable
{
    private PortAudioOutputDefinition _definition;
    private readonly Lock _gate = new();
    private PortAudioOutput? _output;
    private int _holders;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.OutputPreview.PortAudioOutputRuntime");

    public PortAudioOutputRuntime(PortAudioOutputDefinition definition) =>
        _definition = definition;

    /// <summary>Current definition for this runtime. May be swapped by <see cref="ReconfigureAsync"/>.</summary>
    public PortAudioOutputDefinition Definition
    {
        get { lock (_gate) return _definition; }
    }

    /// <summary>Format the persistent stream is opened at — sessions resample upstream when their source differs.</summary>
    public AudioFormat Format => new(Definition.SampleRate, Definition.ChannelCount);

    /// <summary>
    /// Raised after <see cref="ReconfigureAsync"/> swaps the underlying <see cref="PortAudioOutput"/>.
    /// Any prior reference handed out by <see cref="AcquireForPlayback"/> is now stale; subscribers
    /// (i.e. an in-flight <c>HaPlayPlaybackSession</c>) should release + re-acquire to pick up the new
    /// stream. Phase A wires the event but does not orchestrate the re-acquire — that's Phase B's job.
    /// </summary>
    public event EventHandler? Reconfigured;

    /// <summary>Opens and starts the underlying <see cref="PortAudioOutput"/>. Call once per runtime.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_output is not null)
                return;
            var output = CreateOutput();
            try
            {
                output.Start();
            }
            catch
            {
                output.Dispose();
                throw;
            }

            _output = output;
        }
        Trace.LogInformation("Start: '{Name}' device={Device} rate={Rate}Hz channels={Ch}",
            _definition.DisplayName, _definition.GlobalDeviceIndex, _definition.SampleRate, _definition.ChannelCount);
    }

    /// <summary>
    /// Returns the persistent <see cref="PortAudioOutput"/> for use as an audio output. Returns <c>null</c>
    /// when the runtime is disposed / never started, or when another acquirer already holds it. The ring
    /// buffer is flushed so the next Submit plays cleanly. Pair every acquire with <see cref="ReleaseFromPlayback"/>.
    /// </summary>
    public PortAudioOutput? AcquireForPlayback(bool liveMonitoring = false) =>
        AcquireForPlaybackCore(liveMonitoring);

    private PortAudioOutput? AcquireForPlaybackCore(bool liveMonitoring)
    {
        lock (_gate)
        {
            if (_disposed || _output is null)
            {
                Trace.LogTrace("AcquireForPlayback: '{Name}' returning null (disposed={D} hasOutput={H})",
                    _definition.DisplayName, _disposed, _output is not null);
                return null;
            }
            if (Interlocked.CompareExchange(ref _holders, 1, 0) != 0)
            {
                Trace.LogWarning("AcquireForPlayback: '{Name}' already held", _definition.DisplayName);
                return null;
            }

            if (liveMonitoring)
            {
                EnsureLiveSizedOutputLocked();
                PortAudioLiveMonitoring.ApplyTo(_output, _definition.SampleRate);
            }
            else
            {
                try { _output.Flush(); }
                catch (Exception ex) { Trace.LogError(ex, $"PortAudioOutputRuntime '{_definition.DisplayName}' Acquire.Flush"); }
            }

            Trace.LogDebug("AcquireForPlayback: '{Name}' acquired liveMonitoring={Live}", _definition.DisplayName, liveMonitoring);
            return _output;
        }
    }

    /// <summary>
    /// Reopens the stream with a live-monitoring ring when an older session opened a multi-second buffer.
    /// </summary>
    private void EnsureLiveSizedOutputLocked()
    {
        if (_output is null)
            return;

        var liveCap = PortAudioLiveMonitoring.RingCapacityFrames(_definition.SampleRate);
        if (_output.CapacitySamples <= liveCap * 2)
            return;

        Trace.LogInformation(
            "EnsureLiveSizedOutput: '{Name}' reopening stream (capacity={Cap} liveCap={LiveCap})",
            _definition.DisplayName, _output.CapacitySamples, liveCap);

        try { _output.Stop(); }
        catch (Exception ex) { Trace.LogWarning(ex, "EnsureLiveSizedOutput Stop"); }

        try { _output.Dispose(); }
        catch (Exception ex) { Trace.LogWarning(ex, "EnsureLiveSizedOutput Dispose"); }

        var output = CreateOutput();
        output.Start();
        _output = output;
    }

    private PortAudioOutput CreateOutput()
    {
        var output = new PortAudioOutput(
            Format,
            _definition.GlobalDeviceIndex,
            suggestedLatency: null,
            framesPerBuffer: 480,
            ringCapacityFrames: PortAudioLiveMonitoring.RingCapacityFrames(_definition.SampleRate));
        output.TargetQueueSamples = PortAudioLiveMonitoring.TargetQueueSamples(_definition.SampleRate);
        return output;
    }

    /// <summary>
    /// Releases the acquirer's hold. The stream stays open and continues to drain silence on its callback;
    /// the next acquire will see an empty ring.
    /// </summary>
    public void ReleaseFromPlayback()
    {
        lock (_gate)
        {
            Interlocked.Exchange(ref _holders, 0);
            if (_disposed || _output is null)
                return;
            try { _output.Flush(); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Phase A foundations (§9.6) — swaps the underlying <see cref="PortAudioOutput"/> for one opened at
    /// <paramref name="newDefinition"/>'s sample rate / channel count / device. Hot semantics per §3.6:
    /// no policy enforcement here, callers see a brief silence/glitch window and react to <see cref="Reconfigured"/>.
    /// </summary>
    /// <remarks>
    /// <para>The Id field MUST match the existing definition — re-binding to a different line is the wrong
    /// operation (use a fresh runtime). Other fields are free to change.</para>
    /// <para>Existing <see cref="AcquireForPlayback"/> holders see their <see cref="PortAudioOutput"/>
    /// reference go stale; once they release, the next acquire returns the new output.</para>
    /// </remarks>
    public async Task ReconfigureAsync(PortAudioOutputDefinition newDefinition, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (newDefinition.Id != _definition.Id)
            throw new ArgumentException(
                $"ReconfigureAsync requires the same line Id ({_definition.Id}); got {newDefinition.Id}.",
                nameof(newDefinition));

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            PortAudioOutput? toDispose;
            PortAudioOutput? newOutput = null;
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PortAudioOutputRuntime));
                toDispose = _output;
                _definition = newDefinition;
                // Build the new output before dropping the old reference so any failure leaves the line
                // in the "no current output" state rather than half-reconfigured. Hot semantics still hold
                // — acquirers will see null until the new stream comes up.
                _output = null;
            }

            try
            {
                toDispose?.Dispose();
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "ReconfigureAsync: '{Name}' old output Dispose threw", newDefinition.DisplayName);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                newOutput = CreateOutput();
                newOutput.Start();
            }
            catch
            {
                newOutput?.Dispose();
                throw;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    // Concurrent Dispose during reconfigure — drop the just-built output rather than leaking.
                    try { newOutput.Dispose(); }
                    catch { /* best effort */ }
                    throw new ObjectDisposedException(nameof(PortAudioOutputRuntime));
                }

                _output = newOutput;
            }

            Trace.LogInformation("ReconfigureAsync: '{Name}' device={Device} rate={Rate}Hz channels={Ch}",
                newDefinition.DisplayName, newDefinition.GlobalDeviceIndex, newDefinition.SampleRate, newDefinition.ChannelCount);
        }, cancellationToken).ConfigureAwait(false);

        Reconfigured?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        PortAudioOutput? toDispose;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            toDispose = _output;
            _output = null;
        }

        try { toDispose?.Dispose(); }
        catch { /* best effort */ }
    }
}
