using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.Core.Video;
using S.Media.NDI.Audio;
using S.Media.NDI.Clock;
using S.Media.NDI.Video;

namespace S.Media.NDI;

/// <summary>
/// Combined NDI receive source: <see cref="Find"/> on the network, <see cref="Open"/> a source, then wire
/// <see cref="Audio"/> / <see cref="Video"/> into <see cref="S.Media.Playback.MediaPlayer.OpenLive"/>.
/// </summary>
public sealed unsafe class NDISource : IDisposable, INdiOverflowReporter
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.NDISource");

    private const int DefaultVideoQueueDepth = 8;
    private const int MinAudioCapacityFrames = 1024;

    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIIngestPlaybackClock _ingestClock;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private bool _receiveAudio;
    private bool _receiveVideo;
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
    private TimeSpan _videoRebaseBasePts;
    private TimeSpan _lastResolvedVideoPts;
    private long _videoNdiTimingOriginTicks;
    private bool _videoNdiTimingOriginSet;
    private bool _hasLastResolvedVideoPts;
    private bool _hasVideoFormat;
    private bool _disposed;
    private int _captureThreadStuck;
    private NDIConnectionState _state = NDIConnectionState.Opening;
    private long _audioOverflowFloats;
    private long _audioConversionDrops;
    private long _videoOverflowFrames;
    private long _videoUnpackDrops;
    private long _videoFramesUnpacked;
    private volatile Exception? _faultEx;

    /// <summary>Discovers NDI sources visible on the network.</summary>
    public static IReadOnlyList<NDIDiscoveredSource> Find(TimeSpan timeout, NDIFindOptions? options = null)
    {
        options ??= NDIFindOptions.Default;
        var rc = NDIFinder.Create(out var finder, new NDIFinderSettings
        {
            ShowLocalSources = options.ShowLocalSources,
            Groups = options.Groups,
            ExtraIps = options.ExtraIps,
        });
        if (rc != 0 || finder is null)
            return Array.Empty<NDIDiscoveredSource>();
        try
        {
            var waitMs = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
            if (waitMs > 0)
                finder.WaitForSources((uint)waitMs);
            return finder.GetCurrentSources();
        }
        finally
        {
            finder.Dispose();
        }
    }

    /// <summary>Opens a combined audio/video receiver for one discovered source.</summary>
    public static NDISource Open(NDIDiscoveredSource source, NDISourceOptions? options = null) =>
        new(source, options ?? NDISourceOptions.Default);

    public NDISource(NDIDiscoveredSource source, NDISourceOptions? options = null)
        : this(source, options ?? NDISourceOptions.Default, legacy: false)
    {
    }

    private NDISource(NDIDiscoveredSource source, NDISourceOptions options, bool legacy)
    {
        _ = legacy;
        if (!options.ReceiveAudio && !options.ReceiveVideo)
            throw new ArgumentException("At least one of audio or video must be enabled.");
        if (options.MaxQueuedVideoFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxQueuedVideoFrames must be >= 1.");

        var audioRingCapacityDuration = options.AudioRingCapacityDuration == default
            ? TimeSpan.FromSeconds(2)
            : options.AudioRingCapacityDuration;
        if (audioRingCapacityDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "AudioRingCapacityDuration must be > 0.");

        var resolvedMinBuffered = options.AudioMinBufferedDuration ?? NDIAudioReceiver.DefaultMinBufferedDuration;
        if (resolvedMinBuffered < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "AudioMinBufferedDuration must be >= 0.");

        _receiveAudio = options.ReceiveAudio;
        _receiveVideo = options.ReceiveVideo;
        _audioCapacityDuration = audioRingCapacityDuration;
        _audioMinBufferedDuration = resolvedMinBuffered;
        _maxQueuedVideoFrames = options.MaxQueuedVideoFrames;
        _ingestClock = options.IngestClock ?? new NDIIngestPlaybackClock();
        _ingestClock.AttachReceiver();

        Audio = new AudioSourceAdapter(this);
        Video = new VideoSourceAdapter(this);

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null)
            throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            var settings = new NDIReceiverSettings
            {
                ReceiverName = options.ReceiverName,
                ColorFormat = options.ColorFormat,
                Bandwidth = options.Bandwidth,
            };
            rc = NDIReceiver.Create(out var recv, settings);
            if (rc != 0 || recv is null)
                throw new NDIException(rc, "NDIReceiver.Create");
            _receiver = recv;
            _receiver.Connect(source);
            _state = NDIConnectionState.Connected;
            if (Trace.IsEnabled(LogLevel.Information))
            {
                Trace.LogInformation(
                    "NDISource: connected source='{Source}' colorFormat={ColorFormat} bandwidth={Bandwidth}",
                    source.Name, options.ColorFormat, options.Bandwidth);
            }
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "NDISource: NDIReceiver.Create/Connect");
            _state = NDIConnectionState.Disconnected;
#else
            _ = ex;
#endif
            _runtime.Dispose();
            throw;
        }

        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "NDISource",
        };
        _captureThread.Start();
    }

    public IAudioSource Audio { get; }

    public IVideoSource Video { get; }

    public IPlaybackClock IngestClock => _ingestClock;

    public bool ReceiveAudio
    {
        get => _receiveAudio;
        set => _receiveAudio = value;
    }

    public bool ReceiveVideo
    {
        get => _receiveVideo;
        set => _receiveVideo = value;
    }

    public NDIConnectionState State
    {
        get
        {
            if (IsCaptureStuck)
                return NDIConnectionState.Stuck;
            if (_disposed)
                return NDIConnectionState.Disposed;
            if (_state == NDIConnectionState.Opening && (IsAudioConnected || IsVideoConnected))
                return NDIConnectionState.Connected;
            return _state;
        }
    }

    public bool IsAudioConnected => Volatile.Read(ref _audioState) is not null;

    public bool IsVideoConnected => _hasVideoFormat;

    public bool IsDisposed => _disposed;

    /// <summary>
    /// True after <see cref="Dispose"/> timed out while the capture thread was still alive.
    /// Native receiver/runtime state is intentionally retained in this state.
    /// </summary>
    public bool IsCaptureStuck => Volatile.Read(ref _captureThreadStuck) != 0;

    /// <summary>Non-null after the combined NDI capture thread faulted. The source is then terminal:
    /// audio/video adapters report exhausted and blocked video reads stop waiting.</summary>
    public Exception? Fault => _faultEx;

    /// <summary>Raised once if the combined capture thread faults. The handler runs on the capture
    /// thread; keep it lightweight.</summary>
    public event Action<Exception>? Faulted;

    public AudioFormat AudioFormat =>
        Volatile.Read(ref _audioState)?.Format
        ?? throw new InvalidOperationException("NDI source has not delivered an audio frame yet.");

    public VideoFormat VideoFormat =>
        _hasVideoFormat
            ? _videoFormat
            : throw new InvalidOperationException("NDI source has not delivered a video frame yet.");

    /// <summary>Non-throwing audio-format accessor: true with the format once an audio frame has been
    /// received, false (default) before then. Use instead of catching from <see cref="AudioFormat"/>
    /// when the stream may not be connected yet.</summary>
    public bool TryGetAudioFormat(out AudioFormat format)
    {
        var state = Volatile.Read(ref _audioState);
        if (state is null) { format = default; return false; }
        format = state.Format;
        return true;
    }

    /// <summary>Non-throwing video-format accessor: true with the format once a video frame has been
    /// received, false (default) before then.</summary>
    public bool TryGetVideoFormat(out VideoFormat format)
    {
        if (!_hasVideoFormat) { format = default; return false; }
        format = _videoFormat;
        return true;
    }

    /// <summary>
    /// Blocks until every <em>enabled</em> stream (<see cref="ReceiveAudio"/> / <see cref="ReceiveVideo"/>)
    /// has delivered its first frame — i.e. a format is available — or <paramref name="timeout"/> elapses.
    /// Returns true only if all enabled streams connected. Lets a caller obtain the format up front (e.g.
    /// before <c>AudioRouter.AddSource</c>) without hand-rolling a poll loop. The receiver runs from
    /// <see cref="Open"/>, so this just waits for the first frame(s).
    /// </summary>
    public bool WaitForStreams(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var wantAudio = ReceiveAudio;
        var wantVideo = ReceiveVideo;
        var deadline = Environment.TickCount64 + (long)Math.Ceiling(timeout.TotalMilliseconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return false;
            if (_faultEx is not null) return false;
            var audioReady = !wantAudio || IsAudioConnected;
            var videoReady = !wantVideo || IsVideoConnected;
            if (audioReady && videoReady) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(20);
        }
    }

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
            _videoRebaseBasePts = nextPresentationTime;
            _lastResolvedVideoPts = nextPresentationTime;
            _hasLastResolvedVideoPts = false;
            _videoNdiTimingOriginTicks = 0;
            _videoNdiTimingOriginSet = false;
        }
    }

    private int ReadAudioInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faultEx is not null) return 0;
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
            if (_disposed || _faultEx is not null)
            {
                frame = null!;
                return false;
            }

            lock (_videoWaitGate)
            {
                if (_videoQueue.IsEmpty && !_disposed && _faultEx is null)
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
                $"NDISource delivers {_videoFormat.PixelFormat} only; output requested {format}.");
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
        catch (Exception ex)
        {
            // The combined NDISource is the path HaPlay uses. Keep it aligned with the standalone
            // NDIAudioReceiver/NDIVideoReceiver fault-boundary contract: never let a background
            // capture/unpack/native error escape the thread and crash the host.
            _faultEx = ex;
            _state = NDIConnectionState.Disconnected;
            Trace.LogError(ex, "NDISource.CaptureLoop faulted");
            lock (_videoWaitGate)
                Monitor.PulseAll(_videoWaitGate);
            try { Faulted?.Invoke(ex); } catch { /* subscriber best effort */ }
            try { _ingestClock?.NotifyCaptureStopped(); } catch { /* best effort */ }
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
            var pts = ResolveVideoPresentationTime(in video);
            if (NDIVideoFrameUnpack.TryUnpack(video, pts, out var vf) && vf is not null)
            {
                EnsureVideoFormat(vf.Format);
                EnqueueVideoFrame(vf);
                var unpacked = Interlocked.Increment(ref _videoFramesUnpacked);
                if (unpacked <= 3 && Trace.IsEnabled(LogLevel.Information))
                {
                    var avgLuma = NDIVideoFrameUnpack.SampleAveragePackedLuma(vf);
                    Trace.LogInformation(
                        "NDISource: unpacked video frame #{N} fourCC={FourCc} native={NativeW}x{NativeH} stride={NativeStride} → {FmtW}x{FmtH} {FmtPf} avgLuma={AvgLuma:F1} range={Range} pts={Pts}",
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
                        "NDISource: dropped video frame (unpack failed) #{Drop} fourCC={FourCc} xres={Xres} yres={Yres} lineStride={Stride} pData={HasData}",
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
                "NDISource: dropped audio frame (interleaved conversion failed) fourCC={FourCc} channels={Channels} samples={Samples} sampleRate={SampleRate}",
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
        if (_hasLastResolvedVideoPts)
            _nextVideoPts = _lastResolvedVideoPts + _videoPtsStep;
        _hasVideoFormat = true;
    }

    private TimeSpan ResolveVideoPresentationTime(in NDIVideoFrameV2 video)
    {
        if (NDIFrameTiming.TryMapPresentationTime(
                video.Timecode,
                video.Timestamp,
                ref _videoNdiTimingOriginTicks,
                ref _videoNdiTimingOriginSet,
                out var relative))
        {
            var pts = _videoRebaseBasePts + relative;
            _lastResolvedVideoPts = pts;
            _hasLastResolvedVideoPts = true;
            _nextVideoPts = pts + _videoPtsStep;
            return pts;
        }

        var synthetic = _nextVideoPts;
        _lastResolvedVideoPts = synthetic;
        _hasLastResolvedVideoPts = true;
        _nextVideoPts += _videoPtsStep;
        return synthetic;
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
        _state = NDIConnectionState.Disposed;

        NDICaptureThreadLifecycle.StopAndDispose(
            nameof(NDISource),
            _captureThread,
            _cts,
            TimeSpan.FromSeconds(2),
            () => _ingestClock.NotifyCaptureStopped(),
            () =>
            {
                MediaDiagnostics.SwallowDisposeErrors(_receiver.Dispose, "NDISource.Dispose: NDIReceiver");
                MediaDiagnostics.SwallowDisposeErrors(_runtime.Dispose, "NDISource.Dispose: NDIRuntime");
            },
            () =>
            {
                while (_videoQueue.TryDequeue(out var f))
                    f.Dispose();
            },
            () =>
            {
                lock (_videoWaitGate)
                    Monitor.PulseAll(_videoWaitGate);
            },
            ex =>
            {
                Volatile.Write(ref _captureThreadStuck, 1);
                _state = NDIConnectionState.Stuck;
                _faultEx ??= ex;
            },
            Trace);
    }

    private static readonly AudioFormat StandbyAudioFormat = new(48_000, 2);
    private static readonly VideoFormat StandbyVideoFormat =
        new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));

    private sealed class AudioSourceAdapter(NDISource owner) : IAudioSource, IDisposable
    {
        public AudioFormat Format =>
            owner._receiveAudio && owner.IsAudioConnected ? owner.AudioFormat : StandbyAudioFormat;

        public bool IsExhausted => owner.IsDisposed || owner.Fault is not null;

        public int ReadInto(Span<float> destination)
        {
            if (!owner._receiveAudio)
            {
                destination.Clear();
                return destination.Length;
            }

            return owner.ReadAudioInto(destination);
        }

        public void Dispose() => owner.Dispose();
    }

    private sealed class VideoSourceAdapter(NDISource owner) : IVideoSource, IDisposable
    {
        public VideoFormat Format =>
            owner._receiveVideo && owner.IsVideoConnected ? owner.VideoFormat : StandbyVideoFormat;

        public IReadOnlyList<PixelFormat> NativePixelFormats =>
            owner._receiveVideo ? owner.NativeVideoPixelFormats : [PixelFormat.Bgra32];

        public bool IsExhausted => owner.IsDisposed || owner.Fault is not null || !owner._receiveVideo;

        public void SelectOutputFormat(PixelFormat format)
        {
            if (owner._receiveVideo)
                owner.SelectVideoOutputFormat(format);
        }

        public bool TryReadNextFrame(out VideoFrame frame)
        {
            if (!owner._receiveVideo)
            {
                frame = null!;
                return false;
            }

            return owner.TryReadNextVideoFrame(out frame);
        }

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
