using System.Runtime.InteropServices;

namespace S.Media.Decode.FFmpeg.Audio;

/// <summary>
/// Reusable <c>swresample</c> converter for packed 32‑bit float, interleaved audio.
/// </summary>
/// <remarks>
/// <para>Not thread-safe - one instance per concurrent pipeline.</para>
/// <para><see cref="Dispose"/> frees the <c>swresample</c> context; <strong>Debug</strong> builds log failures via <see cref="MediaDiagnostics.LogError"/>.</para>
/// </remarks>
public sealed unsafe class AudioResampler : IDisposable
{
    private SwrContext* _swr;
    private readonly AudioFormat _src;
    private readonly AudioFormat _dst;
    private bool _disposed;

    private AudioResampler(SwrContext* swr, AudioFormat src, AudioFormat dst)
    {
        _swr = swr;
        _src = src;
        _dst = dst;
    }

    public AudioFormat SourceFormat => _src;
    public AudioFormat DestinationFormat => _dst;

    public static AudioResampler Create(AudioFormat src, AudioFormat dst)
    {
        FFmpegRuntime.EnsureInitialized();
        if (src.SampleRate <= 0 || dst.SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(dst), "sample rates must be positive");
        if (src.Channels <= 0 || dst.Channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(dst), "channels must be positive");

        AVChannelLayout inLayout, outLayout;
        av_channel_layout_default(&inLayout, src.Channels);
        av_channel_layout_default(&outLayout, dst.Channels);

        SwrContext* swr = null;
        var ret = swr_alloc_set_opts2(&swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, dst.SampleRate,
            &inLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, src.SampleRate,
            0, null);
        av_channel_layout_uninit(&outLayout);
        av_channel_layout_uninit(&inLayout);
        FFmpegException.ThrowIfError(ret, nameof(swr_alloc_set_opts2));

        ret = swr_init(swr);
        if (ret < 0)
        {
            var s = swr;
            swr_free(&s);
            FFmpegException.ThrowIfError(ret, nameof(swr_init));
        }

        return new AudioResampler(swr, src, dst);
    }

    /// <summary>
    /// Converts using <paramref name="srcFrameCount"/> input PCM frames packed in
    /// <paramref name="srcSamples"/>, producing up to <paramref name="dstMaxFrames"/> output frames.
    /// </summary>
    /// <returns>Output PCM frames stored into <paramref name="dstSamples"/>.</returns>
    public int Convert(
        ReadOnlySpan<float> srcSamples, int srcFrameCount,
        Span<float> dstSamples, int dstMaxFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(srcFrameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(dstMaxFrames);

        var expectedSrc = checked(srcFrameCount * _src.Channels);
        var expectedDstCap = checked(dstMaxFrames * _dst.Channels);
        if (srcSamples.Length < expectedSrc)
            throw new ArgumentException($"src spans {srcSamples.Length} floats but srcFrameCount * srcChannels implies {expectedSrc}", nameof(srcSamples));
        if (dstSamples.Length < expectedDstCap)
            throw new ArgumentException($"dst spans {dstSamples.Length} floats but dst capacity implies {expectedDstCap}", nameof(dstSamples));

        fixed (float* srcFixed = srcSamples)
        fixed (float* dstFixed = dstSamples)
        {
            var dstBytes = (byte*)dstFixed;

            byte* plane = srcFrameCount <= 0 ? null : (byte*)srcFixed;
            var converted = srcFrameCount <= 0
                ? swr_convert(_swr, &dstBytes, dstMaxFrames, null, 0)
                : swr_convert(_swr, &dstBytes, dstMaxFrames, &plane, srcFrameCount);
            if (converted < 0) FFmpegException.ThrowIfError(converted, nameof(swr_convert));
            return converted;
        }
    }

    /// <summary>Flush any queued samples retained inside <c>swr</c>.</summary>
    public int Drain(Span<float> dstSamples, int dstMaxFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(dstMaxFrames);
        if (dstSamples.Length < checked(dstMaxFrames * _dst.Channels))
            throw new ArgumentException("dst shorter than dstMaxFrames * dstChannels", nameof(dstSamples));

        fixed (float* dstFixed = dstSamples)
        {
            var dstBytes = (byte*)dstFixed;
            var converted = swr_convert(_swr, &dstBytes, dstMaxFrames, null, 0);
            if (converted < 0) FFmpegException.ThrowIfError(converted, nameof(swr_convert));
            return converted;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_swr == null) return;
        MediaDiagnostics.SwallowDisposeErrors(() =>
        {
            var s = _swr;
            swr_free(&s);
        }, "AudioResampler.Dispose");
        _swr = null;
    }
}
