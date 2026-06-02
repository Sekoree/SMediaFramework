using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>
/// High-level cue / soundboard engine over an <see cref="AudioRouter"/>. Owns named cues (a clip plus
/// how it plays — mode, choke group, output, gain) and triggers them with <see cref="Fire"/>, which
/// returns a <see cref="CueVoice"/> handle. The low-level router source / route / choke-group / reaping
/// bookkeeping is an implementation detail.
/// </summary>
/// <remarks>
/// Reaping (removing finished voices from the router and raising <see cref="CueVoice.Completed"/>) runs
/// on every <see cref="Fire"/>; hosts that fire rarely should also call <see cref="Reap"/> from a UI
/// tick so finished sources don't linger.
/// </remarks>
public sealed class Soundboard : IDisposable
{
    private readonly AudioRouter _router;
    private readonly bool _ownsRouter;
    private readonly Dictionary<string, CueEntry> _cues = new(StringComparer.Ordinal);
    private readonly List<CueVoice> _live = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="router">The router voices are fired into.</param>
    /// <param name="ownsRouter">When true, <see cref="Dispose"/> also disposes <paramref name="router"/>.</param>
    public Soundboard(AudioRouter router, bool ownsRouter = false)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _ownsRouter = ownsRouter;
    }

    /// <summary>The underlying router (escape hatch for advanced wiring).</summary>
    public AudioRouter Router => _router;

    /// <summary>Registers a cue. <paramref name="outputId"/> must already be an output on the router.</summary>
    public void AddCue(
        string cueId,
        AudioClip clip,
        string outputId,
        AudioClipPlayerMode mode = AudioClipPlayerMode.Polyphonic,
        string? chokeGroup = null,
        float gain = 1f,
        ChannelMap? map = null,
        int maxPolyphony = 8)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentException.ThrowIfNullOrEmpty(outputId);
        if (!_router.TryGetOutput(outputId, out _))
            throw new ArgumentException($"output '{outputId}' is not registered", nameof(outputId));

        var player = new AudioClipPlayer(clip)
        {
            Mode = mode,
            ChokeGroup = chokeGroup,
            MaxPolyphony = maxPolyphony,
        };

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_cues.TryAdd(cueId, new CueEntry(player, outputId, gain, map)))
                throw new ArgumentException($"cue '{cueId}' is already registered", nameof(cueId));
        }
    }

    /// <summary>Removes a cue and stops + reaps any of its sounding voices. Returns false if unknown.</summary>
    public bool RemoveCue(string cueId)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        CueEntry? entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_cues.Remove(cueId, out entry))
                return false;
        }
        HardStop(entry.Player);
        lock (_gate)
            ReapLocked();
        return true;
    }

    /// <summary>
    /// Triggers a cue. Returns a <see cref="CueVoice"/> handle, or <see langword="null"/> when the cue is
    /// unknown or the fire was suppressed (OneShot already sounding / latched-loop toggle-off).
    /// </summary>
    public CueVoice? Fire(string cueId, AudioClipVoiceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        CueEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ReapLocked();
            if (!_cues.TryGetValue(cueId, out var found))
                return null;
            entry = found;
        }

        if (!entry.Player.TryFire(_router, entry.OutputId, out var voice, out var sourceId, entry.Map, entry.Gain, options)
            || voice is null)
            return null;

        var handle = new CueVoice(cueId, voice, _router, sourceId, entry.OutputId);
        lock (_gate)
            _live.Add(handle);
        return handle;
    }

    /// <summary>Stops every sounding voice across all cues (click-free release fade).</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var entry in _cues.Values)
                entry.Player.StopAll();
        }
    }

    /// <summary>Stops sounding voices fired from cues in <paramref name="chokeGroup"/>.</summary>
    public void StopGroup(string chokeGroup)
    {
        ArgumentException.ThrowIfNullOrEmpty(chokeGroup);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var entry in _cues.Values)
                if (entry.Player.ChokeGroup == chokeGroup)
                    entry.Player.StopAll();
        }
    }

    /// <summary>
    /// Reaps finished voices — removes their router sources and raises <see cref="CueVoice.Completed"/>.
    /// Runs on every <see cref="Fire"/>; call it from a UI tick when firing rarely. Returns the count of
    /// router sources removed.
    /// </summary>
    public int Reap()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReapLocked();
        }
    }

    private int ReapLocked()
    {
        var reaped = 0;
        foreach (var entry in _cues.Values)
            reaped += entry.Player.ReapExhausted();

        for (var i = _live.Count - 1; i >= 0; i--)
        {
            var v = _live[i];
            if (v.IsActive)
                continue;
            v.RaiseCompletedOnce();
            _live.RemoveAt(i);
        }
        return reaped;
    }

    public void Dispose()
    {
        List<CueVoice> live;
        CueEntry[] entries;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            entries = [.. _cues.Values];
            live = [.. _live];
            _cues.Clear();
            _live.Clear();
        }

        // Hard-stop every cue (dispose voices = instant exhaust) then reap so the router sources / choke
        // registrations are removed immediately — important when the router is borrowed (no later reap).
        foreach (var entry in entries)
            HardStop(entry.Player);

        foreach (var v in live)
            v.RaiseCompletedOnce();

        if (_ownsRouter)
            MediaDiagnostics.SwallowDisposeErrors(_router.Dispose, "Soundboard: router");
    }

    private static void HardStop(AudioClipPlayer player)
    {
        foreach (var voice in player.ActiveVoices)
            voice.Dispose(); // instant exhaust (no release fade) so ReapExhausted removes it now
        player.ReapExhausted();
    }

    private sealed record CueEntry(AudioClipPlayer Player, string OutputId, float Gain, ChannelMap? Map);
}
