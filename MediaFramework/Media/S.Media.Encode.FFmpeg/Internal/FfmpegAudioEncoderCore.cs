namespace S.Media.Encode.FFmpeg.Internal;

/// <summary>
/// One audio leg: interleaved float in → swresample (sample format + channel layout + sample rate
/// conversion) → <c>AVAudioFifo</c> → fixed-size codec frames → packets. Single-threaded (session
/// encode worker). Salvaged from the pre-rewrite FfmpegAudioEncoder, extended with channel/rate
/// conversion via the canonical swr+fifo pattern (the old 1:1 swr path couldn't resample).
/// </summary>
internal sealed unsafe class FfmpegAudioEncoderCore : IDisposable
{
    private readonly AudioLegOptions _options;
    private AVCodecContext* _codec;
    private AVCodecParameters* _codecParameters;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwrContext* _swr;
    private AVAudioFifo* _fifo;
    private byte** _convertBuffer;
    private int _convertBufferSamples;
    private AudioFormat _inputFormat;
    private int _frameSamples;
    private long _ptsSamples;
    private bool _disposed;

    internal FfmpegAudioEncoderCore(AudioLegOptions options, AudioFormat inputFormat)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        FFmpegRuntime.EnsureInitialized();
        inputFormat.Validate(nameof(inputFormat));
        _inputFormat = inputFormat;
        OpenCodec();
    }

    internal AVCodecParameters* CodecParameters => _codecParameters;

    internal AVRational TimeBase => _codec->time_base;

    internal AudioFormat InputFormat => _inputFormat;

    /// <summary>Feeds interleaved float samples (the input format's channel count); emits packets for
    /// every full codec frame that becomes available. Timestamps are in output-rate samples.</summary>
    internal void Submit(ReadOnlySpan<float> packedSamples, Action<IntPtr> onPacket)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packedSamples.IsEmpty)
            return;
        if (packedSamples.Length % _inputFormat.Channels != 0)
            throw new ArgumentException(
                $"packedSamples length {packedSamples.Length} is not a multiple of channel count {_inputFormat.Channels}.",
                nameof(packedSamples));

        var inSamples = packedSamples.Length / _inputFormat.Channels;
        // Worst-case output of this convert call: buffered + rescaled input, padded.
        var outCap = (int)av_rescale_rnd(
            swr_get_delay(_swr, _inputFormat.SampleRate) + inSamples,
            _codec->sample_rate, _inputFormat.SampleRate, AVRounding.AV_ROUND_UP) + 64;
        EnsureConvertBuffer(outCap);

        fixed (float* src = packedSamples)
        {
            var inBuf = (byte*)src;
            var converted = swr_convert(_swr, _convertBuffer, outCap, &inBuf, inSamples);
            if (converted < 0)
                FFmpegException.ThrowIfError(converted, nameof(swr_convert));
            if (converted > 0)
                WriteToFifo(converted);
        }

        DrainFullFrames(onPacket);
    }

    /// <summary>End of stream: drain swr's tail, pad the final partial frame with silence, flush the codec.</summary>
    internal void Flush(Action<IntPtr> onPacket)
    {
        if (_disposed || _codec is null)
            return;

        // Drain whatever swr still buffers (resampler latency).
        while (true)
        {
            var outCap = 4096;
            EnsureConvertBuffer(outCap);
            var converted = swr_convert(_swr, _convertBuffer, outCap, null, 0);
            if (converted <= 0)
                break;
            WriteToFifo(converted);
        }

        DrainFullFrames(onPacket);

        var remaining = av_audio_fifo_size(_fifo);
        if (remaining > 0)
        {
            // Final partial frame padded with silence.
            var wr = av_frame_make_writable(_frame);
            FFmpegException.ThrowIfError(wr, nameof(av_frame_make_writable));
            av_samples_set_silence(_frame->extended_data, 0, _frameSamples, _codec->ch_layout.nb_channels, _codec->sample_fmt);
            var read = av_audio_fifo_read(_fifo, (void**)_frame->extended_data, remaining);
            if (read < 0)
                FFmpegException.ThrowIfError(read, nameof(av_audio_fifo_read));
            _frame->pts = _ptsSamples;
            _ptsSamples += _frameSamples;
            SendFrame(_frame, onPacket);
        }

        var ret = avcodec_send_frame(_codec, null);
        if (ret < 0 && ret != AVERROR_EOF)
            FFmpegException.ThrowIfError(ret, nameof(avcodec_send_frame));
        DrainPackets(onPacket);
    }

    private void WriteToFifo(int samples)
    {
        var ret = av_audio_fifo_write(_fifo, (void**)_convertBuffer, samples);
        if (ret < samples)
            FFmpegException.ThrowIfError(ret < 0 ? ret : -1, nameof(av_audio_fifo_write));
    }

    private void DrainFullFrames(Action<IntPtr> onPacket)
    {
        while (av_audio_fifo_size(_fifo) >= _frameSamples)
        {
            var wr = av_frame_make_writable(_frame);
            FFmpegException.ThrowIfError(wr, nameof(av_frame_make_writable));
            var read = av_audio_fifo_read(_fifo, (void**)_frame->extended_data, _frameSamples);
            if (read < _frameSamples)
                FFmpegException.ThrowIfError(read < 0 ? read : -1, nameof(av_audio_fifo_read));
            _frame->pts = _ptsSamples;
            _ptsSamples += _frameSamples;
            SendFrame(_frame, onPacket);
        }
    }

    private void SendFrame(AVFrame* frame, Action<IntPtr> onPacket)
    {
        var ret = avcodec_send_frame(_codec, frame);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_send_frame));
        DrainPackets(onPacket);
    }

    private void DrainPackets(Action<IntPtr> onPacket)
    {
        while (true)
        {
            var ret = avcodec_receive_packet(_codec, _packet);
            if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
                break;
            FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_packet));
            try
            {
                onPacket((IntPtr)_packet);
            }
            finally
            {
                av_packet_unref(_packet);
            }
        }
    }

    private void EnsureConvertBuffer(int samples)
    {
        if (_convertBuffer is not null && _convertBufferSamples >= samples)
            return;

        FreeConvertBuffer();
        byte** buffer = null;
        int linesize;
        var ret = av_samples_alloc_array_and_samples(
            &buffer, &linesize, _codec->ch_layout.nb_channels, samples, _codec->sample_fmt, 0);
        FFmpegException.ThrowIfError(ret, nameof(av_samples_alloc_array_and_samples));
        _convertBuffer = buffer;
        _convertBufferSamples = samples;
    }

    private void FreeConvertBuffer()
    {
        if (_convertBuffer is null)
            return;
        av_freep(&_convertBuffer[0]);
        var b = _convertBuffer;
        av_freep(&b);
        _convertBuffer = null;
        _convertBufferSamples = 0;
    }

    private void OpenCodec()
    {
        var codec = FfmpegEncodeMaps.FindAudioEncoder(_options.Codec);
        if (codec is null)
            throw new InvalidOperationException($"no FFmpeg encoder for {_options.Codec}");

        _codec = avcodec_alloc_context3(codec);
        if (_codec is null)
            throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");

        var outChannels = _options.Channels > 0 ? _options.Channels : _inputFormat.Channels;
        var outRate = _options.SampleRate > 0 ? _options.SampleRate : _inputFormat.SampleRate;

        av_channel_layout_default(&_codec->ch_layout, outChannels);
        _codec->sample_rate = outRate;
        _codec->sample_fmt = PickSampleFormat(codec);
        _codec->time_base = new AVRational { num = 1, den = outRate };
        if (_options.BitrateBps > 0)
            _codec->bit_rate = _options.BitrateBps;

        // GLOBAL_HEADER unconditionally - packets fan out to N muxers (see video core).
        _codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;

        var ret = avcodec_open2(_codec, codec, null);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        _codecParameters = avcodec_parameters_alloc();
        if (_codecParameters is null)
            throw new OutOfMemoryException("avcodec_parameters_alloc returned NULL");
        ret = avcodec_parameters_from_context(_codecParameters, _codec);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_from_context));

        _frameSamples = _codec->frame_size > 0 ? _codec->frame_size : 1024;
        _frame = av_frame_alloc();
        if (_frame is null)
            throw new OutOfMemoryException("av_frame_alloc returned NULL");
        _frame->format = (int)_codec->sample_fmt;
        av_channel_layout_copy(&_frame->ch_layout, &_codec->ch_layout);
        _frame->sample_rate = _codec->sample_rate;
        _frame->nb_samples = _frameSamples;
        ret = av_frame_get_buffer(_frame, 0);
        FFmpegException.ThrowIfError(ret, nameof(av_frame_get_buffer));

        _packet = av_packet_alloc();
        if (_packet is null)
            throw new OutOfMemoryException("av_packet_alloc returned NULL");

        // Input side is what Submit receives: interleaved float at the INPUT rate/channel count.
        AVChannelLayout inLayout;
        av_channel_layout_default(&inLayout, _inputFormat.Channels);
        SwrContext* swr = null;
        ret = swr_alloc_set_opts2(&swr,
            &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate,
            &inLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _inputFormat.SampleRate,
            0, null);
        FFmpegException.ThrowIfError(ret, nameof(swr_alloc_set_opts2));
        _swr = swr;
        ret = swr_init(_swr);
        FFmpegException.ThrowIfError(ret, nameof(swr_init));

        _fifo = av_audio_fifo_alloc(_codec->sample_fmt, outChannels, _frameSamples * 4);
        if (_fifo is null)
            throw new OutOfMemoryException("av_audio_fifo_alloc returned NULL");
    }

    private static AVSampleFormat PickSampleFormat(AVCodec* codec)
    {
#pragma warning disable CS0618 // sample_fmts: avcodec_get_supported_config needs an open context we don't have yet
        if (codec->sample_fmts is null)
            return AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        for (var p = codec->sample_fmts; *p != AVSampleFormat.AV_SAMPLE_FMT_NONE; p++)
        {
            if (*p == AVSampleFormat.AV_SAMPLE_FMT_FLTP)
                return AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        }

        return codec->sample_fmts[0];
#pragma warning restore CS0618
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_codec is not null)
        {
            var c = _codec;
            avcodec_free_context(&c);
            _codec = null;
        }

        if (_codecParameters is not null)
        {
            var p = _codecParameters;
            avcodec_parameters_free(&p);
            _codecParameters = null;
        }

        if (_frame is not null)
        {
            var f = _frame;
            av_frame_free(&f);
            _frame = null;
        }

        if (_packet is not null)
        {
            var p = _packet;
            av_packet_free(&p);
            _packet = null;
        }

        if (_swr is not null)
        {
            var s = _swr;
            swr_free(&s);
            _swr = null;
        }

        if (_fifo is not null)
        {
            av_audio_fifo_free(_fifo);
            _fifo = null;
        }

        FreeConvertBuffer();
    }
}
