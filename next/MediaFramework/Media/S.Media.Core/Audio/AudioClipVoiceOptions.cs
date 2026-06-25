namespace S.Media.Core.Audio;

/// <summary>Playback options for a single <see cref="AudioClipVoice"/> instance.</summary>
public readonly record struct AudioClipVoiceOptions(
    bool Loop = false,
    double StartOffsetSec = 0,
    float StartGain = 1f,
    TimeSpan? AttackFade = null,
    TimeSpan? ReleaseFade = null)
{
    /// <summary>
    /// The intended defaults (notably <see cref="StartGain"/> = 1, full gain). Use this instead of
    /// <see langword="default"/>: a <see langword="default"/> struct zero-initializes every field, so its
    /// <see cref="StartGain"/> is 0 (silent) — the constructor's <c>= 1f</c> default only applies when the
    /// primary constructor is actually invoked, as it is here.
    /// </summary>
    public static AudioClipVoiceOptions Default { get; } = new(StartGain: 1f);
}
