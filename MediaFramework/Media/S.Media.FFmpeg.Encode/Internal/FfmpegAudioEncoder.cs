using System.Runtime.InteropServices;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Audio;

namespace S.Media.FFmpeg.Encode.Internal;

internal sealed unsafe class FfmpegAudioEncoder : IAudioOutput, IDisposable
{
    private readonly FfmpegMuxContext _mux;
    private readonly FFmpegAudioFileOutputOptions _options;
    private readonly Lock _gate = new();
    private float[] _pending = [];
    private int _pendingStart;
    private int _pendingEnd;
    private AVCodecContext* _codec;
    private AVStream* _stream;
    private AVFrame* _frame;
    private SwrContext* _swr;
    private AudioFormat _format;
    private int _frameSamples;
    private long _ptsSamples;
    private bool _configured;
    private bool _disposed;

    public FfmpegAudioEncoder(FfmpegMuxContext mux, FFmpegAudioFileOutputOptions options)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        FFmpegRuntime.EnsureInitialized();
    }

    public AudioFormat Format => _format;

    public void Configure(AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        format.Validate(nameof(format));
        lock (_gate)
        {
            if (_configured)
                throw new InvalidOperationException("FFmpeg audio encoder already configured.");

            _format = format;
            OpenCodecLocked();
            _configured = true;
            _mux.NotifyAudioConfigured();
        }
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_configured)
            throw new InvalidOperationException("Configure must be called before Submit.");

        lock (_gate)
        {
            AppendPending(packedSamples);

            var floatsPerFrame = checked(_frameSamples * _codec->ch_layout.nb_channels);
            while (_pendingEnd - _pendingStart >= floatsPerFrame)
            {
                WriteFrameLocked(_pending.AsSpan(_pendingStart, floatsPerFrame));
                _pendingStart += floatsPerFrame;
            }

            // Fully drained — reset offsets to the front. Draining a frame is O(1) (advance the read
            // offset), replacing the old List.RemoveRange(0, …) which shifted every frame (O(n)).
            if (_pendingStart == _pendingEnd)
                _pendingStart = _pendingEnd = 0;
        }
    }

    /// <summary>Appends interleaved float samples to the pending buffer, compacting the live range to the
    /// front (or growing) only when the tail runs out of room.</summary>
    private void AppendPending(ReadOnlySpan<float> samples)
    {
        if (_pendingEnd + samples.Length > _pending.Length)
        {
            var count = _pendingEnd - _pendingStart;
            if (count + samples.Length <= _pending.Length)
            {
                Array.Copy(_pending, _pendingStart, _pending, 0, count); // compact
            }
            else
            {
                var grown = new float[Math.Max(_pending.Length * 2, count + samples.Length)];
                Array.Copy(_pending, _pendingStart, grown, 0, count);
                _pending = grown;
            }
            _pendingStart = 0;
            _pendingEnd = count;
        }

        samples.CopyTo(_pending.AsSpan(_pendingEnd));
        _pendingEnd += samples.Length;
    }

    private void WriteFrameLocked(ReadOnlySpan<float> interleaved)
    {
        var ret = av_frame_make_writable(_frame);
        FFmpegException.ThrowIfError(ret, nameof(av_frame_make_writable));

        // swr converts the interleaved float input into the codec's sample format/layout (planar or
        // packed, float or integer). In/out rates are equal → 1:1, filling exactly _frameSamples.
        fixed (float* src = interleaved)
        {
            var inBuf = (byte*)src;
            var converted = swr_convert(_swr, _frame->extended_data, _frameSamples, &inBuf, _frameSamples);
            if (converted < 0)
                FFmpegException.ThrowIfError(converted, nameof(swr_convert));
        }

        _frame->pts = _ptsSamples;
        _ptsSamples += _frameSamples;
        ret = avcodec_send_frame(_codec, _frame);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_send_frame));
        DrainPacketsLocked(sendFlush: false);
    }

    private void DrainPacketsLocked(bool sendFlush)
    {
        if (sendFlush)
        {
            var ret = avcodec_send_frame(_codec, null);
            if (ret < 0 && ret != ffmpeg.AVERROR_EOF)
                FFmpegException.ThrowIfError(ret, nameof(avcodec_send_frame));
        }

        var pkt = av_packet_alloc();
        if (pkt is null)
            throw new OutOfMemoryException("av_packet_alloc returned NULL");
        try
        {
            while (true)
            {
                var ret = avcodec_receive_packet(_codec, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;
                FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_packet));
                av_packet_rescale_ts(pkt, _codec->time_base, _stream->time_base);
                _mux.WritePacket(pkt, _stream->index);
                av_packet_unref(pkt);
            }
        }
        finally
        {
            av_packet_free(&pkt);
        }
    }

    private void OpenCodecLocked()
    {
        var codecId = FfmpegEncodeMaps.AudioCodecId(_options.Codec);
        AVCodec* codec = null;
        var name = FfmpegEncodeMaps.AudioEncoderName(_options.Codec);
        if (name is not null)
            codec = avcodec_find_encoder_by_name(name);
        if (codec is null)
            codec = avcodec_find_encoder(codecId);
        if (codec is null)
            throw new InvalidOperationException($"no FFmpeg encoder for {_options.Codec}");

        _stream = avformat_new_stream(_mux.FormatContext, null);
        if (_stream is null)
            throw new OutOfMemoryException("avformat_new_stream returned NULL");

        _codec = avcodec_alloc_context3(codec);
        if (_codec is null)
            throw new OutOfMemoryException("avcodec_alloc_context3 returned NULL");

        av_channel_layout_default(&_codec->ch_layout, _format.Channels);
        _codec->sample_rate = _format.SampleRate;
        _codec->sample_fmt = PickSampleFormat(codec);
        _codec->time_base = new AVRational { num = 1, den = _format.SampleRate };
        if (_options.Bitrate > 0)
            _codec->bit_rate = _options.Bitrate;

        if ((_mux.FormatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        var ret = avcodec_open2(_codec, codec, null);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_open2));

        ret = avcodec_parameters_from_context(_stream->codecpar, _codec);
        FFmpegException.ThrowIfError(ret, nameof(avcodec_parameters_from_context));
        _stream->time_base = _codec->time_base;

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

        // Convert the interleaved float input (what Submit receives) into the codec's required sample
        // format/layout. This makes non-float codecs correct (FLAC = s16/s32, libopus may pick s16)
        // instead of copying raw float bytes into a frame the codec interprets as integers.
        SwrContext* swr = null;
        ret = swr_alloc_set_opts2(&swr,
            &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate,
            &_codec->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _codec->sample_rate,
            0, null);
        FFmpegException.ThrowIfError(ret, nameof(swr_alloc_set_opts2));
        _swr = swr;
        ret = swr_init(_swr);
        FFmpegException.ThrowIfError(ret, nameof(swr_init));
    }

    private static AVSampleFormat PickSampleFormat(AVCodec* codec)
    {
        if (codec->sample_fmts is null)
            return AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        for (var p = codec->sample_fmts; *p != AVSampleFormat.AV_SAMPLE_FMT_NONE; p++)
        {
            if (*p == AVSampleFormat.AV_SAMPLE_FMT_FLTP)
                return AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        }

        return codec->sample_fmts[0];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            if (_codec is not null)
            {
                var remaining = _pendingEnd - _pendingStart;
                if (remaining > 0)
                {
                    // Pad the final partial frame with silence (one-time alloc on dispose).
                    var pad = checked(_frameSamples * _codec->ch_layout.nb_channels);
                    var finalFrame = new float[pad];
                    _pending.AsSpan(_pendingStart, remaining).CopyTo(finalFrame);
                    WriteFrameLocked(finalFrame);
                    _pendingStart = _pendingEnd = 0;
                }

                MediaDiagnostics.SwallowDisposeErrors(() => DrainPacketsLocked(sendFlush: true),
                    "FfmpegAudioEncoder.Dispose: flush");
                var c = _codec;
                avcodec_free_context(&c);
                _codec = null;
            }

            if (_frame is not null)
            {
                var f = _frame;
                av_frame_free(&f);
                _frame = null;
            }

            if (_swr is not null)
            {
                var s = _swr;
                swr_free(&s);
                _swr = null;
            }
        }
    }
}
