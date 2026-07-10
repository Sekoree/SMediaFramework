using S.Media.Core.Audio;
using S.Media.Routing;

namespace S.Media.Session;

/// <summary>
/// Live handle for one sounding cue voice, returned by <see cref="Soundboard.Fire"/>. Lets a host
/// stop, re-gain, and observe a single triggered voice without touching the underlying router.
/// </summary>
public sealed class CueVoice
{
    private readonly AudioClipVoice _voice;
    private readonly AudioRouter _router;
    private readonly string _sourceId;
    private readonly string _outputId;
    private int _completedRaised;

    internal CueVoice(string cueId, AudioClipVoice voice, AudioRouter router, string sourceId, string outputId)
    {
        CueId = cueId;
        _voice = voice;
        _router = router;
        _sourceId = sourceId;
        _outputId = outputId;
    }

    /// <summary>The cue id this voice was fired from.</summary>
    public string CueId { get; }

    /// <summary>Playback position within the clip.</summary>
    public TimeSpan Position => _voice.Position;

    /// <summary>True until the voice finishes - natural end, or after <see cref="Stop"/>'s release fade.</summary>
    public bool IsActive => !_voice.IsExhausted;

    /// <summary>Begins a click-free release fade; the voice finishes after the fade and is reaped by the <see cref="Soundboard"/>.</summary>
    public void Stop() => _voice.Stop();

    /// <summary>
    /// Sets this voice's route gain (click-free ramp). Returns false once the voice has been reaped - its
    /// route no longer exists.
    /// </summary>
    public bool SetGain(float gain)
    {
        try
        {
            _router.SetRouteGain(_sourceId, _outputId, gain);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Route gone (voice reaped) or router disposed (ObjectDisposedException derives from this).
            return false;
        }
    }

    /// <summary>
    /// Raised exactly once when the voice finishes (detected by <see cref="Soundboard.Reap"/> or the next
    /// <see cref="Soundboard.Fire"/>). The handler runs on the reaping thread - keep it light.
    /// </summary>
    public event Action<CueVoice>? Completed;

    internal AudioClipVoice Voice => _voice;

    internal void RaiseCompletedOnce()
    {
        if (Interlocked.Exchange(ref _completedRaised, 1) != 0)
            return;
        try { Completed?.Invoke(this); }
        catch { /* subscriber best effort */ }
    }
}
