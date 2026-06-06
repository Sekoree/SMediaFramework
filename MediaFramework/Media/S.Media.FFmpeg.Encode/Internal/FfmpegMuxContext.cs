using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;

namespace S.Media.FFmpeg.Encode.Internal;

/// <summary>Shared libavformat output context for one output file.</summary>
internal sealed unsafe class FfmpegMuxContext : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly bool _expectVideo;
    private readonly bool _expectAudio;
    private AVFormatContext* _fmt;
    private bool _videoConfigured;
    private bool _audioConfigured;
    private bool _headerWritten;
    private bool _disposed;

    public FfmpegMuxContext(string path, FFmpegEncodeContainer container, bool expectVideo, bool expectAudio)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _expectVideo = expectVideo;
        _expectAudio = expectAudio;
        FFmpegRuntime.EnsureInitialized();

        var fmtName = FfmpegEncodeMaps.ContainerShortName(container);
        AVFormatContext* ctx = null;
        var ret = avformat_alloc_output_context2(&ctx, null, fmtName, _path);
        FFmpegException.ThrowIfError(ret, nameof(avformat_alloc_output_context2));
        if (ctx is null)
            throw new FFmpegException(0, "avformat_alloc_output_context2 returned NULL");
        _fmt = ctx;
    }

    public AVFormatContext* FormatContext
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _fmt;
        }
    }

    public void NotifyVideoConfigured() =>
        NotifyLegConfigured(expectVideo: true);

    public void NotifyAudioConfigured() =>
        NotifyLegConfigured(expectVideo: false);

    private void NotifyLegConfigured(bool expectVideo)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (expectVideo)
                _videoConfigured = true;
            else
                _audioConfigured = true;
            TryWriteHeaderLocked();
        }
    }

    private void TryWriteHeaderLocked()
    {
        if (_headerWritten)
            return;
        if (_expectVideo && !_videoConfigured)
            return;
        if (_expectAudio && !_audioConfigured)
            return;

        if ((_fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            var ret = avio_open(&_fmt->pb, _path, ffmpeg.AVIO_FLAG_WRITE);
            FFmpegException.ThrowIfError(ret, nameof(avio_open));
        }

        var hdr = avformat_write_header(_fmt, null);
        FFmpegException.ThrowIfError(hdr, nameof(avformat_write_header));
        _headerWritten = true;
    }

    public void WritePacket(AVPacket* pkt, int streamIndex)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_headerWritten)
                TryWriteHeaderLocked();
            if (!_headerWritten)
            {
                throw new InvalidOperationException(
                    "All expected streams must be configured before submitting packets.");
            }
            pkt->stream_index = streamIndex;
            var ret = av_interleaved_write_frame(_fmt, pkt);
            FFmpegException.ThrowIfError(ret, nameof(av_interleaved_write_frame));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            if (_fmt is null)
                return;

            if (_headerWritten)
            {
                MediaDiagnostics.SwallowDisposeErrors(() =>
                {
                    var ret = av_write_trailer(_fmt);
                    if (ret < 0)
                        FFmpegException.ThrowIfError(ret, nameof(av_write_trailer));
                }, "FfmpegMuxContext.Dispose: av_write_trailer");
            }

            if ((_fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                MediaDiagnostics.SwallowDisposeErrors(() => avio_closep(&_fmt->pb), "FfmpegMuxContext.Dispose: avio_closep");

            var f = _fmt;
            avformat_free_context(f);
            _fmt = null;
        }
    }
}
