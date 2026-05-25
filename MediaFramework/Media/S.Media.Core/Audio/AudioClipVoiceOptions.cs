namespace S.Media.Core.Audio;

/// <summary>Playback options for a single <see cref="AudioClipVoice"/> instance.</summary>
public readonly record struct AudioClipVoiceOptions(
    bool Loop = false,
    double StartOffsetSec = 0,
    float StartGain = 1f,
    TimeSpan? AttackFade = null,
    TimeSpan? ReleaseFade = null);
