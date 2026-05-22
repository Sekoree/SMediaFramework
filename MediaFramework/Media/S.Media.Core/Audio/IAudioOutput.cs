namespace S.Media.Core.Audio;

/// <summary>
/// Push-based audio consumer. Outputs include playback devices (PortAudio output)
/// and network senders (NDI). The router calls <see cref="Submit"/> with
/// samples already mapped into the output's channel layout.
/// </summary>
public interface IAudioOutput
{
    AudioFormat Format { get; }

    /// <summary>
    /// Submit packed (interleaved) float samples. <c>packedSamples.Length</c>
    /// must be a multiple of <see cref="Format"/>'s channel count. Outputs
    /// decide their own overflow behaviour (drop vs. block).
    /// </summary>
    void Submit(ReadOnlySpan<float> packedSamples);
}
