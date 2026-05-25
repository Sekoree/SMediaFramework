using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>Playback-host helpers formerly on <c>AudioPlayer</c> (auto primary output, owned sources, convenience routes).</summary>
public sealed partial class AudioRouter
{
    private readonly List<IDisposable> _ownedDisposables = [];
    private readonly Dictionary<string, AudioFormat> _sinkFormats = new(StringComparer.Ordinal);
    private MediaClock? _attachedMasterClock;
    private string? _primaryOutputId;

    /// <summary>
    /// When <c>true</c> (default), the first <see cref="IClockedOutput"/> becomes the pacing primary and,
    /// if it implements <see cref="IPlaybackClock"/>, <see cref="AttachMasterClock"/> receives updates.
    /// </summary>
    public bool AutoWirePrimary { get; set; } = true;

    /// <summary>The current primary output id (router slave clock), or <c>null</c>.</summary>
    public string? PrimaryOutputId
    {
        get { lock (_gate) return _primaryOutputId; }
    }

    /// <summary>Alias for <see cref="IsRunning"/>.</summary>
    public bool IsPlaying => IsRunning;

    /// <summary>Registers a clock to master when the primary output implements <see cref="IPlaybackClock"/>.</summary>
    public void AttachMasterClock(MediaClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
            _attachedMasterClock = clock;
    }

    /// <summary>
    /// Add a source the router will dispose on <see cref="Dispose"/> (in addition to resampler wrappers).
    /// </summary>
    public string AddOwnedSource(IAudioSource source, string? id = null, bool autoResample = false)
    {
        var sourceId = AddSource(source, id, autoResample);
        if (source is IDisposable d)
        {
            lock (_gate)
                _ownedDisposables.Add(d);
        }

        return sourceId;
    }

    /// <summary>Registers an output and applies <see cref="AutoWirePrimary"/> when eligible.</summary>
    public string AddOutputAndAutoWirePrimary(IAudioOutput output, string? id = null, int? pumpCapacityChunks = null) =>
        AddOutput(output, id, pumpCapacityChunks);

    /// <summary>
    /// Convenience route with identity <see cref="ChannelMap"/> sized to the output channel count.
    /// </summary>
    public void Connect(string sourceId, string outputId, ChannelMap? map = null, float gain = 1.0f)
    {
        ChannelMap effective;
        if (map is { } m)
        {
            effective = m;
        }
        else
        {
            int channels;
            lock (_gate)
            {
                if (!_sinkFormats.TryGetValue(outputId, out var fmt))
                    throw new ArgumentException($"unknown output '{outputId}'", nameof(outputId));
                channels = fmt.Channels;
            }

            effective = ChannelMap.Identity(channels);
        }

        AddRoute(sourceId, outputId, effective, gain);
    }

    /// <summary>Seeks the only registered source. Throws when zero or multiple sources exist.</summary>
    public void Seek(TimeSpan position)
    {
        string id;
        lock (_gate)
        {
            var ids = SourceIds;
            if (ids.Count != 1)
                throw new InvalidOperationException(
                    $"Seek() requires exactly one registered source (found {ids.Count}); use SeekSource(sourceId, position) instead.");
            id = ids.First();
        }

        SeekSource(id, position);
    }

    private void AutoWirePrimaryOutputIfNeeded(string outputId, IAudioOutput output)
    {
        if (!AutoWirePrimary || _primaryOutputId is not null || output is not IClockedOutput)
            return;

        SlaveTo(outputId);
        if (output is IPlaybackClock pc)
            _attachedMasterClock?.SetMaster(pc);
        _primaryOutputId = outputId;
        MediaDiagnostics.LogDebug(
            "AddOutput: promoted output {0} to primary ({1})",
            outputId,
            output is IPlaybackClock ? output.GetType().Name : "IClockedOutput only");
    }

    private IAudioOutput MaybeWrapAdaptiveRateOutputLocked(IAudioOutput output, string outputId)
    {
        if (!_wrapAdaptiveRateOnNonMasterOutputs || output is IAdaptiveRateWrappedOutput)
            return output;
        if (_slaveClockOutputId == outputId || _primaryOutputId == outputId)
            return output;
        if (AutoWirePrimary && _primaryOutputId is null && _slaveClockOutputId is null && output is IClockedOutput)
            return output;
        var wrap = MediaFrameworkPlugins.WrapAdaptiveRateOutput;
        return wrap is null ? output : wrap(this, output, outputId, _adaptiveRateMaxDeltaHz);
    }

    private void PromoteNextPrimaryIfNeeded(string removedOutputId)
    {
        if (_primaryOutputId != removedOutputId)
            return;

        _primaryOutputId = null;
        _attachedMasterClock?.SetMaster(null);
        if (!AutoWirePrimary)
            return;

        foreach (var id in OutputIds.Order(StringComparer.Ordinal))
        {
            if (id == removedOutputId) continue;
            if (!TryGetOutput(id, out var s) || s is not IClockedOutput) continue;
            RetargetSlaveClock(id);
            if (s is IPlaybackClock pc)
                _attachedMasterClock?.SetMaster(pc);
            _primaryOutputId = id;
            return;
        }
    }

    private void DisposeOwnedSources()
    {
        foreach (var d in _ownedDisposables)
            MediaDiagnostics.SwallowDisposeErrors(d.Dispose, "AudioRouter.Dispose: owned source");
        _ownedDisposables.Clear();
    }
}
