namespace S.Media.Core.Audio;

/// <summary>
/// Standard channel-layout gain matrices for <see cref="AudioRouter.ApplyMatrix"/>, indexed
/// <c>[sourceChannel, outputChannel]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Channel order follows the FFmpeg/SMPTE default layouts the decoder negotiates
/// (<c>av_channel_layout_default</c>): stereo = FL FR; 5.1 = FL FR FC LFE BL BR;
/// 7.1 = FL FR FC LFE BL BR SL SR. Downmix coefficients are ITU-R BS.775 style
/// (center and surrounds folded at −3 dB, LFE dropped).
/// </para>
/// <para>
/// With <c>normalize: true</c> (the default) every matrix is scaled so no output channel's
/// summed |gain| exceeds 1.0 - a full-scale correlated input cannot clip. Pass
/// <c>normalize: false</c> for the raw textbook coefficients.
/// </para>
/// </remarks>
public static class AudioChannelLayoutPresets
{
    private const float MinusThreeDb = 0.70710678f; // 1/sqrt(2)

    /// <summary>Identity NxN matrix (channel i → channel i at unity).</summary>
    public static float[,] Passthrough(int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        var m = new float[channels, channels];
        for (var i = 0; i < channels; i++)
            m[i, i] = 1f;
        return m;
    }

    /// <summary>
    /// Standard mix matrix from <paramref name="sourceChannels"/> to
    /// <paramref name="outputChannels"/>. Throws when no standard mapping exists - use
    /// <see cref="TryGetDownmix"/> to probe, or build a custom matrix.
    /// </summary>
    public static float[,] Downmix(int sourceChannels, int outputChannels, bool normalize = true) =>
        TryGetDownmix(sourceChannels, outputChannels, out var m, normalize)
            ? m
            : throw new NotSupportedException(
                $"no standard downmix from {sourceChannels} to {outputChannels} channels - supported: equal counts, mono↔N, 2→1, N≥src identity placement, 5.1→2.0, 7.1→2.0, 7.1→5.1.");

    /// <summary>Probe form of <see cref="Downmix"/>.</summary>
    public static bool TryGetDownmix(int sourceChannels, int outputChannels, out float[,] gains, bool normalize = true)
    {
        gains = null!;
        if (sourceChannels < 1 || outputChannels < 1)
            return false;

        if (sourceChannels == outputChannels)
        {
            gains = Passthrough(sourceChannels);
            return true;
        }

        float[,]? m = null;
        if (sourceChannels == 1)
        {
            // Mono fans out to every output at unity.
            m = new float[1, outputChannels];
            for (var d = 0; d < outputChannels; d++)
                m[0, d] = 1f;
        }
        else if (sourceChannels == 2 && outputChannels == 1)
        {
            m = new float[2, 1];
            m[0, 0] = 0.5f;
            m[1, 0] = 0.5f;
        }
        else if (sourceChannels == 6 && outputChannels == 2)
        {
            // 5.1 (FL FR FC LFE BL BR) → stereo: L = FL + .707·FC + .707·BL, LFE dropped.
            m = new float[6, 2];
            m[0, 0] = 1f;
            m[1, 1] = 1f;
            m[2, 0] = MinusThreeDb;
            m[2, 1] = MinusThreeDb;
            m[4, 0] = MinusThreeDb;
            m[5, 1] = MinusThreeDb;
        }
        else if (sourceChannels == 8 && outputChannels == 2)
        {
            // 7.1 (FL FR FC LFE BL BR SL SR) → stereo: backs and sides both fold at −3 dB.
            m = new float[8, 2];
            m[0, 0] = 1f;
            m[1, 1] = 1f;
            m[2, 0] = MinusThreeDb;
            m[2, 1] = MinusThreeDb;
            m[4, 0] = MinusThreeDb;
            m[5, 1] = MinusThreeDb;
            m[6, 0] = MinusThreeDb;
            m[7, 1] = MinusThreeDb;
        }
        else if (sourceChannels == 8 && outputChannels == 6)
        {
            // 7.1 → 5.1: front four pass, sides fold into the backs at −3 dB.
            m = new float[8, 6];
            for (var i = 0; i < 6; i++)
                m[i, i] = 1f;
            m[6, 4] = MinusThreeDb;
            m[7, 5] = MinusThreeDb;
        }
        else if (sourceChannels < outputChannels)
        {
            // More outputs than sources: place channels identity-wise, leave the rest silent.
            m = new float[sourceChannels, outputChannels];
            for (var i = 0; i < sourceChannels; i++)
                m[i, i] = 1f;
        }

        if (m is null)
            return false;

        if (normalize)
            NormalizeColumns(m);
        gains = m;
        return true;
    }

    /// <summary>Scales the whole matrix so no output channel's summed |gain| exceeds 1.0.</summary>
    private static void NormalizeColumns(float[,] m)
    {
        var srcChannels = m.GetLength(0);
        var dstChannels = m.GetLength(1);
        var maxSum = 0f;
        for (var d = 0; d < dstChannels; d++)
        {
            var sum = 0f;
            for (var s = 0; s < srcChannels; s++)
                sum += Math.Abs(m[s, d]);
            if (sum > maxSum)
                maxSum = sum;
        }

        if (maxSum <= 1f)
            return;

        var scale = 1f / maxSum;
        for (var s = 0; s < srcChannels; s++)
        {
            for (var d = 0; d < dstChannels; d++)
                m[s, d] *= scale;
        }
    }
}
