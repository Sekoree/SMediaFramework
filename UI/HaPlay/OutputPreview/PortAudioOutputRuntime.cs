using System.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Audio.PortAudio;
using S.Media.Routing;

namespace HaPlay.OutputPreview;

/// <summary>
/// Owns one persistent audio output for the lifetime of an audio <see cref="OutputDefinition"/> line.
/// The device is opened once (at the line's declared sample rate / channel count) and stays open
/// across playback sessions - receivers don't see Pa_OpenStream cost on every track change, and the
/// device callback keeps draining silence between sessions so the OS doesn't release the device.
/// Sessions acquire isolated inputs via <see cref="AcquireForPlayback"/>. A persistent fan-in mixer
/// is the sole producer for the hardware stream, so simultaneous cues/decks share one OS audio node
/// without violating PortAudio's single-producer ring contract. The stream is only closed when the
/// line is removed or structurally reconfigured.
/// </summary>
internal sealed class PortAudioOutputRuntime : IDisposable
{
    private PortAudioOutputDefinition _definition;
    private readonly Lock _gate = new();
    private IAudioOutput? _output;
    private SharedAudioOutput? _sharedOutput;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.OutputPreview.PortAudioOutputRuntime");

    public PortAudioOutputRuntime(PortAudioOutputDefinition definition) =>
        _definition = definition;

    /// <summary>Current definition for this runtime. May be swapped by <see cref="ReconfigureAsync"/>.</summary>
    public PortAudioOutputDefinition Definition
    {
        get { lock (_gate) return _definition; }
    }

    /// <summary>Format the persistent stream is opened at - sessions resample upstream when their source differs.</summary>
    public AudioFormat Format => new(Definition.SampleRate, Definition.ChannelCount);

    /// <summary>
    /// Raised after <see cref="ReconfigureAsync"/> swaps the underlying audio output.
    /// Any prior reference handed out by <see cref="AcquireForPlayback"/> is now stale; subscribers
    /// holding an in-flight playback lease should release + re-acquire to pick up the new
    /// stream. Phase A wires the event but does not orchestrate the re-acquire - that's Phase B's job.
    /// </summary>
    public event EventHandler? Reconfigured;

    /// <summary>Opens and starts the underlying audio output. Call once per runtime.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_output is not null)
                return;
            var output = CreateOutput(_definition, out var resolvedDefinition);
            try
            {
                _sharedOutput = new SharedAudioOutput(output, chunkSamples: 480, pumpCapacityChunks: 4);
                _definition = resolvedDefinition;
                _output = output;
            }
            catch
            {
                DisposeOutput(output);
                throw;
            }
        }
        Trace.LogInformation("Start: '{Name}' backend={Backend} device={Device} rate={Rate}Hz channels={Ch}",
            _definition.DisplayName, _definition.EffectiveAudioBackendName, _definition.DeviceName,
            _definition.SampleRate, _definition.ChannelCount);
    }

    /// <summary>
    /// Returns a private producer endpoint feeding the persistent output's fan-in mixer. Returns
    /// <c>null</c> when the runtime is disposed or never started. Multiple leases may coexist; dispose
    /// each lease when its playback route is detached.
    /// </summary>
    public SharedAudioOutputLease? AcquireForPlayback(bool liveMonitoring = false) =>
        AcquireForPlaybackCore(liveMonitoring);

    private SharedAudioOutputLease? AcquireForPlaybackCore(bool liveMonitoring)
    {
        lock (_gate)
        {
            if (_disposed || _output is null || _sharedOutput is null)
            {
                Trace.LogTrace("AcquireForPlayback: '{Name}' returning null (disposed={D} hasOutput={H})",
                    _definition.DisplayName, _disposed, _output is not null);
                return null;
            }

            if (liveMonitoring)
            {
                if (_output is PortAudioOutput portAudio)
                    PortAudioLiveMonitoring.ApplyTo(portAudio, _definition.SampleRate);
            }

            var lease = _sharedOutput.Acquire();
            Trace.LogDebug("AcquireForPlayback: '{Name}' acquired client={Clients} liveMonitoring={Live}",
                _definition.DisplayName, _sharedOutput.ActiveLeaseCount, liveMonitoring);
            return lease;
        }
    }

    private IAudioOutput CreateOutput(
        PortAudioOutputDefinition definition,
        out PortAudioOutputDefinition resolvedDefinition)
    {
        if (definition.UsesPortAudioBackend)
            return CreatePortAudioOutput(definition, out resolvedDefinition);

        return CreateBackendOutput(definition, out resolvedDefinition);
    }

    private PortAudioOutput CreatePortAudioOutput(
        PortAudioOutputDefinition definition,
        out PortAudioOutputDefinition resolvedDefinition)
    {
        var devices = PortAudioDeviceCatalog.EnumerateOutputDevices();
        var hostApis = PortAudioDeviceCatalog.EnumerateHostApis();
        resolvedDefinition = ResolveCurrentOutputDevice(definition, devices, hostApis);
        if (resolvedDefinition.GlobalDeviceIndex != definition.GlobalDeviceIndex
            || !string.Equals(resolvedDefinition.DeviceName, definition.DeviceName, StringComparison.Ordinal)
            || resolvedDefinition.HostApiIndex != definition.HostApiIndex)
        {
            Trace.LogWarning(
                "CreateOutput: '{Name}' saved PA device {SavedIndex} '{SavedDevice}' resolved to current device {ResolvedIndex} '{ResolvedDevice}'",
                definition.DisplayName,
                definition.GlobalDeviceIndex,
                definition.DeviceName,
                resolvedDefinition.GlobalDeviceIndex,
                resolvedDefinition.DeviceName);
        }

        var output = new PortAudioOutput(
            new AudioFormat(resolvedDefinition.SampleRate, resolvedDefinition.ChannelCount),
            resolvedDefinition.GlobalDeviceIndex,
            suggestedLatency: null,
            framesPerBuffer: 480,
            ringCapacityFrames: PortAudioLiveMonitoring.RingCapacityFrames(resolvedDefinition.SampleRate));
        output.TargetQueueSamples = PortAudioLiveMonitoring.TargetQueueSamples(resolvedDefinition.SampleRate);
        try
        {
            output.Start();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private IAudioOutput CreateBackendOutput(
        PortAudioOutputDefinition definition,
        out PortAudioOutputDefinition resolvedDefinition)
    {
        var backendName = definition.EffectiveAudioBackendName;
        if (!MediaRuntime.TryGetAudioBackend(backendName, out var backend))
            throw new InvalidOperationException(
                $"audio backend '{backendName}' is not registered; available: {FormatBackendNames()}");

        var deviceId = definition.EffectiveAudioBackendDeviceId;
        var useDefaultDevice = string.IsNullOrWhiteSpace(deviceId);
        var deviceName = string.IsNullOrWhiteSpace(definition.DeviceName) ? "System default" : definition.DeviceName;
        try
        {
            var devices = backend.EnumerateOutputDevices();
            var matched = MatchBackendDevice(devices, deviceId, definition.DeviceName);
            if (matched is not null)
            {
                if (!useDefaultDevice)
                    deviceId = matched.Id;
                deviceName = matched.Name;
            }
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "CreateBackendOutput: '{Name}' failed to enumerate {Backend} devices; using saved device id",
                definition.DisplayName, backend.Name);
        }

        resolvedDefinition = definition with
        {
            HostApiIndex = -1,
            HostApiName = backend.Name,
            GlobalDeviceIndex = -1,
            DeviceName = deviceName,
            AudioBackendName = backend.Name,
            AudioBackendDeviceId = useDefaultDevice ? null : deviceId,
        };

        var options = new AudioBackendOptions(
            SuggestedLatencySeconds: null,
            FramesPerBuffer: 480,
            RingCapacityFrames: PortAudioLiveMonitoring.RingCapacityFrames(resolvedDefinition.SampleRate));

        return backend.CreateOutput(deviceId, new AudioFormat(resolvedDefinition.SampleRate, resolvedDefinition.ChannelCount), options);
    }

    private static AudioDeviceInfo? MatchBackendDevice(
        IReadOnlyList<AudioDeviceInfo> devices,
        string? deviceId,
        string? savedDeviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var byId = devices.FirstOrDefault(d =>
                string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(savedDeviceName))
        {
            var byName = devices.FirstOrDefault(d =>
                string.Equals(d.Name, savedDeviceName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        return devices.FirstOrDefault(d => d.IsDefault);
    }

    private static string FormatBackendNames()
    {
        var names = MediaRuntime.Registry.AudioBackends.Select(b => b.Name).ToArray();
        return names.Length == 0 ? "(none)" : string.Join(", ", names);
    }

    internal static PortAudioOutputDefinition ResolveCurrentOutputDevice(
        PortAudioOutputDefinition definition,
        IReadOnlyList<PortAudioOutputDeviceEntry> devices,
        IReadOnlyList<PortAudioHostApiEntry> hostApis)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(hostApis);

        var requestedChannels = Math.Max(1, definition.ChannelCount);
        var hostNames = hostApis.ToDictionary(h => h.Index, h => h.Name);
        var requestedDeviceName = definition.DeviceName?.Trim() ?? string.Empty;
        var requestedHostName = definition.HostApiName?.Trim() ?? string.Empty;

        static bool SameName(string? a, string? b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        string HostNameFor(PortAudioOutputDeviceEntry device) =>
            hostNames.TryGetValue(device.HostApiIndex, out var name) ? name : requestedHostName;

        PortAudioOutputDeviceEntry? PickUsable(IEnumerable<PortAudioOutputDeviceEntry> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.MaxOutputChannels >= requestedChannels)
                    return candidate;
            }

            return null;
        }

        var nameMatches = devices
            .Where(d => SameName(d.Name, requestedDeviceName))
            .ToList();

        PortAudioOutputDeviceEntry? match = null;
        if (nameMatches.Count > 0)
        {
            match = PickUsable(nameMatches.Where(d => SameName(HostNameFor(d), requestedHostName)))
                ?? PickUsable(nameMatches.Where(d => d.HostApiIndex == definition.HostApiIndex))
                ?? PickUsable(nameMatches.Where(d => d.GlobalDeviceIndex == definition.GlobalDeviceIndex))
                ?? PickUsable(nameMatches);

            if (match is null)
            {
                var closest = nameMatches
                    .OrderByDescending(d => SameName(HostNameFor(d), requestedHostName))
                    .ThenByDescending(d => d.HostApiIndex == definition.HostApiIndex)
                    .ThenByDescending(d => d.GlobalDeviceIndex == definition.GlobalDeviceIndex)
                    .First();
                throw new InvalidOperationException(
                    $"saved PortAudio output '{definition.DisplayName}' resolved to device '{closest.Name}', " +
                    $"but it supports {closest.MaxOutputChannels} output channel(s), requested {requestedChannels}");
            }
        }
        else if (string.IsNullOrWhiteSpace(requestedDeviceName))
        {
            match = PickUsable(devices.Where(d => d.GlobalDeviceIndex == definition.GlobalDeviceIndex));
        }

        if (match is not { } resolved)
        {
            throw new InvalidOperationException(
                $"saved PortAudio output '{definition.DisplayName}' device '{definition.DeviceName}' was not found; edit the output and choose an available output device");
        }

        return definition with
        {
            HostApiIndex = resolved.HostApiIndex,
            HostApiName = HostNameFor(resolved),
            GlobalDeviceIndex = resolved.GlobalDeviceIndex,
            DeviceName = resolved.Name,
        };
    }

    /// <summary>
    /// Phase A foundations (§9.6) - swaps the underlying audio output for one opened at
    /// <paramref name="newDefinition"/>'s sample rate / channel count / device. Hot semantics per §3.6:
    /// no policy enforcement here, callers see a brief silence/glitch window and react to <see cref="Reconfigured"/>.
    /// </summary>
    /// <remarks>
    /// <para>The Id field MUST match the existing definition - re-binding to a different line is the wrong
    /// operation (use a fresh runtime). Other fields are free to change.</para>
    /// <para>Existing <see cref="AcquireForPlayback"/> holders see their audio output
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
            IAudioOutput? toDispose;
            SharedAudioOutput? sharedToDispose;
            IAudioOutput? newOutput = null;
            SharedAudioOutput? newSharedOutput = null;
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(PortAudioOutputRuntime));
                toDispose = _output;
                sharedToDispose = _sharedOutput;
                // Build the new output before dropping the old reference so any failure leaves the line
                // in the "no current output" state rather than half-reconfigured. Hot semantics still hold
                // - acquirers will see null until the new stream comes up.
                _output = null;
                _sharedOutput = null;
            }

            try { sharedToDispose?.Dispose(); }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "ReconfigureAsync: '{Name}' old shared mixer Dispose threw", newDefinition.DisplayName);
            }
            try { DisposeOutput(toDispose); }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "ReconfigureAsync: '{Name}' old output Dispose threw", newDefinition.DisplayName);
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                newOutput = CreateOutput(newDefinition, out var resolvedDefinition);
                newSharedOutput = new SharedAudioOutput(newOutput, chunkSamples: 480, pumpCapacityChunks: 4);
                newDefinition = resolvedDefinition;
            }
            catch
            {
                newSharedOutput?.Dispose();
                DisposeOutput(newOutput);
                throw;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    // Concurrent Dispose during reconfigure - drop the just-built output rather than leaking.
                    try { newSharedOutput?.Dispose(); }
                    catch { /* best effort */ }
                    try { DisposeOutput(newOutput); }
                    catch { /* best effort */ }
                    throw new ObjectDisposedException(nameof(PortAudioOutputRuntime));
                }

                _definition = newDefinition;
                _output = newOutput;
                _sharedOutput = newSharedOutput;
            }

            Trace.LogInformation("ReconfigureAsync: '{Name}' backend={Backend} device={Device} rate={Rate}Hz channels={Ch}",
                newDefinition.DisplayName, newDefinition.EffectiveAudioBackendName, newDefinition.DeviceName,
                newDefinition.SampleRate, newDefinition.ChannelCount);
        }, cancellationToken).ConfigureAwait(false);

        Reconfigured?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        IAudioOutput? toDispose;
        SharedAudioOutput? sharedToDispose;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            toDispose = _output;
            sharedToDispose = _sharedOutput;
            _output = null;
            _sharedOutput = null;
        }

        try { sharedToDispose?.Dispose(); }
        catch { /* best effort */ }
        try { DisposeOutput(toDispose); }
        catch { /* best effort */ }
    }

    private static void DisposeOutput(IAudioOutput? output)
    {
        if (output is IDisposable disposable)
            disposable.Dispose();
    }
}
