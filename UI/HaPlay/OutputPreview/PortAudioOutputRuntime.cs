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
    private readonly PortAudioOutputDefinition _definition;
    private readonly object _gate = new();
    private PortAudioOutput? _output;
    private int _holders;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.OutputPreview.PortAudioOutputRuntime");

    public PortAudioOutputRuntime(PortAudioOutputDefinition definition) =>
        _definition = definition;

    /// <summary>Format the persistent stream is opened at — sessions resample upstream when their source differs.</summary>
    public AudioFormat Format => new(_definition.SampleRate, _definition.ChannelCount);

    /// <summary>Opens and starts the underlying <see cref="PortAudioOutput"/>. Call once per runtime.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_output is not null)
                return;
            // chunk=480 + ringCapacityFrames=sampleRate (1 s of headroom) match HaPlayPlaybackSession's
            // previous per-session sizing. defaultHighOutputLatency is picked by PortAudioOutput's ctor.
            var output = new PortAudioOutput(
                Format,
                _definition.GlobalDeviceIndex,
                suggestedLatency: null,
                framesPerBuffer: 480,
                ringCapacityFrames: _definition.SampleRate);
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
    /// Returns the persistent <see cref="PortAudioOutput"/> for use as an audio sink. Returns <c>null</c>
    /// when the runtime is disposed / never started, or when another acquirer already holds it. The ring
    /// buffer is flushed so the next Submit plays cleanly. Pair every acquire with <see cref="ReleaseFromPlayback"/>.
    /// </summary>
    public PortAudioOutput? AcquireForPlayback()
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

            try { _output.Flush(); }
            catch (Exception ex) { Trace.LogError(ex, $"PortAudioOutputRuntime '{_definition.DisplayName}' Acquire.Flush"); }

            Trace.LogDebug("AcquireForPlayback: '{Name}' acquired", _definition.DisplayName);
            return _output;
        }
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
