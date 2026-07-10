using System.Runtime.InteropServices;

namespace S.Media.FFmpeg.Common;

/// <summary>
/// Narrowly scoped libavformat stream-copy remuxer (no transcode): combines a video-only and an
/// audio-only input file into one local container, or passes a single input through. Built for the
/// YouTube prepare path (separate best-quality A/V streams → one asset the normal FFmpeg open path
/// plays), deliberately NOT a general encoder - see the old tree's <c>S.Media.FFmpeg.Encode</c> for
/// raw-frame encoding. In-process libavformat, no shelled <c>ffmpeg</c> binary (review: cue logic must
/// not spawn untracked processes).
/// </summary>
public static class FFmpegStreamCopyRemuxer
{
    /// <summary>
    /// Remuxes the first video stream of <paramref name="videoPath"/> and/or the first audio stream of
    /// <paramref name="audioPath"/> into <paramref name="outputPath"/> (container chosen by the output
    /// extension - use <c>.mkv</c> for codec-agnostic storage). Either input may be null (single-stream
    /// pass-through), not both. Packet timestamps are rescaled; packets are written in dts order across
    /// inputs. Cancellation deletes nothing - the caller owns partial-file semantics (write to a
    /// <c>.partial</c> path and rename on success).
    /// </summary>
    /// <param name="progress">Coarse 0..1 progress from packet timestamps against the longest input duration.</param>
    /// <param name="containerFormat">Explicit libav muxer name (e.g. <c>"matroska"</c>). Required when the
    /// output path's extension doesn't name the container - e.g. atomic-commit <c>.partial</c> temp paths.</param>
    public static unsafe void Remux(
        string? videoPath,
        string? audioPath,
        string outputPath,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null,
        string? containerFormat = null)
    {
        if (videoPath is null && audioPath is null)
            throw new ArgumentException("at least one of videoPath/audioPath is required");
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        FFmpegRuntime.EnsureInitialized();

        var inputs = new List<InputLeg>(2);
        AVFormatContext* output = null;
        try
        {
            if (videoPath is not null)
                inputs.Add(InputLeg.Open(videoPath, AVMediaType.AVMEDIA_TYPE_VIDEO));
            if (audioPath is not null)
                inputs.Add(InputLeg.Open(audioPath, AVMediaType.AVMEDIA_TYPE_AUDIO));

            AVFormatContext* outTmp;
            FFmpegException.ThrowIfError(
                avformat_alloc_output_context2(&outTmp, null, containerFormat, outputPath),
                nameof(avformat_alloc_output_context2));
            output = outTmp;

            foreach (var leg in inputs)
            {
                var outStream = avformat_new_stream(output, null);
                if (outStream is null)
                    throw new FFmpegException("avformat_new_stream returned null");
                FFmpegException.ThrowIfError(
                    avcodec_parameters_copy(outStream->codecpar, leg.InStream->codecpar),
                    nameof(avcodec_parameters_copy));
                outStream->codecpar->codec_tag = 0; // container-specific tags must not leak across formats
                leg.OutStreamIndex = outStream->index;
            }

            if ((output->oformat->flags & AVFMT_NOFILE) == 0)
            {
                AVIOContext* io;
                FFmpegException.ThrowIfError(
                    avio_open(&io, outputPath, AVIO_FLAG_WRITE), nameof(avio_open));
                output->pb = io;
            }

            FFmpegException.ThrowIfError(avformat_write_header(output, null), nameof(avformat_write_header));

            var totalDuration = inputs.Max(l => l.DurationSeconds);
            // Prime one pending packet per leg, then always write the leg whose packet is earliest in
            // stream time - av_interleaved_write_frame then only has to buffer small reorder windows.
            foreach (var leg in inputs)
                leg.Advance();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InputLeg? next = null;
                foreach (var leg in inputs)
                {
                    if (!leg.HasPacket)
                        continue;
                    if (next is null || av_compare_ts(
                            leg.Packet->dts, leg.InStream->time_base,
                            next.Packet->dts, next.InStream->time_base) < 0)
                        next = leg;
                }

                if (next is null)
                    break; // every leg drained

                var outStream = output->streams[next.OutStreamIndex];
                if (totalDuration > 0 && progress is not null && next.Packet->pts != AV_NOPTS_VALUE)
                {
                    var seconds = next.Packet->pts * av_q2d(next.InStream->time_base);
                    progress.Report(Math.Clamp(seconds / totalDuration, 0d, 1d));
                }

                av_packet_rescale_ts(next.Packet, next.InStream->time_base, outStream->time_base);
                next.Packet->stream_index = next.OutStreamIndex;
                next.Packet->pos = -1;
                FFmpegException.ThrowIfError(
                    av_interleaved_write_frame(output, next.Packet), nameof(av_interleaved_write_frame));
                next.Advance();
            }

            FFmpegException.ThrowIfError(av_write_trailer(output), nameof(av_write_trailer));
            progress?.Report(1d);
        }
        finally
        {
            foreach (var leg in inputs)
                leg.Dispose();
            if (output is not null)
            {
                if (output->pb is not null && (output->oformat->flags & AVFMT_NOFILE) == 0)
                {
                    var pb = output->pb;
                    avio_closep(&pb);
                    output->pb = null;
                }

                avformat_free_context(output);
            }
        }
    }

    /// <summary>One input file contributing its first stream of a given media type.</summary>
    private sealed unsafe class InputLeg : IDisposable
    {
        private AVFormatContext* _format;
        private readonly int _inStreamIndex;

        public AVPacket* Packet { get; private set; }
        public bool HasPacket { get; private set; }
        public int OutStreamIndex { get; set; }
        public AVStream* InStream => _format->streams[_inStreamIndex];
        public double DurationSeconds { get; }

        private InputLeg(AVFormatContext* format, int streamIndex)
        {
            _format = format;
            _inStreamIndex = streamIndex;
            Packet = av_packet_alloc();
            if (Packet is null)
                throw new OutOfMemoryException("av_packet_alloc");
            DurationSeconds = format->duration > 0 ? format->duration / (double)AV_TIME_BASE : 0d;
        }

        public static InputLeg Open(string path, AVMediaType type)
        {
            AVFormatContext* format = null;
            FFmpegException.ThrowIfError(
                avformat_open_input(&format, path, null, null), nameof(avformat_open_input));
            try
            {
                FFmpegException.ThrowIfError(
                    avformat_find_stream_info(format, null), nameof(avformat_find_stream_info));
                var index = av_find_best_stream(format, type, -1, -1, null, 0);
                if (index < 0)
                    throw new FFmpegException($"'{path}' has no {type} stream to remux");
                return new InputLeg(format, index);
            }
            catch
            {
                avformat_close_input(&format);
                throw;
            }
        }

        /// <summary>Reads until the next packet of the selected stream (or EOF).</summary>
        public void Advance()
        {
            while (true)
            {
                av_packet_unref(Packet);
                var ret = av_read_frame(_format, Packet);
                if (ret == AVERROR_EOF)
                {
                    HasPacket = false;
                    return;
                }

                FFmpegException.ThrowIfError(ret, nameof(av_read_frame));
                if (Packet->stream_index == _inStreamIndex)
                {
                    HasPacket = true;
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (Packet is not null)
            {
                var p = Packet;
                av_packet_free(&p);
                Packet = null;
            }

            if (_format is not null)
            {
                var f = _format;
                avformat_close_input(&f);
                _format = null;
            }
        }
    }
}
