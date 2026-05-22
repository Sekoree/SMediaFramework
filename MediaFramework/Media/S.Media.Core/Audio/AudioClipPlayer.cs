namespace S.Media.Core.Audio;

/// <summary>Per-pad helper that owns one <see cref="AudioClip"/> and triggers voices into an <see cref="AudioRouter"/>.</summary>
/// <remarks>
/// <para>
/// <see cref="Fire"/> registers each voice as a router source and routes it to the supplied output;
/// when the voice exhausts (natural end of clip or after <see cref="AudioClipVoice.Stop"/>'s release
/// fade), the next <see cref="Fire"/> or <see cref="StopAll"/> call removes it from the router so
/// dead sources / routes do not accumulate. Operators who fire pads frequently and never call
/// <see cref="Fire"/> again should call <see cref="ReapExhausted"/> from their UI tick (or set up an
/// <see cref="AudioRouter.PumpPressure"/> handler that calls it).
/// </para>
/// </remarks>
public sealed class AudioClipPlayer
{
    private readonly AudioClip _clip;
    private readonly List<VoiceRegistration> _activeVoices = [];
    private readonly Lock _gate = new();
    private VoiceRegistration? _latched;

    public AudioClipPlayer(AudioClip clip) => _clip = clip ?? throw new ArgumentNullException(nameof(clip));

    public AudioClip Clip => _clip;

    public AudioClipPlayerMode Mode { get; set; } = AudioClipPlayerMode.Polyphonic;

    public int MaxPolyphony { get; set; } = 8;

    /// <summary>When set, <see cref="Fire"/> registers the voice in this router choke group.</summary>
    public string? ChokeGroup { get; set; }

    public IReadOnlyList<AudioClipVoice> ActiveVoices
    {
        get
        {
            lock (_gate)
            {
                ReapExhaustedLocked();
                var snapshot = new AudioClipVoice[_activeVoices.Count];
                for (var i = 0; i < _activeVoices.Count; i++)
                    snapshot[i] = _activeVoices[i].Voice;
                return snapshot;
            }
        }
    }

    /// <summary>
    /// Triggers playback: adds an <see cref="AudioClipVoice"/> as a router source and routes it to
    /// <paramref name="outputId"/>. Returns the new router source id, or <see cref="string.Empty"/> when
    /// the fire was suppressed (OneShot) or only stopped a latched loop.
    /// </summary>
    public string Fire(
        AudioRouter router,
        string outputId,
        ChannelMap? map = null,
        float gain = 1f,
        AudioClipVoiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentException.ThrowIfNullOrEmpty(outputId);

        lock (_gate)
        {
            ReapExhaustedLocked();

            if (Mode == AudioClipPlayerMode.LatchedLoop
                && _latched is { } latched
                && !latched.Voice.IsExhausted)
            {
                latched.Voice.Loop = false;
                latched.Voice.Stop();
                _latched = null;
                return string.Empty;
            }

            if (Mode == AudioClipPlayerMode.OneShot && _activeVoices.Any(v => !v.Voice.IsExhausted))
                return string.Empty;

            if (Mode == AudioClipPlayerMode.MonoRetrigger)
            {
                foreach (var v in _activeVoices)
                    v.Voice.Stop();
            }

            var voiceOptions = options ?? default;
            if (Mode == AudioClipPlayerMode.LatchedLoop)
                voiceOptions = voiceOptions with { Loop = true };

            var voice = _clip.CreateVoice(voiceOptions);
            var sourceId = router.AddSource(voice, autoResample: true);
            var routeMap = map ?? (router.TryGetOutput(outputId, out var output) && output is not null
                ? ChannelMap.Identity(output.Format.Channels)
                : ChannelMap.Identity(_clip.Format.Channels));
            router.Route(sourceId, outputId, routeMap, gain);

            if (ChokeGroup is { } group)
                router.RegisterChokeGroup(group, voice);

            var registration = new VoiceRegistration(voice, sourceId, router, ChokeGroup);
            if (Mode == AudioClipPlayerMode.LatchedLoop)
                _latched = registration;

            _activeVoices.Add(registration);
            EnforcePolyphonyLocked();
            return sourceId;
        }
    }

    /// <summary>Stops every active voice (release fade); call again or <see cref="ReapExhausted"/> to actually purge them.</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var v in _activeVoices)
                v.Voice.Stop();
            _latched = null;
        }
    }

    /// <summary>
    /// Removes any exhausted voices from the router (source + choke-group registration).
    /// Called automatically on every <see cref="Fire"/>; expose it for hosts that need to reclaim
    /// router slots without triggering a new fire.
    /// </summary>
    public int ReapExhausted()
    {
        lock (_gate) return ReapExhaustedLocked();
    }

    private int ReapExhaustedLocked()
    {
        var reaped = 0;
        for (var i = _activeVoices.Count - 1; i >= 0; i--)
        {
            var reg = _activeVoices[i];
            if (!reg.Voice.IsExhausted) continue;
            DisposeRegistration(reg);
            _activeVoices.RemoveAt(i);
            if (ReferenceEquals(_latched, reg))
                _latched = null;
            reaped++;
        }
        return reaped;
    }

    private void EnforcePolyphonyLocked()
    {
        if (_activeVoices.Count <= MaxPolyphony) return;
        for (var i = 0; i < _activeVoices.Count && _activeVoices.Count > MaxPolyphony; i++)
        {
            var reg = _activeVoices[i];
            if (reg.Voice.IsExhausted) continue;
            reg.Voice.Stop();
        }
    }

    private static void DisposeRegistration(VoiceRegistration reg)
    {
        reg.Router.RemoveSource(reg.SourceId);
        if (reg.ChokeGroup is { } group)
            reg.Router.UnregisterChokeGroup(group, reg.Voice);
        reg.Voice.Dispose();
    }

    private sealed record VoiceRegistration(AudioClipVoice Voice, string SourceId, AudioRouter Router, string? ChokeGroup);
}
