using System.Runtime.InteropServices;
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
public sealed unsafe class NDIAudioReceiver : IAudioSource, IDisposable
{
    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIIngestPlaybackClock? _ingestClock;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly TimeSpan _capacityDuration;
    private readonly TimeSpan _maxReadLatency;
    /// <summary>Hard floor in frames-per-channel; mirrors the pre-2026-05 minimum to keep tiny durations from producing pathological rings.</summary>
    private const int MinCapacityFrames = 1024;

    private FormatSnapshot? _state;
    private long _overflowSamples;
    private bool _disposed;

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

    public bool IsExhausted => _disposed;

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

    /// <summary>Shared ingest clock when paired with <see cref="Video.NDIVideoReceiver"/> on the same source.</summary>
    public NDIIngestPlaybackClock? IngestClock => _ingestClock;

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
    /// <param name="ingestClock">Optional <see cref="S.Media.Core.Clock.IPlaybackClock"/> implementation updated from this receiver's capture thread.</param>
    public NDIAudioReceiver(
        NDIDiscoveredSource source,
        string? receiverName = null,
        TimeSpan ringCapacityDuration = default,
        NDIIngestPlaybackClock? ingestClock = null,
        TimeSpan? maxReadLatency = null)
    {
        if (ringCapacityDuration == default)
            ringCapacityDuration = TimeSpan.FromSeconds(2);
        _maxReadLatency = maxReadLatency ?? ringCapacityDuration;
        if (ringCapacityDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ringCapacityDuration), "must be > 0");

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

        _captureThread = new Thread(() => CaptureLoop(_cts.Token))
        {
            IsBackground = true,
            Name = "NDIAudioReceiver",
        };
        _captureThread.Start();
    }

    /// <summary>
    /// Discards buffered samples and re-anchors the ingest clock for a new play pass.
    /// Call when transport starts so pre-buffered audio (captured while waiting to play) is not
    /// heard behind freshly reset video.
    /// </summary>
    public void ResetPlaybackBuffer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snap = Volatile.Read(ref _state);
        if (snap is not null)
        {
            var write = Volatile.Read(ref snap.WriteIndex);
            Volatile.Write(ref snap.ReadIndex, write);
        }

        _ingestClock?.AttachReceiver();
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

        TrimExcessBuffered(snap);

        var read = Volatile.Read(ref snap.ReadIndex);
        var write = Volatile.Read(ref snap.WriteIndex);
        var available = (int)(write - read);
        var toRead = Math.Min(dst.Length, available);
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
                    NDIAudioUtils.ToInterleaved32f(audio, ref interleaved);
                    _ingestClock?.NotifyAudioFrame(in audio);
                    _receiver.FreeAudio(audio);

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
        var snap = new FormatSnapshot(new AudioFormat(sampleRate, channels), capacityFrames);
        Volatile.Write(ref _state, snap);
        return snap;
    }

    /// <summary>Drops oldest samples when the ring holds more than <see cref="_maxReadLatency"/> of audio.</summary>
    private void TrimExcessBuffered(FormatSnapshot snap)
    {
        var channels = snap.Channels;
        var sampleRate = snap.Format.SampleRate;
        if (sampleRate <= 0 || channels <= 0)
            return;

        var maxFrames = Math.Max(64, (int)(_maxReadLatency.TotalSeconds * sampleRate));
        var read = Volatile.Read(ref snap.ReadIndex);
        var write = Volatile.Read(ref snap.WriteIndex);
        var bufferedFrames = (int)((write - read) / channels);
        if (bufferedFrames <= maxFrames)
            return;

        var skipFrames = bufferedFrames - maxFrames;
        Volatile.Write(ref snap.ReadIndex, read + (long)skipFrames * channels);
    }

    private void EnqueueSamples(FormatSnapshot snap, ReadOnlySpan<float> src)
    {
        var write = Volatile.Read(ref snap.WriteIndex);
        var read = Volatile.Read(ref snap.ReadIndex);
        var freeFloats = snap.RingBuffer.Length - (int)(write - read);
        var toWrite = Math.Min(src.Length, freeFloats);

        if (toWrite > 0)
        {
            var startIdx = (int)(write & snap.RingMask);
            var firstChunk = Math.Min(toWrite, snap.RingBuffer.Length - startIdx);
            src[..firstChunk].CopyTo(snap.RingBuffer.AsSpan(startIdx));
            if (firstChunk < toWrite)
                src.Slice(firstChunk, toWrite - firstChunk).CopyTo(snap.RingBuffer.AsSpan(0));
            Volatile.Write(ref snap.WriteIndex, write + toWrite);
        }

        var dropped = src.Length - toWrite;
        if (dropped > 0)
            Interlocked.Add(ref _overflowSamples, dropped);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts.Cancel();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver.Dispose: Cancel");
        }
#else
        catch
        {
        }
#endif
        try
        {
            CooperativePlaybackJoin.JoinThread(_captureThread, TimeSpan.FromSeconds(2));
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver.Dispose: JoinThread");
        }
#else
        catch
        {
        }
#endif
        try
        {
            _ingestClock?.NotifyCaptureStopped();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver.Dispose: NotifyCaptureStopped");
        }
#else
        catch
        {
        }
#endif
        try
        {
            _cts.Dispose();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver.Dispose: CancellationTokenSource.Dispose");
        }
#else
        catch
        {
        }
#endif
        try
        {
            _receiver.Dispose();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver.Dispose: NDIReceiver");
        }
#else
        catch
        {
        }
#endif
        try
        {
            _runtime.Dispose();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "NDIAudioReceiver.Dispose: NDIRuntime");
        }
#else
        catch
        {
        }
#endif
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
        public long WriteIndex;
        public long ReadIndex;

        public FormatSnapshot(AudioFormat format, int capacityFrames)
        {
            Format = format;
            Channels = format.Channels;
            var capFloats = capacityFrames * format.Channels;
            var rounded = 1;
            while (rounded < capFloats) rounded <<= 1;
            RingBuffer = new float[rounded];
            RingMask = rounded - 1;
        }
    }
}
