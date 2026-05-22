using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.NDI.Audio;
using S.Media.NDI.Clock;
using S.Media.NDI.Video;

namespace S.Media.NDI;

/// <summary>
/// Single NDI receiver that captures audio and video on one SDK receive loop and exposes lightweight
/// <see cref="IAudioSource"/> / <see cref="IVideoSource"/> adapters for the playback graph.
/// </summary>
/// <remarks>
/// The standalone <see cref="NDIAudioReceiver"/> and <see cref="NDIVideoReceiver"/> remain useful for
/// tools and focused tests. Use this type for normal live NDI playback so one network receiver owns
/// both streams, one capture thread drains the SDK queue, and audio/video rebase together at play time.
/// </remarks>
public sealed unsafe class NDILiveReceiver : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.NDILiveReceiver");

    private const int DefaultVideoQueueDepth = 8;
    private const int MinAudioCapacityFrames = 1024;

    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIIngestPlaybackClock? _ingestClock;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _receiveAudio;
    private readonly bool _receiveVideo;
    private readonly TimeSpan _audioCapacityDuration;
    private readonly TimeSpan _audioMinBufferedDuration;

    private readonly ConcurrentQueue<VideoFrame> _videoQueue = new();
    private readonly object _videoWaitGate = new();
    private readonly object _videoPtsLock = new();
    private readonly int _maxQueuedVideoFrames;

    private AudioFormatSnapshot? _audioState;
    private VideoFormat _videoFormat;
    private PixelFormat[] _videoNative = [];
    private TimeSpan _videoPtsStep = TimeSpan.FromMilliseconds(33);
    private TimeSpan _nextVideoPts;
    private bool _hasVideoFormat;
    private bool _disposed;
    private long _audioOverflowFloats;
    private long _audioConversionDrops;
    private long _videoOverflowFrames;
    private long _videoUnpackDrops;
    private long _videoFramesUnpacked;

    public NDILiveReceiver(
        NDIDiscoveredSource source,
        bool receiveAudio = true,
        bool receiveVideo = true,
        string? receiverName = null,
        TimeSpan audioRingCapacityDuration = default,
        TimeSpan? audioMinBufferedDuration = null,
        int maxQueuedVideoFrames = DefaultVideoQueueDepth,
        NDIRecvBandwidth bandwidth = NDIRecvBandwidth.Highest,
        NDIRecvColorFormat colorFormat = NDIRecvColorFormat.BgrxBgra,
        NDIIngestPlaybackClock? ingestClock = null)
    {
        if (!receiveAudio && !receiveVideo)
            throw new ArgumentException("At least one of audio or video must be enabled.");
        if (maxQueuedVideoFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(maxQueuedVideoFrames));
        if (audioRingCapacityDuration == default)
            audioRingCapacityDuration = TimeSpan.FromSeconds(2);
        if (audioRingCapacityDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(audioRingCapacityDuration), "must be > 0");

        var resolvedMinBuffered = audioMinBufferedDuration ?? NDIAudioReceiver.DefaultMinBufferedDuration;
        if (resolvedMinBuffered < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(audioMinBufferedDuration), "must be >= 0");

        _receiveAudio = receiveAudio;
        _receiveVideo = receiveVideo;
        _audioCapacityDuration = audioRingCapacityDuration;
        _audioMinBufferedDuration = resolvedMinBuffered;
        _maxQueuedVideoFrames = maxQueuedVideoFrames;
        _ingestClock = ingestClock;
        _ingestClock?.AttachReceiver();

        if (receiveAudio)
            AudioSource = new AudioSourceAdapter(this);
        if (receiveVideo)
            VideoSource = new VideoSourceAdapter(this);

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null)
            throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            var settings = new NDIReceiverSettings
            {
                ReceiverName = receiverName,
                ColorFormat = colorFormat,
                Bandwidth = bandwidth,
            };
            rc = NDIReceiver.Create(out var recv, settings);
            if (rc != 0 || recv is null)
                throw new NDIException(rc, "NDIReceiver.Create");
            _receiver = recv;
            _receiver.Connect(source);
            if (Trace.IsEnabled(LogLevel.Information))
            {
                Trace.LogInformation(
                    "NDILiveReceiver: connected source='{Source}' colorFormat={ColorFormat} bandwidth={Bandwidth}",
                    source.Name, colorFormat, bandwidth);
            }
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "NDILiveReceiver: NDIReceiver.Create/Connect");
#else
            _ = ex;
#endif
            _runtime.Dispose();
            throw;
        }

        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "NDILiveReceiver",
        };
        _captureThread.Start();
    }

    public IAudioSource? AudioSource { get; }

    public IVideoSource? VideoSource { get; }

    public bool IsAudioConnected => Volatile.Read(ref _audioState) is not null;

    public bool IsVideoConnected => _hasVideoFormat;

    public bool IsDisposed => _disposed;

    public AudioFormat AudioFormat =>
        Volatile.Read(ref _audioState)?.Format
        ?? throw new InvalidOperationException("NDI source has not delivered an audio frame yet.");

    public VideoFormat VideoFormat =>
        _hasVideoFormat
            ? _videoFormat
            : throw new InvalidOperationException("NDI source has not delivered a video frame yet.");

    public IReadOnlyList<PixelFormat> NativeVideoPixelFormats => _videoNative;

    public long AudioOverflowFloats => Volatile.Read(ref _audioOverflowFloats);

    public long VideoOverflowFrames => Interlocked.Read(ref _videoOverflowFrames);

    public long VideoUnpackDrops => Interlocked.Read(ref _videoUnpackDrops);

    public long VideoFramesUnpacked => Interlocked.Read(ref _videoFramesUnpacked);

    public void RebaseToLatest(TimeSpan videoNextPresentationTime = default, TimeSpan audioKeepBuffered = default)
    {
        if (_receiveAudio)
            RebaseAudioToLatest(audioKeepBuffered);
        if (_receiveVideo)
            RebaseVideoToLatest(videoNextPresentationTime);
    }

    public void RebaseAudioToLatest(TimeSpan keepBuffered = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snap = Volatile.Read(ref _audioState);
        if (snap is null) return;

        var keep = keepBuffered <= TimeSpan.Zero
            ? TimeSpan.FromTicks(NDIAudioReceiver.DefaultMinBufferedDuration.Ticks * 2)
            : (keepBuffered < NDIAudioReceiver.DefaultMinBufferedDuration
                ? NDIAudioReceiver.DefaultMinBufferedDuration
                : keepBuffered);
        var keepFrames = Math.Max(0, (int)(keep.TotalSeconds * snap.Format.SampleRate));
        var keepFloats = checked(keepFrames * snap.Channels);

        var write = Volatile.Read(ref snap.WriteIndex);
        var read = Volatile.Read(ref snap.ReadIndex);
        var buffered = (int)(write - read);
        if (buffered <= keepFloats) return;

        Volatile.Write(ref snap.ReadIndex, read + (buffered - keepFloats));
        // Keep Primed when enough samples remain so Play/Prefill can read immediately — clearing Primed
        // forces another 50 ms holdback and causes PortAudio underruns on the first chunk.
        Volatile.Write(ref snap.Primed, keepFloats >= snap.MinBufferedFloats ? 1 : 0);
    }

    public void RebaseVideoToLatest(TimeSpan nextPresentationTime = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (nextPresentationTime < TimeSpan.Zero)
            nextPresentationTime = TimeSpan.Zero;
        lock (_videoPtsLock)
        {
            while (_videoQueue.TryDequeue(out var frame))
                frame.Dispose();
            _nextVideoPts = nextPresentationTime;
        }
    }

    private int ReadAudioInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snap = Volatile.Read(ref _audioState);
        if (snap is null) return 0;

        var channels = snap.Channels;
        if (dst.Length % channels != 0)
            throw new ArgumentException(
                $"dst length {dst.Length} is not a multiple of channel count {channels}", nameof(dst));

        var read = Volatile.Read(ref snap.ReadIndex);
        var write = Volatile.Read(ref snap.WriteIndex);
        var available = (int)(write - read);
        var primed = Volatile.Read(ref snap.Primed) != 0;
        var toRead = NDIAudioReceiver.ComputeReadCount(dst.Length, available, snap.MinBufferedFloats, ref primed);
        Volatile.Write(ref snap.Primed, primed ? 1 : 0);
        if (toRead == 0) return 0;

        var startIdx = (int)(read & snap.RingMask);
        var firstChunk = Math.Min(toRead, snap.RingBuffer.Length - startIdx);
        snap.RingBuffer.AsSpan(startIdx, firstChunk).CopyTo(dst);
        if (firstChunk < toRead)
            snap.RingBuffer.AsSpan(0, toRead - firstChunk).CopyTo(dst[firstChunk..]);
        Volatile.Write(ref snap.ReadIndex, read + toRead);
        return toRead;
    }

    private bool TryReadNextVideoFrame(out VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        VideoFrame? next;
        while (!_videoQueue.TryDequeue(out next))
        {
            if (_disposed)
            {
                frame = null!;
                return false;
            }

            lock (_videoWaitGate)
            {
                if (_videoQueue.IsEmpty && !_disposed)
                    Monitor.Wait(_videoWaitGate, 100);
            }
        }

        frame = next;
        return true;
    }

    private void SelectVideoOutputFormat(PixelFormat format)
    {
        if (!_hasVideoFormat)
            throw new InvalidOperationException("Format is not known until the first video frame arrives.");
        if (format != _videoFormat.PixelFormat)
            throw new InvalidOperationException(
                $"NDILiveReceiver delivers {_videoFormat.PixelFormat} only; sink requested {format}.");
    }

    private void CaptureLoop(CancellationToken token)
    {
        var interleaved = new NDIAudioInterleaved32f();
        var heldAudioBuffer = Array.Empty<float>();
        GCHandle audioPin = default;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var frameType = _receiver.Capture(out var video, out var audio, out var metadata, timeoutMs: 100);
                switch (frameType)
                {
                    case NDIFrameType.Audio:
                    {
                        try
                        {
                            if (_receiveAudio)
                                ProcessAudioFrame(in audio, ref interleaved, ref heldAudioBuffer, ref audioPin);
                        }
                        finally
                        {
                            _receiver.FreeAudio(audio);
                        }

                        break;
                    }
                    case NDIFrameType.Video:
                    {
                        try
                        {
                            if (_receiveVideo)
                                ProcessVideoFrame(in video);
                        }
                        finally
                        {
                            _receiver.FreeVideo(video);
                        }

                        break;
                    }
                    case NDIFrameType.Metadata:
                        _receiver.FreeMetadata(metadata);
                        break;
                }
            }
        }
        finally
        {
            if (audioPin.IsAllocated) audioPin.Free();
        }
    }

    private void ProcessAudioFrame(
        in NDIAudioFrameV3 audio,
        ref NDIAudioInterleaved32f interleaved,
        ref float[] heldBuffer,
        ref GCHandle pin)
    {
        var samples = audio.NoSamples;
        var channels = audio.NoChannels;
        var sampleRate = audio.SampleRate;
        long totalFloatsLong = (long)samples * channels;
        if (totalFloatsLong <= 0 || totalFloatsLong > Array.MaxLength)
            return;

        var totalFloats = (int)totalFloatsLong;
        var snap = EnsureAudioFormat(sampleRate, channels);

        if (heldBuffer.Length < totalFloats)
        {
            if (pin.IsAllocated) pin.Free();
            heldBuffer = new float[totalFloats];
            pin = GCHandle.Alloc(heldBuffer, GCHandleType.Pinned);
        }

        interleaved = new NDIAudioInterleaved32f
        {
            SampleRate = sampleRate,
            NoChannels = channels,
            NoSamples = samples,
            PData = pin.AddrOfPinnedObject(),
        };
        if (!NDIAudioUtils.ToInterleaved32f(audio, ref interleaved))
        {
            LogAudioConversionDrop(in audio);
            return;
        }

        _ingestClock?.NotifyAudioFrame(in audio);
        EnqueueAudioSamples(snap, heldBuffer.AsSpan(0, totalFloats));
    }

    private void ProcessVideoFrame(in NDIVideoFrameV2 video)
    {
        lock (_videoPtsLock)
        {
            if (NDIVideoFrameUnpack.TryUnpack(video, _nextVideoPts, out var vf) && vf is not null)
            {
                EnsureVideoFormat(vf.Format);
                EnqueueVideoFrame(vf);
                _nextVideoPts += _videoPtsStep;
                var unpacked = Interlocked.Increment(ref _videoFramesUnpacked);
                if (unpacked <= 3 && Trace.IsEnabled(LogLevel.Information))
                {
                    var avgLuma = NDIVideoFrameUnpack.SampleAveragePackedLuma(vf);
                    Trace.LogInformation(
                        "NDILiveReceiver: unpacked video frame #{N} fourCC={FourCc} native={NativeW}x{NativeH} stride={NativeStride} → {FmtW}x{FmtH} {FmtPf} avgLuma={AvgLuma:F1} range={Range} pts={Pts}",
                        unpacked, video.FourCC, video.Xres, video.Yres, video.LineStrideInBytes,
                        vf.Format.Width, vf.Format.Height, vf.Format.PixelFormat, avgLuma, vf.ColorRange,
                        vf.PresentationTime);
                }
            }
            else
            {
                var drops = Interlocked.Increment(ref _videoUnpackDrops);
                if (drops <= 8)
                {
                    Trace.LogWarning(
                        "NDILiveReceiver: dropped video frame (unpack failed) #{Drop} fourCC={FourCc} xres={Xres} yres={Yres} lineStride={Stride} pData={HasData}",
                        drops, video.FourCC, video.Xres, video.Yres, video.LineStrideInBytes, video.PData != nint.Zero);
                }
            }
        }
    }

    private AudioFormatSnapshot EnsureAudioFormat(int sampleRate, int channels)
    {
        var existing = Volatile.Read(ref _audioState);
        if (existing is not null
            && existing.Format.SampleRate == sampleRate
            && existing.Format.Channels == channels)
            return existing;

        var capacityFrames = Math.Max(MinAudioCapacityFrames, (int)(_audioCapacityDuration.TotalSeconds * sampleRate));
        var minBufferedFrames = NDIAudioReceiver.ComputeMinBufferedFrames(
            _audioMinBufferedDuration, sampleRate, capacityFrames);
        var snap = new AudioFormatSnapshot(new AudioFormat(sampleRate, channels), capacityFrames, minBufferedFrames);
        Volatile.Write(ref _audioState, snap);
        return snap;
    }

    private void EnqueueAudioSamples(AudioFormatSnapshot snap, ReadOnlySpan<float> src)
    {
        var capacity = snap.UsableFloats;
        if (capacity <= 0) return;

        var dropped = 0;
        if (src.Length > capacity)
        {
            dropped += src.Length - capacity;
            src = src[^capacity..];
        }

        var write = Volatile.Read(ref snap.WriteIndex);
        var read = Volatile.Read(ref snap.ReadIndex);
        var buffered = (int)(write - read);
        var free = capacity - buffered;
        if (src.Length > free)
        {
            var dropOld = src.Length - free;
            read += dropOld;
            dropped += dropOld;
            Volatile.Write(ref snap.ReadIndex, read);
            Volatile.Write(ref snap.Primed, 0);
        }

        var startIdx = (int)(write & snap.RingMask);
        var firstChunk = Math.Min(src.Length, snap.RingBuffer.Length - startIdx);
        src[..firstChunk].CopyTo(snap.RingBuffer.AsSpan(startIdx));
        if (firstChunk < src.Length)
            src[firstChunk..].CopyTo(snap.RingBuffer.AsSpan(0));
        Volatile.Write(ref snap.WriteIndex, write + src.Length);

        if (dropped > 0)
            Interlocked.Add(ref _audioOverflowFloats, dropped);
    }

    private void LogAudioConversionDrop(in NDIAudioFrameV3 audio)
    {
        var drops = Interlocked.Increment(ref _audioConversionDrops);
        if (drops <= 5)
        {
            MediaDiagnostics.LogWarning(
                "NDILiveReceiver: dropped audio frame (interleaved conversion failed) fourCC={FourCc} channels={Channels} samples={Samples} sampleRate={SampleRate}",
                audio.FourCC, audio.NoChannels, audio.NoSamples, audio.SampleRate);
        }
    }

    private void EnsureVideoFormat(VideoFormat format)
    {
        if (_hasVideoFormat && _videoFormat.PixelFormat == format.PixelFormat
                            && _videoFormat.Width == format.Width && _videoFormat.Height == format.Height)
            return;

        _videoFormat = format;
        _videoNative = [format.PixelFormat];
        _videoPtsStep = format.FrameRate.Denominator > 0 && format.FrameRate.ToDouble() > 0
            ? TimeSpan.FromSeconds(1.0 / format.FrameRate.ToDouble())
            : TimeSpan.FromMilliseconds(33);
        _hasVideoFormat = true;
    }

    private void EnqueueVideoFrame(VideoFrame frame)
    {
        while (_videoQueue.Count >= _maxQueuedVideoFrames && _videoQueue.TryDequeue(out var old))
        {
            old.Dispose();
            Interlocked.Increment(ref _videoOverflowFrames);
        }

        _videoQueue.Enqueue(frame);
        lock (_videoWaitGate)
            Monitor.PulseAll(_videoWaitGate);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* best effort */ }
        try { CooperativePlaybackJoin.JoinThread(_captureThread, TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        try { _ingestClock?.NotifyCaptureStopped(); } catch { /* best effort */ }
        try { _cts.Dispose(); } catch { /* best effort */ }

        while (_videoQueue.TryDequeue(out var f))
            f.Dispose();

        lock (_videoWaitGate)
            Monitor.PulseAll(_videoWaitGate);

        try { _receiver.Dispose(); } catch { /* best effort */ }
        try { _runtime.Dispose(); } catch { /* best effort */ }
    }

    private sealed class AudioSourceAdapter(NDILiveReceiver owner) : IAudioSource, IDisposable
    {
        public AudioFormat Format => owner.AudioFormat;
        public bool IsExhausted => owner.IsDisposed;
        public int ReadInto(Span<float> destination) => owner.ReadAudioInto(destination);
        public void Dispose() => owner.Dispose();
    }

    private sealed class VideoSourceAdapter(NDILiveReceiver owner) : IVideoSource, IDisposable
    {
        public VideoFormat Format => owner.VideoFormat;
        public IReadOnlyList<PixelFormat> NativePixelFormats => owner.NativeVideoPixelFormats;
        public bool IsExhausted => owner.IsDisposed;
        public void SelectOutputFormat(PixelFormat format) => owner.SelectVideoOutputFormat(format);
        public bool TryReadNextFrame(out VideoFrame frame) => owner.TryReadNextVideoFrame(out frame);
        public void Dispose() => owner.Dispose();
    }

    private sealed class AudioFormatSnapshot
    {
        public readonly AudioFormat Format;
        public readonly int Channels;
        public readonly float[] RingBuffer;
        public readonly int RingMask;
        public readonly int UsableFloats;
        public readonly int MinBufferedFloats;
        public int Primed;
        public long WriteIndex;
        public long ReadIndex;

        public AudioFormatSnapshot(AudioFormat format, int capacityFrames, int minBufferedFrames)
        {
            Format = format;
            Channels = format.Channels;
            var capFloats = capacityFrames * format.Channels;
            var rounded = 1;
            while (rounded < capFloats) rounded <<= 1;
            RingBuffer = new float[rounded];
            RingMask = rounded - 1;
            UsableFloats = rounded - (rounded % Channels);
            MinBufferedFloats = checked(minBufferedFrames * format.Channels);
        }
    }
}
