using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using S.Media.Core;
using S.Media.Core.Audio;

namespace S.Media.FFmpeg.Audio;

/// <summary>
/// Pull-based decoder for the best audio stream in a container. Frames are
/// converted to packed (interleaved) 32-bit float at the source sample rate
/// and channel count — the AV mixer handles any further resampling.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe. <see cref="ReadInto"/> writes <c>swr_convert</c> output
/// directly into the caller's buffer (zero allocations on the hot path);
/// <c>swr</c>'s own internal queue handles overflow when a codec frame
/// produces more samples than the destination has room for.
/// </para>
/// <para>
/// <see cref="TryReadNextFrame"/> still allocates a fresh sample buffer per
/// returned <see cref="AudioFrame"/> (the contract: frames survive across
/// reads). The two APIs share the same <c>swr</c> context — interleaving
/// calls between them is supported but the AudioFrame's PTS may include
/// samples buffered from a prior <see cref="ReadInto"/>.
/// </para>
/// <para>
/// Optional <see cref="AudioFileDecoderOpenOptions.CodecThreadCount"/> forwards to libav
/// <c>AVCodecContext.thread_count</c> before <c>avcodec_open2</c> (non-zero values clamped to 1…64). When the codec advertises frame or slice threading,
/// <c>thread_type</c> is set from <see cref="AudioFileDecoderOpenOptions.LibavThreadTypePreference"/> when the codec advertises both frame and slice threading; otherwise the single supported kind wins (same default precedence as <see cref="VideoFileDecoder.ApplyDecoderThreading"/> for the frame-first case). Otherwise only <c>thread_count</c> is set and libav may ignore it. Many audio decoders still run effectively single-threaded.
/// Splitting one stream across several libav contexts, pinning work to CPU cores, or other “second decoder” strategies are not built in — see <see cref="AudioFileDecoderOpenOptions"/> remarks (host-owned multi-context policy).
/// </para>
/// </remarks>
public sealed unsafe class AudioFileDecoder : IAudioSource, ISeekableSource, IDisposable
{
    private AVFormatContext* _formatCtx;
    private AVCodecContext* _codecCtx;
    private SwrContext* _swrCtx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private int _audioStreamIndex = -1;
    private AVRational _audioTimeBase;
    private long _samplesEmitted;
    private bool _eofReached;
    private bool _drainedTail;
    private bool _disposed;
    private bool _drainPacketSent;

    public AudioFormat Format { get; private set; }
    /// <summary>
    /// When <see cref="AudioFileDecoderOpenOptions.CodecThreadCount"/> was non-zero at open, the **clamped (1…64)** value assigned to
    /// <c>AVCodecContext.thread_count</c> before <c>avcodec_open2</c>; otherwise zero. Libav may still run fewer effective threads.
    /// </summary>
    public int CodecThreadCountOption { get; private set; }
    /// <summary>
    /// Libav <c>AVCodecContext.thread_type</c> after <c>avcodec_open2</c> when <see cref="CodecThreadCountOption"/> was non-zero (frame/slice threading requested when the codec supports it); otherwise zero.
    /// </summary>
    public int LibavCodecThreadType { get; private set; }
    public string CodecName { get; private set; } = "";
    public TimeSpan Duration { get; private set; }
    public TimeSpan Position { get; private set; }
    public bool IsAtEnd => _eofReached;

    /// <summary>
    /// <see cref="IAudioSource.IsExhausted"/>: true once the codec has hit EOF
    /// AND <c>swr</c>'s internal output queue has been fully drained by a
    /// prior <see cref="ReadInto"/> or <see cref="TryReadNextFrame"/>.
    /// </summary>
    public bool IsExhausted => _eofReached && _drainedTail;

    /// <summary>
    /// <see cref="IAudioSource.ReadInto"/>: zero-allocation pull. Writes
    /// <c>swr_convert</c> output straight into <paramref name="dst"/>; surplus
    /// samples (when a codec frame produces more than fits) stay in
    /// <c>swr</c>'s internal queue and are drained on the next call.
    /// </summary>
    public int ReadInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dst.Length % Format.Channels != 0)
            throw new ArgumentException(
                $"destination length {dst.Length} is not a multiple of channel count {Format.Channels}", nameof(dst));

        if (IsExhausted) return 0;

        var written = 0;
        while (written < dst.Length)
        {
            var remainingFrames = (dst.Length - written) / Format.Channels;
            if (remainingFrames == 0) break;

            // 1. Drain whatever swr already has queued from previous calls.
            var drained = SwrConvertInto(dst[written..], remainingFrames, null, 0);
            if (drained > 0)
            {
                written += drained * Format.Channels;
                _samplesEmitted += drained;
            }
            if (drained == remainingFrames) continue; // dst full from drain alone

            // 2. swr is empty (or returned a partial). If codec is at EOF, we're done.
            if (_eofReached)
            {
                if (drained == 0)
                {
                    _drainedTail = true;
                    break;
                }
                continue; // produced some tail; loop to see if there's more
            }

            // 3. Pull the next codec frame. If TryReceiveDecodedFrame reports EOF,
            //    loop iterates and the next drain handles the tail.
            if (!TryReceiveDecodedFrame()) continue;

            // 4. Feed the whole frame to swr; output up to remaining capacity into dst.
            //    swr keeps any surplus internally for the next iteration / call.
            var capacity = (dst.Length - written) / Format.Channels;
            var produced = SwrConvertInto(dst[written..], capacity, _frame->extended_data, _frame->nb_samples);
            if (produced > 0)
            {
                written += produced * Format.Channels;
                _samplesEmitted += produced;
            }
            av_frame_unref(_frame);
        }

        if (written > 0)
            Position = TimeSpan.FromSeconds((double)_samplesEmitted / Format.SampleRate);
        return written;
    }

    private AudioFileDecoder() { }

    public static AudioFileDecoder Open(string path, AudioFileDecoderOpenOptions options = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new FileNotFoundException("audio file not found", path);

        FFmpegRuntime.EnsureInitialized();

        var decoder = new AudioFileDecoder();
        try
        {
            decoder.OpenInternal(path, options);
            return decoder;
        }
        catch
        {
            decoder.Dispose();
            throw;
        }
    }

    public bool TryReadNextFrame(out AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryReceiveDecodedFrame())
            return TryDrainTailFrame(out frame);
        frame = ConvertFrame();
        av_frame_unref(_frame);
        return true;
    }

    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));

        var ts = (long)(position.TotalSeconds * _audioTimeBase.den / _audioTimeBase.num);
        var ret = av_seek_frame(_formatCtx, _audioStreamIndex, ts, AVSEEK_FLAG_BACKWARD);
        FFmpegException.ThrowIfError(ret, nameof(av_seek_frame));

        avcodec_flush_buffers(_codecCtx);

        // Clear any queued decoder/converter state after seek.
        av_packet_unref(_packet);
        av_frame_unref(_frame);
        swr_close(_swrCtx);
        ret = swr_init(_swrCtx);
        FFmpegException.ThrowIfError(ret, nameof(swr_init));

        _eofReached = false;
        _drainedTail = false;
        _drainPacketSent = false;
        _samplesEmitted = (long)(position.TotalSeconds * Format.SampleRate);
        Position = position;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_packet != null)    { var p = _packet;     av_packet_free(&p);          _packet = null; }
        if (_frame != null)     { var f = _frame;      av_frame_free(&f);           _frame = null; }
        if (_swrCtx != null)    { var s = _swrCtx;     swr_free(&s);                _swrCtx = null; }
        if (_codecCtx != null)  { var c = _codecCtx;   avcodec_free_context(&c);    _codecCtx = null; }
        if (_formatCtx != null) { var f = _formatCtx;  avformat_close_input(&f);    _formatCtx = null; }
    }

    // --- internals ---------------------------------------------------------

    private void OpenInternal(string path, AudioFileDecoderOpenOptions options)
    {
        AVFormatContext* fmt = null;
        var ret = avformat_open_input(&fmt, path, null, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_open_input));
        _formatCtx = fmt;

        ret = avformat_find_stream_info(_formatCtx, null);
        FFmpegException.ThrowIfError(ret, nameof(avformat_find_stream_info));

        AVCodec* codec = null;
        _audioStreamIndex = av_find_best_stream(_formatCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
        if (_audioStreamIndex < 0 || codec == null)
            throw new FFmpegException(_audioStreamIndex, "no decodable audio stream found");

        var stream = _formatCtx->streams[_audioStreamIndex];
        _audioTimeBase = stream->time_base;

        _codecCtx = avcodec_alloc_context3(codec);
        if (_codecCtx == null) throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");

        ret = avcodec_parameters_to_context(_codecCtx, stream->codecpar);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_to_context));

        CodecThreadCountOption = 0;
        LibavCodecThreadType = 0;
        if (options.CodecThreadCount > 0)
        {
            CodecThreadCountOption = Math.Clamp(options.CodecThreadCount, 1, 64);
            _codecCtx->thread_count = CodecThreadCountOption;
            var frameOk = (codec->capabilities & AV_CODEC_CAP_FRAME_THREADS) != 0;
            var sliceOk = (codec->capabilities & AV_CODEC_CAP_SLICE_THREADS) != 0;
            if (frameOk || sliceOk)
            {
                if (options.LibavThreadTypePreference == AudioDecoderLibavThreadTypePreference.SliceFirst)
                {
                    if (sliceOk)
                        _codecCtx->thread_type = (int)FF_THREAD_SLICE;
                    else if (frameOk)
                        _codecCtx->thread_type = (int)FF_THREAD_FRAME;
                }
                else
                {
                    if (frameOk)
                        _codecCtx->thread_type = (int)FF_THREAD_FRAME;
                    else if (sliceOk)
                        _codecCtx->thread_type = (int)FF_THREAD_SLICE;
                }
            }
        }

        ret = avcodec_open2(_codecCtx, codec, null);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        if (CodecThreadCountOption > 0)
            LibavCodecThreadType = _codecCtx->thread_type;

        Format = new AudioFormat(_codecCtx->sample_rate, _codecCtx->ch_layout.nb_channels);
        CodecName = Marshal.PtrToStringAnsi((IntPtr)codec->name) ?? "unknown";

        if (stream->duration > 0)
            Duration = PtsToTimeSpan(stream->duration);
        else if (_formatCtx->duration > 0)
            Duration = TimeSpan.FromMicroseconds(_formatCtx->duration);

        AVChannelLayout outLayout;
        av_channel_layout_default(&outLayout, Format.Channels);

        SwrContext* swr = null;
        ret = swr_alloc_set_opts2(&swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, Format.SampleRate,
            &_codecCtx->ch_layout, _codecCtx->sample_fmt, _codecCtx->sample_rate,
            0, null);
        av_channel_layout_uninit(&outLayout);
        FFmpegException.ThrowIfError(ret, nameof(swr_alloc_set_opts2));
        _swrCtx = swr;

        ret = swr_init(_swrCtx);
        FFmpegException.ThrowIfError(ret, nameof(swr_init));

        _packet = av_packet_alloc();
        if (_packet == null) throw new OutOfMemoryException("av_packet_alloc returned NULL");
        _frame = av_frame_alloc();
        if (_frame == null) throw new OutOfMemoryException("av_frame_alloc returned NULL");
    }

    /// <summary>Pulls the next decoded frame from the codec into <see cref="_frame"/>. Sets <see cref="_eofReached"/> on EOF.</summary>
    private bool TryReceiveDecodedFrame()
    {
        while (true)
        {
            var ret = avcodec_receive_frame(_codecCtx, _frame);
            if (ret == 0) return true;
            if (ret == AVERROR_EOF)
            {
                _eofReached = true;
                return false;
            }
            if (ret == AVERROR(EAGAIN))
            {
                FeedDecoder();
                continue;
            }
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));
            return false; // unreachable
        }
    }

    private void FeedDecoder()
    {
        while (true)
        {
            av_packet_unref(_packet);
            var ret = av_read_frame(_formatCtx, _packet);
            if (ret == AVERROR_EOF)
            {
                if (_drainPacketSent) return;

                ret = avcodec_send_packet(_codecCtx, null);
                if (ret == 0)
                {
                    _drainPacketSent = true;
                    return;
                }
                if (ret == AVERROR(EAGAIN)) return;
                FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
                return;
            }
            FFmpegException.ThrowIfError(ret, nameof(av_read_frame));

            if (_packet->stream_index != _audioStreamIndex) continue;

            ret = avcodec_send_packet(_codecCtx, _packet);
            if (ret == 0)
            {
                _drainPacketSent = false;
                return;
            }
            if (ret == AVERROR(EAGAIN)) return;
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_packet));
        }
    }

    private int SwrConvertInto(Span<float> dst, int capacityFrames, byte** inputData, int inputSamples)
    {
        if (capacityFrames <= 0) return 0;
        int converted;
        fixed (float* dstPtr = dst)
        {
            var outBuf = (byte*)dstPtr;
            converted = swr_convert(_swrCtx, &outBuf, capacityFrames, inputData, inputSamples);
        }
        if (converted < 0) FFmpegException.ThrowIfError(converted, nameof(swr_convert));
        return converted;
    }

    /// <summary>
    /// Rents an array from <see cref="ArrayPool{T}.Shared"/> and converts <see cref="_frame"/> into it.
    /// The returned <see cref="AudioFrame"/> carries a <see cref="AudioFrame.Release"/> callback that
    /// returns the buffer on <see cref="AudioFrame.Dispose"/>.
    /// </summary>
    private AudioFrame ConvertFrame()
    {
        var srcSamples = _frame->nb_samples;
        var outCapacity = (int)av_rescale_rnd(
            swr_get_delay(_swrCtx, Format.SampleRate) + srcSamples,
            Format.SampleRate, Format.SampleRate, AVRounding.AV_ROUND_UP);

        var totalFloats = outCapacity * Format.Channels;
        var samples = ArrayPool<float>.Shared.Rent(totalFloats);
        int converted;
        fixed (float* outPtr = samples)
        {
            var outBuf = (byte*)outPtr;
            converted = swr_convert(_swrCtx, &outBuf, outCapacity, _frame->extended_data, srcSamples);
        }
        if (converted < 0)
        {
            ArrayPool<float>.Shared.Return(samples);
            FFmpegException.ThrowIfError(converted, nameof(swr_convert));
        }

        var pts = ResolvePts();
        Position = pts + TimeSpan.FromSeconds((double)converted / Format.SampleRate);
        _samplesEmitted += converted;

        var owned = samples;
        // Idempotent single-shot Release: Interlocked guards double-Dispose from returning
        // the same buffer twice (AudioFrame's XML doc promises multi-call safety).
        var released = 0;
        return new AudioFrame(
            pts,
            Format,
            converted,
            samples.AsMemory(0, converted * Format.Channels),
            Release: DisposableRelease.Wrap(() =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                    ArrayPool<float>.Shared.Return(owned, clearArray: false);
            }));
    }

    private bool TryDrainTailFrame(out AudioFrame frame)
    {
        if (_drainedTail)
        {
            frame = default;
            return false;
        }

        var delay = swr_get_delay(_swrCtx, Format.SampleRate);
        var outCapacity = (int)av_rescale_rnd(delay, Format.SampleRate, Format.SampleRate, AVRounding.AV_ROUND_UP);
        if (outCapacity <= 0)
        {
            _drainedTail = true;
            frame = default;
            return false;
        }

        var totalFloats = outCapacity * Format.Channels;
        var samples = ArrayPool<float>.Shared.Rent(totalFloats);
        int converted;
        fixed (float* outPtr = samples)
        {
            var outBuf = (byte*)outPtr;
            converted = swr_convert(_swrCtx, &outBuf, outCapacity, null, 0);
        }
        if (converted < 0)
        {
            ArrayPool<float>.Shared.Return(samples);
            FFmpegException.ThrowIfError(converted, nameof(swr_convert));
        }

        if (converted == 0)
        {
            ArrayPool<float>.Shared.Return(samples);
            _drainedTail = true;
            frame = default;
            return false;
        }

        var pts = TimeSpan.FromSeconds((double)_samplesEmitted / Format.SampleRate);
        Position = pts + TimeSpan.FromSeconds((double)converted / Format.SampleRate);
        _samplesEmitted += converted;

        var owned = samples;
        var released = 0;
        frame = new AudioFrame(
            pts,
            Format,
            converted,
            samples.AsMemory(0, converted * Format.Channels),
            Release: DisposableRelease.Wrap(() =>
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                    ArrayPool<float>.Shared.Return(owned, clearArray: false);
            }));
        return true;
    }

    private TimeSpan ResolvePts()
    {
        var pts = _frame->best_effort_timestamp;
        if (pts == AV_NOPTS_VALUE) pts = _frame->pts;
        return pts == AV_NOPTS_VALUE
            ? TimeSpan.FromSeconds((double)_samplesEmitted / Format.SampleRate)
            : PtsToTimeSpan(pts);
    }

    private TimeSpan PtsToTimeSpan(long pts)
        => TimeSpan.FromSeconds((double)pts * _audioTimeBase.num / _audioTimeBase.den);
}
