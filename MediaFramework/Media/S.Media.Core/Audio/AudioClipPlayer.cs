namespace S.Media.Core.Audio;

/// <summary>Per-pad helper that owns one <see cref="AudioClip"/> and triggers voices into an <see cref="AudioRouter"/>.</summary>
public sealed class AudioClipPlayer
{
    private readonly AudioClip _clip;
    private readonly List<AudioClipVoice> _activeVoices = [];
    private readonly Lock _gate = new();
    private AudioClipVoice? _latchedVoice;

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
                return _activeVoices.Where(v => !v.IsExhausted).ToArray();
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
            PruneExhausted();

            if (Mode == AudioClipPlayerMode.LatchedLoop
                && _latchedVoice is { } latched
                && !latched.IsExhausted)
            {
                latched.Loop = false;
                latched.Stop();
                _latchedVoice = null;
                return string.Empty;
            }

            if (Mode == AudioClipPlayerMode.OneShot && _activeVoices.Any(v => !v.IsExhausted))
                return string.Empty;

            if (Mode == AudioClipPlayerMode.MonoRetrigger)
            {
                foreach (var v in _activeVoices)
                    v.Stop();
            }

            var voiceOptions = options ?? default;
            if (Mode == AudioClipPlayerMode.LatchedLoop)
                voiceOptions = voiceOptions with { Loop = true };

            var voice = _clip.CreateVoice(voiceOptions);
            if (Mode == AudioClipPlayerMode.LatchedLoop)
                _latchedVoice = voice;

            var sourceId = router.AddSource(voice, autoResample: true);
            var routeMap = map ?? (router.TryGetOutput(outputId, out var output) && output is not null
                ? ChannelMap.Identity(output.Format.Channels)
                : ChannelMap.Identity(_clip.Format.Channels));
            router.Route(sourceId, outputId, routeMap, gain);

            if (ChokeGroup is { } group)
                router.RegisterChokeGroup(group, voice);

            _activeVoices.Add(voice);
            EnforcePolyphony();
            return sourceId;
        }
    }

    /// <summary>Stops every active voice (release fade).</summary>
    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var voice in _activeVoices)
                voice.Stop();
            _latchedVoice = null;
        }
    }

    private void EnforcePolyphony()
    {
        while (_activeVoices.Count(v => !v.IsExhausted) > MaxPolyphony)
        {
            var oldest = _activeVoices.First(v => !v.IsExhausted);
            oldest.Stop();
        }
    }

    private void PruneExhausted() => _activeVoices.RemoveAll(v => v.IsExhausted);
}
