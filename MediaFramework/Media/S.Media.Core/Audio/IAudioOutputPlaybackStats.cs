namespace S.Media.Core.Audio;

/// <summary>Optional device playback counters (e.g. PortAudio).</summary>
public interface IAudioOutputPlaybackStats
{
    long PlayedSamples { get; }
    long UnderrunSamples { get; }
    long DroppedSamples { get; }
}
