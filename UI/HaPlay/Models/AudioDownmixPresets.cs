namespace HaPlay.Models;

/// <summary>
/// Multichannel channel-mapping presets shared by the media-player audio matrix and the cue route
/// editor. Each preset expands to a set of audible source→output <see cref="DownmixContribution"/>s
/// for a given source/output channel count; cells/routes not listed are silent. Gains are in dB.
/// </summary>
public enum AudioDownmixPreset
{
    /// <summary>Direct mapping: source channel i → output channel i (unity).</summary>
    PassThrough,

    /// <summary>Duplicate source channel 0 onto every output channel (unity). For a mono source.</summary>
    MonoToStereo,

    /// <summary>ITU-R BS.775 5.1 → stereo fold-down (C and surrounds at −3 dB, LFE dropped).</summary>
    Surround51ToStereo,

    /// <summary>Pass-through but silence the LFE channel (index 3 in SMPTE 5.1 order).</summary>
    DropLfe,
}

/// <summary>One audible source→output contribution at <paramref name="GainDb"/>. Multiple
/// contributions targeting the same output channel sum at the router.</summary>
public readonly record struct DownmixContribution(int InputChannel, int OutputChannel, double GainDb);

public static class AudioDownmixPresets
{
    /// <summary>−3.01 dB ≈ 0.7071 linear — the standard fold-down coefficient for C/surrounds.</summary>
    public const double Minus3Db = -3.0102999566398121;

    /// <summary>LFE channel index in SMPTE/ITU 5.1 order (0=L 1=R 2=C 3=LFE 4=Ls 5=Rs).</summary>
    public const int Lfe51Channel = 3;

    public static IReadOnlyList<AudioDownmixPreset> All { get; } = Enum.GetValues<AudioDownmixPreset>();

    public static string DisplayName(AudioDownmixPreset preset) => preset switch
    {
        AudioDownmixPreset.PassThrough => "Pass-through",
        AudioDownmixPreset.MonoToStereo => "Mono → stereo",
        AudioDownmixPreset.Surround51ToStereo => "5.1 → stereo",
        AudioDownmixPreset.DropLfe => "Drop LFE",
        _ => preset.ToString(),
    };

    /// <summary>Whether the preset makes sense for the given channel counts (used to enable/disable the
    /// quick-apply buttons so an operator can't apply, e.g., a 5.1 fold-down to a stereo source).</summary>
    public static bool IsApplicable(AudioDownmixPreset preset, int inputChannels, int outputChannels)
    {
        if (inputChannels < 1 || outputChannels < 1)
            return false;
        return preset switch
        {
            AudioDownmixPreset.PassThrough => true,
            AudioDownmixPreset.MonoToStereo => outputChannels >= 2,
            AudioDownmixPreset.Surround51ToStereo => inputChannels >= 6 && outputChannels >= 2,
            AudioDownmixPreset.DropLfe => inputChannels > Lfe51Channel,
            _ => false,
        };
    }

    /// <summary>Audible contributions for the preset, clamped to the available channels. Cells/routes
    /// not yielded should be muted by the caller.</summary>
    public static IEnumerable<DownmixContribution> Contributions(AudioDownmixPreset preset, int inputChannels, int outputChannels)
    {
        switch (preset)
        {
            case AudioDownmixPreset.PassThrough:
                for (var i = 0; i < Math.Min(inputChannels, outputChannels); i++)
                    yield return new DownmixContribution(i, i, 0.0);
                break;

            case AudioDownmixPreset.MonoToStereo:
                if (inputChannels >= 1)
                    for (var o = 0; o < outputChannels; o++)
                        yield return new DownmixContribution(0, o, 0.0);
                break;

            case AudioDownmixPreset.Surround51ToStereo:
                if (inputChannels >= 6 && outputChannels >= 2)
                {
                    yield return new DownmixContribution(0, 0, 0.0);        // L  → L
                    yield return new DownmixContribution(1, 1, 0.0);        // R  → R
                    yield return new DownmixContribution(2, 0, Minus3Db);   // C  → L
                    yield return new DownmixContribution(2, 1, Minus3Db);   // C  → R
                    yield return new DownmixContribution(4, 0, Minus3Db);   // Ls → L
                    yield return new DownmixContribution(5, 1, Minus3Db);   // Rs → R
                }
                break;

            case AudioDownmixPreset.DropLfe:
                for (var i = 0; i < Math.Min(inputChannels, outputChannels); i++)
                {
                    if (i == Lfe51Channel)
                        continue; // silence the LFE channel
                    yield return new DownmixContribution(i, i, 0.0);
                }
                break;
        }
    }
}
