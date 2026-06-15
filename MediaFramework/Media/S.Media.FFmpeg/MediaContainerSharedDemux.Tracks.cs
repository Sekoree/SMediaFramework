using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Internal;
using S.Media.FFmpeg.Video;
using S.Media.FFmpeg.Video.Internal;

namespace S.Media.FFmpeg;

internal sealed unsafe partial class MediaContainerSharedDemux
{
    internal sealed class AudioTrack : IAudioSource, ISeekableSource, ICooperativeAudioReadInterrupt, IDisposable
    {
        private readonly MediaContainerSharedDemux _o;

        internal AudioTrack(MediaContainerSharedDemux owner) => _o = owner;

        public AudioFormat Format { get; internal set; } = default!;
        public string CodecName => _o.AudioCodecName;
        public TimeSpan Duration => _o.Duration;
        public TimeSpan Position { get; internal set; }
        public bool IsAtEnd => !_o._hasAudio || _o._aEof;
        public bool IsExhausted => !_o._hasAudio || (_o._aEof && _o._aDrainedTail);

        public void RequestYieldBetweenReads() => _o.RequestReadYield();

        public void ClearYieldRequest() => _o.ClearReadYield();

        public int ReadInto(Span<float> dst)
        {
            _o._readSeekGate.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_o._disposed, this);
                if (!_o._hasAudio) return 0;
                if (dst.Length % Format.Channels != 0)
                    throw new ArgumentException(
                        $"destination length {dst.Length} is not a multiple of channel count {Format.Channels}", nameof(dst));

                lock (_o._audioDecodeLock)
                {
                    _o.ThrowIfDisposedUnsafe();
                    // First read past a seek: the primed state is now being consumed, so a later reseek to
                    // the same position must run in full rather than dedup.
                    _o._seekPrimePending = false;
                    if (IsExhausted) return 0;

                    var written = 0;
                    while (written < dst.Length)
                    {
                        if (_o.IsReadYieldRequested) break;

                        var remainingFrames = (dst.Length - written) / Format.Channels;
                        if (remainingFrames == 0) break;

                        var drained = _o.SwrConvertInto(dst[written..], remainingFrames, null, 0);
                        if (drained > 0)
                        {
                            written += drained * Format.Channels;
                            _o._aSamplesEmitted += drained;
                        }
                        if (drained == remainingFrames) continue;

                        if (_o._aEof)
                        {
                            if (drained == 0)
                            {
                                _o._aDrainedTail = true;
                                break;
                            }
                            continue;
                        }

                        if (!_o.TryReceiveAudioFrame())
                        {
                            if (_o.IsReadYieldRequested) break;
                            continue;
                        }

                        _o.EnsureSwrMatchesDecodedAudioFrameLocked();
                        var capacity = (dst.Length - written) / Format.Channels;
                        var produced = _o.SwrConvertInto(dst[written..], capacity, _o._aFrame->extended_data, _o._aFrame->nb_samples);
                        if (produced > 0)
                        {
                            written += produced * Format.Channels;
                            _o._aSamplesEmitted += produced;
                        }
                        av_frame_unref(_o._aFrame);
                    }

                    if (written > 0)
                        Position = TimeSpan.FromSeconds((double)_o._aSamplesEmitted / Format.SampleRate);
                    return written;
                }
            }
            finally
            {
                _o._readSeekGate.ExitReadLock();
            }
        }

        public bool TryReadNextFrame(out AudioFrame frame)
        {
            _o._readSeekGate.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_o._disposed, this);
                if (!_o._hasAudio)
                {
                    frame = default;
                    return false;
                }
                lock (_o._audioDecodeLock)
                {
                    _o.ThrowIfDisposedUnsafe();
                    _o._seekPrimePending = false;
                    if (_o.IsReadYieldRequested)
                    {
                        frame = default;
                        return false;
                    }
                    if (!_o.TryReceiveAudioFrame())
                    {
                        if (_o.IsReadYieldRequested)
                        {
                            frame = default;
                            return false;
                        }
                        return _o.TryDrainAudioTailFrame(out frame);
                    }
                    frame = _o.ConvertAudioFrame();
                    av_frame_unref(_o._aFrame);
                    return true;
                }
            }
            finally
            {
                _o._readSeekGate.ExitReadLock();
            }
        }

        public void Seek(TimeSpan position) => _o.SeekPresentation(position);

        public void Dispose() { }
    }

    internal sealed class VideoTrack : IVideoSource, ISeekableSource, IHardwareD3D11GlInteropSource,
        ICooperativeVideoReadInterrupt, IDisposable
    {
        private readonly MediaContainerSharedDemux _o;

        internal VideoTrack(MediaContainerSharedDemux owner) => _o = owner;

        void ICooperativeVideoReadInterrupt.RequestYieldBetweenReads() => _o.RequestVideoDecodeYield();

        void ICooperativeVideoReadInterrupt.ClearYieldRequest() => _o.ClearVideoDecodeYield();

        public VideoFormat Format { get; internal set; } = default!;
        public string CodecName => _o.VideoCodecName;
        public TimeSpan Duration => _o.Duration;
        public TimeSpan Position { get; internal set; }
        // Attached-picture cover: exhausted once its single frame has been emitted (it cannot be re-decoded
        // after a seek, and is held downstream), so the decode loop stops instead of blocking forever.
        public bool IsAtEnd => !_o._hasVideo || _o._vEof || (_o._videoIsAttachedPicture && _o._vAttachedPicEmitted);
        public bool IsExhausted => !_o._hasVideo || _o._vEof || (_o._videoIsAttachedPicture && _o._vAttachedPicEmitted);
        public IReadOnlyList<PixelFormat> NativePixelFormats => _o._vNativePixFormats;

        public void SelectOutputFormat(PixelFormat format)
        {
            _o._readSeekGate.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_o._disposed, this);
                if (format == PixelFormat.Unknown)
                    throw new ArgumentException("cannot select Unknown pixel format", nameof(format));

                if (!_o._hasVideo)
                {
                    // No real video stream — just record the negotiated output format on the stub so the
                    // output's Configure(Format) call sees a coherent VideoFormat. The decode path is inert.
                    Format = Format with { PixelFormat = format };
                    _o._vOutPixFmt = format;
                    return;
                }

                lock (_o._videoDecodeLock)
                {
                    _o.ThrowIfDisposedUnsafe();
                    _o.SelectVideoOutputFormatLocked(format);
                }
            }
            finally
            {
                _o._readSeekGate.ExitReadLock();
            }
        }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            _o._readSeekGate.EnterReadLock();
            try
            {
                ObjectDisposedException.ThrowIf(_o._disposed, this);
                if (!_o._hasVideo)
                {
                    frame = null!;
                    return false;
                }
                lock (_o._videoDecodeLock)
                {
                    _o.ThrowIfDisposedUnsafe();
                    _o._seekPrimePending = false;

                    if (Volatile.Read(ref _o._videoDecodeYieldRequested) != 0 || _o.IsReadYieldRequested)
                    {
                        frame = null!;
                        return false;
                    }

                    if (_o._vPrimedAfterSeek is { } primed)
                    {
                        frame = primed;
                        _o._vPrimedAfterSeek = null;
                        return true;
                    }

                    while (true)
                    {
                        if (Volatile.Read(ref _o._videoDecodeYieldRequested) != 0 || _o.IsReadYieldRequested)
                        {
                            frame = null!;
                            return false;
                        }

                        var ret = avcodec_receive_frame(_o._vCtx, _o._vFrame);
                        if (ret == 0)
                        {
                            var workFrame = _o.ResolveWorkVideoFrame();
                            _o.SyncVideoPixelFormatIfNeeded(workFrame);
                            var meta = _o.ExtractVideoMetadata(_o._vFrame);
                            frame = _o.BuildVideoFrame(workFrame, meta);
                            av_frame_unref(_o._vFrame);
                            return true;
                        }
                        if (ret == AVERROR_EOF)
                        {
                            _o._vEof = true;
                            frame = null!;
                            return false;
                        }
                        if (ret == AVERROR(EAGAIN))
                        {
                            _o.FeedVideoFromQueue();
                            continue;
                        }
                        FFmpegException.ThrowIfError(ret, nameof(avcodec_receive_frame));
                    }
                }
            }
            finally
            {
                _o._readSeekGate.ExitReadLock();
            }
        }

        public void Seek(TimeSpan position) => _o.SeekPresentation(position);

        public void Dispose() { }

        public bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr) =>
            _o.TryGetHardwareD3D11DeviceForWin32Gl(out deviceComPtr);

        public bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked) =>
            _o.TryGetHardwareD3D11AdapterLuid(out adapterLuidPacked);
    }
}
