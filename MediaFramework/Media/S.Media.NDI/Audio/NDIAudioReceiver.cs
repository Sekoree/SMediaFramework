using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NDILib;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;
using S.Media.NDI.Clock;

namespace S.Media.NDI.Audio;

/// <summary>
/// <see cref="IAudioSource"/> backed by an NDI receiver. Captures the audio
/// stream from a discovered NDI source on a background thread, converts the
/// native planar float (FLTP) frames to packed float32 via NDIlib's
/// interleaving utility, and queues into a lock-free SPSC ring buffer that
/// <see cref="ReadInto"/> drains.
/// </summary>
/// <remarks>
/// <para>
/// The first audio frame from the source determines the
/// <see cref="AudioFormat"/> (sample rate × channel count). Until that frame
/// arrives <see cref="Format"/> is unknown — wait for <see cref="IsConnected"/>
/// or for a successful read before binding routes.
/// </para>
/// <para>
/// Read path is lock-free: producer and consumer share an immutable
/// <see cref="FormatSnapshot"/> record holding the ring + format. A
/// mid-stream format change publishes a fresh snapshot via
/// <see cref="Volatile.Write{T}"/>; the in-flight chunk on the abandoned
/// snapshot is discarded (acceptable for the rare format-change event).
/// </para>
/// <para>
/// <see cref="Dispose"/> tears down capture, join, clock notification, and native receiver/runtime in order; each step is wrapped so
/// <strong>Debug</strong> builds log failures via <see cref="MediaDiagnostics.LogError"/> while <strong>Release</strong> builds continue best-effort.
/// </para>
/// </remarks>
internal sealed unsafe class NDIAudioReceiver : IAudioSource, IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.NDI.Audio.NDIAudioReceiver");

    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIIngestPlaybackClock? _ingestClock;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _capacityDuration;
    private readonly TimeSpan _minBufferedDuration;
    /// <summary>Hard floor in frames-per-channel; mirrors the pre-2026-05 minimum to keep tiny durations from producing pathological rings.</summary>
    private const int MinCapacityFrames = 1024;
    /// <summary>Default jitter-buffer prime threshold. Covers the worst-case inter-NDI-frame gap (33 ms at 30p) plus a margin so router pulls can ride over burst timing.</summary>
    public static readonly TimeSpan DefaultMinBufferedDuration = TimeSpan.FromMilliseconds(50);

    private FormatSnapshot? _state;
    private long _overflowSamples;
    private long _conversionDrops;
    private bool _disposed;
    private volatile Exception? _faultEx;

    /// <summary>True once at least one audio frame has been received and
    /// <see cref="Format"/> is meaningful.</summary>
    public bool IsConnected => Volatile.Read(ref _state) is not null;

    public AudioFormat Format
    {
        get
        {
            var snap = Volatile.Read(ref _state);
            return snap is null
                ? throw new InvalidOperationException("NDI source has not delivered an audio frame yet — wait until IsConnected is true")
                : snap.Format;
        }
    }

    public bool IsExhausted => _disposed || _faultEx is not null;

    /// <summary>Non-null after the background capture thread faulted. The receiver is then terminal:
    /// <see cref="IsExhausted"/> becomes true so the router stops pulling.</summary>
    public Exception? Fault => _faultEx;

    /// <summary>Raised once if the background capture thread faults (native/conversion error). The handler
    /// runs on the capture thread; keep it lightweight.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Approximate samples-per-channel currently buffered.</summary>
    public int AvailableSamples
    {
        get
        {
            var snap = Volatile.Read(ref _state);
            if (snap is null) return 0;
            return (int)((Volatile.Read(ref snap.WriteIndex) - Volatile.Read(ref snap.ReadIndex)) / snap.Channels);
        }
    }

    public long OverflowSamples => Volatile.Read(ref _overflowSamples);

    /// <summary>
    /// Skips ahead to the most recent samples by advancing the consumer's read pointer so the ring
    /// holds no more than <paramref name="keepBuffered"/> of audio. Intended for the play-start moment
    /// in HaPlay: the receiver runs continuously from connect, so when the operator finally hits Play
    /// the ring already contains seconds of stale samples that the router would otherwise consume in
    /// FIFO order — making audio play <c>Tconnect</c> seconds behind real time.
    /// </summary>
    /// <param name="keepBuffered">
    /// Default <see cref="TimeSpan.Zero"/> falls back to twice <see cref="DefaultMinBufferedDuration"/>
    /// (100 ms), enough to absorb the worst-case NDI burst gap (33 ms at 30p) plus the receiver-side
    /// jitter reserve. Clamped to <see cref="DefaultMinBufferedDuration"/> minimum so callers can't
    /// accidentally choose a value below the steady-state holdback.
    /// </param>
    public void RebaseToLatest(TimeSpan keepBuffered = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snap = Volatile.Read(ref _state);
        if (snap is null) return; // No frames captured yet — nothing to discard.

        var keep = keepBuffered <= TimeSpan.Zero
            ? TimeSpan.FromTicks(DefaultMinBufferedDuration.Ticks * 2)
            : (keepBuffered < DefaultMinBufferedDuration ? DefaultMinBufferedDuration : keepBuffered);
        var keepFrames = Math.Max(0, (int)(keep.TotalSeconds * snap.Format.SampleRate));
        var keepFloats = checked(keepFrames * snap.Channels);

        var write = Volatile.Read(ref snap.WriteIndex);
        var read = Volatile.Read(ref snap.ReadIndex);
        var buffered = (int)(write - read);
        if (buffered <= keepFloats) return;

        var skip = buffered - keepFloats;
        Volatile.Write(ref snap.ReadIndex, read + skip);
    }

    /// <summary>
    /// Connects to the given NDI source. Capture begins immediately on a
    /// background thread.
    /// </summary>
    /// <param name="source">A discovered source from <see cref="NDIFinder"/>.</param>
    /// <param name="receiverName">Optional human-readable receiver name.</param>
    /// <param name="ringCapacityDuration">
    /// Upper bound on buffered audio expressed as a <see cref="TimeSpan"/> (default 2 s). The frame count
    /// is computed from the first observed sample rate, so the ring is right-sized whether the source
    /// sends 44.1, 48 or 96 kHz. A floor of <c>1024</c> frames is enforced so very short durations at low
    /// sample rates still yield a usable ring (matches the pre-<see cref="TimeSpan"/> minimum).
    /// </param>
    /// <param name="minBufferedDuration">
    /// Jitter-buffer holdback. NDI senders deliver audio in bursts aligned to video frames (typically
    /// 16.7 ms at 60p or 33.3 ms at 30p). The <c>AudioRouter</c> pulls fixed-size chunks at a faster
    /// cadence, so without a holdback the ring frequently drains to fewer samples than the router asks
    /// for and the router silence-pads the tail — audible as "lots of small dropouts." This holdback is
    /// the amount of samples the ring accumulates before <see cref="ReadInto"/> starts handing audio
    /// out after startup or underrun. Once primed, the buffered reserve is consumable so bursty NDI
    /// delivery can be smoothed into smaller router chunks. <c>null</c> uses
    /// <see cref="DefaultMinBufferedDuration"/> (50 ms); <see cref="TimeSpan.Zero"/> opts out and
    /// restores the pre-2026-05 zero-latency behaviour. Clamped to half the ring capacity.
    /// </param>
    /// <param name="ingestClock">Optional <see cref="S.Media.Core.Clock.IPlaybackClock"/> implementation updated from this receiver's capture thread.</param>
    public NDIAudioReceiver(
        NDIDiscoveredSource source,
        string? receiverName = null,
        TimeSpan ringCapacityDuration = default,
        TimeSpan? minBufferedDuration = null,
        NDIIngestPlaybackClock? ingestClock = null)
    {
        if (ringCapacityDuration == default)
            ringCapacityDuration = TimeSpan.FromSeconds(2);
        if (ringCapacityDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ringCapacityDuration), "must be > 0");
        var resolvedMinBuffered = minBufferedDuration ?? DefaultMinBufferedDuration;
        if (resolvedMinBuffered < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minBufferedDuration), "must be >= 0");

        _ingestClock = ingestClock;
        _ingestClock?.AttachReceiver();

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null)
            throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            var settings = new NDIReceiverSettings { ReceiverName = receiverName };
            rc = NDIReceiver.Create(out var recv, settings);
            if (rc != 0 || recv is null)
                throw new NDIException(rc, "NDIReceiver.Create");
            _receiver = recv;
            _receiver.Connect(source);
        }
        catch (Exception ex)
        {
#if DEBUG
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver: NDIReceiver.Create/Connect");
#else
            _ = ex;
#endif
            _runtime.Dispose();
            throw;
        }

        _capacityDuration = ringCapacityDuration;
        _minBufferedDuration = resolvedMinBuffered;

        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "NDIAudioReceiver",
        };
        _captureThread.Start();
    }

    public int ReadInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Lock-free read: capture the current snapshot once. If a format
        // change races with us mid-read, we still get a self-consistent view
        // of the snapshot we observed (its buffer is rooted by our local).
        var snap = Volatile.Read(ref _state);
        if (snap is null) return 0;

        var channels = snap.Channels;
        if (dst.Length % channels != 0)
            throw new ArgumentException(
                $"dst length {dst.Length} is not a multiple of channel count {channels}", nameof(dst));

        var read = Volatile.Read(ref snap.ReadIndex);
        var write = Volatile.Read(ref snap.WriteIndex);
        var available = (int)(write - read);
        var primed = Volatile.Read(ref snap.Primed) != 0;
        var toRead = ComputeReadCount(dst.Length, available, snap.MinBufferedFloats, ref primed);
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

    private void CaptureLoop(CancellationToken token)
    {
        var interleaved = new NDIAudioInterleaved32f();
        var heldBuffer = Array.Empty<float>();
        GCHandle pin = default;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var frameType = _receiver.Capture(out var video, out var audio, out var metadata, timeoutMs: 100);

                if (frameType == NDIFrameType.Audio)
                {
                    var samples = audio.NoSamples;
                    var channels = audio.NoChannels;
                    var sampleRate = audio.SampleRate;
                    long totalFloatsLong = (long)samples * channels;
                    if (totalFloatsLong <= 0 || totalFloatsLong > Array.MaxLength)
                    {
                        _receiver.FreeAudio(audio);
                        continue;
                    }

                    var totalFloats = (int)totalFloatsLong;

                    var snap = EnsureFormat(sampleRate, channels);

                    // Resize / pin our packed-output buffer if needed.
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
                    bool converted;
                    try
                    {
                        converted = NDIAudioUtils.ToInterleaved32f(audio, ref interleaved);
                        if (converted)
                            _ingestClock?.NotifyAudioFrame(in audio);
                    }
                    finally
                    {
                        _receiver.FreeAudio(audio);
                    }

                    if (!converted)
                    {
                        LogConversionDrop(in audio);
                        continue;
                    }

                    EnqueueSamples(snap, heldBuffer.AsSpan(0, totalFloats));
                }
                else if (frameType == NDIFrameType.Video)
                {
                    // Audio-only consumer: free any video frames so the network buffer keeps draining.
                    _receiver.FreeVideo(video);
                }
                else if (frameType == NDIFrameType.Metadata)
                {
                    _receiver.FreeMetadata(metadata);
                }
            }
        }
        catch (Exception ex)
        {
            // Background capture must never crash the host. Record a terminal fault and surface it;
            // ReadInto then drains to empty and IsExhausted becomes true so the router stops pulling.
            _faultEx = ex;
#if DEBUG
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver.CaptureLoop faulted");
#else
            _ = ex;
#endif
            try { Faulted?.Invoke(ex); } catch { /* subscriber best effort */ }
        }
        finally
        {
            if (pin.IsAllocated) pin.Free();
        }
    }

    private FormatSnapshot EnsureFormat(int sampleRate, int channels)
    {
        var existing = Volatile.Read(ref _state);
        if (existing is not null
            && existing.Format.SampleRate == sampleRate
            && existing.Format.Channels == channels)
            return existing;

        // First frame, or a mid-stream format change: publish a fresh
        // snapshot. The previous one (if any) is dropped on the floor and
        // GC'd once the reader's local goes out of scope; that's acceptable
        // — format changes are rare and a brief discontinuity at the
        // boundary is preferable to copying state across.
        var capacityFrames = Math.Max(MinCapacityFrames, (int)(_capacityDuration.TotalSeconds * sampleRate));
        var minBufferedFrames = ComputeMinBufferedFrames(_minBufferedDuration, sampleRate, capacityFrames);
        var snap = new FormatSnapshot(new AudioFormat(sampleRate, channels), capacityFrames, minBufferedFrames);
        Volatile.Write(ref _state, snap);
        return snap;
    }

    /// <summary>
    /// Computes the jitter-buffer holdback in frames-per-channel for a given sample rate and ring
    /// capacity. Capped at half the ring capacity so the holdback can never starve the consumer of
    /// the entire ring; clamped to zero for non-positive durations so callers can opt out.
    /// </summary>
    internal static int ComputeMinBufferedFrames(TimeSpan minBufferedDuration, int sampleRate, int capacityFrames)
    {
        if (minBufferedDuration <= TimeSpan.Zero || sampleRate <= 0 || capacityFrames <= 0)
            return 0;
        var requested = (int)(minBufferedDuration.TotalSeconds * sampleRate);
        if (requested <= 0) return 0;
        var cap = Math.Max(0, capacityFrames / 2);
        return Math.Min(cap, requested);
    }

    /// <summary>
    /// Jitter-buffer read policy. The holdback is a startup/recovery threshold, not a permanent floor:
    /// once primed, the consumer may read from the buffered reserve so NDI's video-frame-sized audio
    /// bursts can be smoothed into smaller router chunks.
    /// </summary>
    internal static int ComputeReadCount(int requestedFloats, int availableFloats, int minBufferedFloats, ref bool primed)
    {
        if (requestedFloats <= 0 || availableFloats <= 0)
            return 0;

        if (minBufferedFloats <= 0)
        {
            primed = true;
            return Math.Min(requestedFloats, availableFloats);
        }

        if (!primed)
        {
            if (availableFloats < minBufferedFloats)
                return 0;
            primed = true;
        }

        var toRead = Math.Min(requestedFloats, availableFloats);
        if (toRead < requestedFloats)
            primed = false;
        return toRead;
    }

    private void EnqueueSamples(FormatSnapshot snap, ReadOnlySpan<float> src)
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
        var freeFloats = capacity - buffered;
        if (src.Length > freeFloats)
        {
            // Live receivers should keep the newest audio, especially when pre-connected before Play.
            // Drop oldest buffered samples rather than rejecting fresh network samples.
            var dropOld = src.Length - freeFloats;
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
            Interlocked.Add(ref _overflowSamples, dropped);
    }

    private void LogConversionDrop(in NDIAudioFrameV3 audio)
    {
        var drops = Interlocked.Increment(ref _conversionDrops);
        if (drops <= 5)
        {
            MediaDiagnostics.LogWarning(
                "NDIAudioReceiver: dropped audio frame (interleaved conversion failed) fourCC={FourCc} channels={Channels} samples={Samples} sampleRate={SampleRate}",
                audio.FourCC, audio.NoChannels, audio.NoSamples, audio.SampleRate);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(_cts.Cancel, "NDIAudioReceiver.Dispose: Cancel");
        MediaDiagnostics.SwallowDisposeErrors(() => CooperativePlaybackJoin.JoinThread(_captureThread, TimeSpan.FromSeconds(2)), "NDIAudioReceiver.Dispose: JoinThread");
        var captureStopped = !_captureThread.IsAlive;
        if (captureStopped)
            MediaDiagnostics.SwallowDisposeErrors(() => _ingestClock?.NotifyCaptureStopped(), "NDIAudioReceiver.Dispose: NotifyCaptureStopped");
        if (captureStopped)
            MediaDiagnostics.SwallowDisposeErrors(_cts.Dispose, "NDIAudioReceiver.Dispose: CancellationTokenSource.Dispose");
        else
        {
            _faultEx ??= new TimeoutException("NDIAudioReceiver capture thread did not exit during Dispose; native receiver/runtime were intentionally leaked.");
            Trace.LogError(_faultEx, "NDIAudioReceiver.Dispose: capture thread still alive after join cap; leaking native receiver/runtime and CTS to avoid use-after-dispose.");
        }

        if (captureStopped)
        {
            MediaDiagnostics.SwallowDisposeErrors(_receiver.Dispose, "NDIAudioReceiver.Dispose: NDIReceiver");
            MediaDiagnostics.SwallowDisposeErrors(_runtime.Dispose, "NDIAudioReceiver.Dispose: NDIRuntime");
        }
    }

    /// <summary>
    /// Immutable-shape snapshot of (format, ring, indices). The ring and
    /// format never change after construction; the long indices are mutated
    /// volatilely by exactly one producer (capture thread) and one consumer
    /// (router thread).
    /// </summary>
    private sealed class FormatSnapshot
    {
        public readonly AudioFormat Format;
        public readonly int Channels;
        public readonly float[] RingBuffer;
        public readonly int RingMask;
        public readonly int UsableFloats;
        /// <summary>Jitter-buffer holdback in floats (channel-aligned). See <see cref="ReadInto"/>.</summary>
        public readonly int MinBufferedFloats;
        public int Primed;
        public long WriteIndex;
        public long ReadIndex;

        public FormatSnapshot(AudioFormat format, int capacityFrames, int minBufferedFrames)
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
