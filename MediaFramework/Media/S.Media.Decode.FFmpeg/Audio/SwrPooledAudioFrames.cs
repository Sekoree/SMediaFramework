using System.Buffers;

namespace S.Media.Decode.FFmpeg.Audio;

/// <summary>
/// The single implementation of "swr_convert into a pooled buffer and wrap it as an
/// <see cref="AudioFrame"/>" shared by <see cref="AudioFileDecoder"/> and
/// <c>MediaContainerSharedDemux</c>. Both used to carry verbatim copies of this code
/// (rent → convert → idempotent pooled release), so fixes had to land twice; the delicate
/// parts - the error-path buffer return and the Interlocked double-Dispose guard the
/// <see cref="AudioFrame"/> contract requires - now live here only.
/// </summary>
/// <remarks>
/// Callers own all decoder/track state: they resolve the PTS to stamp, then apply the returned
/// converted-sample count to their own position / samples-emitted bookkeeping. The helpers only
/// touch the <c>swr</c> context and the pool.
/// </remarks>
internal static unsafe class SwrPooledAudioFrames
{
    /// <summary>
    /// Converts one decoded <paramref name="frame"/> (plus any samples swr buffered internally)
    /// into a pooled <see cref="AudioFrame"/> stamped with <paramref name="pts"/>.
    /// <paramref name="convertedSamples"/> is the per-channel sample count actually produced.
    /// </summary>
    internal static AudioFrame ConvertDecodedFrame(
        SwrContext* swr, AVFrame* frame, AudioFormat format, TimeSpan pts, out int convertedSamples)
    {
        var srcSamples = frame->nb_samples;
        var outCapacity = (int)av_rescale_rnd(
            swr_get_delay(swr, format.SampleRate) + srcSamples,
            format.SampleRate, format.SampleRate, AVRounding.AV_ROUND_UP);
        return RentAndConvert(swr, frame->extended_data, srcSamples, outCapacity, format, pts, out convertedSamples);
    }

    /// <summary>
    /// Drains swr's internal tail (end of stream) into a pooled <see cref="AudioFrame"/> stamped
    /// with <paramref name="pts"/>. Returns <c>false</c> - with no frame - when swr had nothing
    /// buffered; the caller then latches its drained-tail flag.
    /// </summary>
    internal static bool TryDrainTail(
        SwrContext* swr, AudioFormat format, TimeSpan pts, out AudioFrame frame, out int convertedSamples)
    {
        frame = default;
        convertedSamples = 0;

        var delay = swr_get_delay(swr, format.SampleRate);
        var outCapacity = (int)av_rescale_rnd(delay, format.SampleRate, format.SampleRate, AVRounding.AV_ROUND_UP);
        if (outCapacity <= 0)
            return false;

        var tail = RentAndConvert(swr, null, 0, outCapacity, format, pts, out convertedSamples);
        if (convertedSamples == 0)
        {
            tail.Dispose(); // returns the rented buffer
            return false;
        }

        frame = tail;
        return true;
    }

    private static AudioFrame RentAndConvert(
        SwrContext* swr, byte** input, int inputSamples, int outCapacity,
        AudioFormat format, TimeSpan pts, out int convertedSamples)
    {
        var totalFloats = outCapacity * format.Channels;
        var samples = ArrayPool<float>.Shared.Rent(totalFloats);
        int converted;
        fixed (float* outPtr = samples)
        {
            var outBuf = (byte*)outPtr;
            converted = swr_convert(swr, &outBuf, outCapacity, input, inputSamples);
        }
        if (converted < 0)
        {
            ArrayPool<float>.Shared.Return(samples);
            FFmpegException.ThrowIfError(converted, nameof(swr_convert));
        }

        convertedSamples = converted;
        var owned = samples;
        // Idempotent single-shot Release: Interlocked guards double-Dispose from returning
        // the same buffer twice (AudioFrame's XML doc promises multi-call safety).
        var released = 0;
        return new AudioFrame(
            pts,
            format,
            converted,
            samples.AsMemory(0, converted * format.Channels),
            Release: DisposableRelease.Wrap(() =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                    ArrayPool<float>.Shared.Return(owned, clearArray: false);
            }));
    }
}
