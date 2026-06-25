namespace S.Media.Core.Audio;

/// <summary>
/// Optional <see cref="IAudioSource"/> capability: jump to an arbitrary
/// position in the source's timeline. File decoders implement this; live
/// sources (NDI receiver, capture device) do not.
/// </summary>
public interface ISeekableSource
{
    /// <summary>Total duration if known; <see cref="TimeSpan.Zero"/> for live/unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Current decode position (most recently emitted frame's PTS).</summary>
    TimeSpan Position { get; }

    /// <summary>Jump to <paramref name="position"/>. Must clear any internal decode/conversion state so the next read starts at the new location.</summary>
    void Seek(TimeSpan position);
}
