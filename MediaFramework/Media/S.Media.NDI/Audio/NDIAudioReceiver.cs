using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Audio;

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
/// </remarks>
public sealed unsafe class NDIAudioReceiver : IAudioSource, IDisposable
{
    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();
    private readonly int _capacityFrames;

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

    /// <summary>
    /// Connects to the given NDI source. Capture begins immediately on a
    /// background thread.
    /// </summary>
    /// <param name="source">A discovered source from <see cref="NDIFinder"/>.</param>
    /// <param name="receiverName">Optional human-readable receiver name.</param>
    /// <param name="ringCapacityFrames">Upper bound on samples-per-channel buffered (default 96000 = ~2 s @ 48 kHz).</param>
    public NDIAudioReceiver(NDIDiscoveredSource source, string? receiverName = null, int ringCapacityFrames = 96000)
    {
        if (ringCapacityFrames < 1024)
            throw new ArgumentOutOfRangeException(nameof(ringCapacityFrames), "must be >= 1024");

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
        catch
        {
            _runtime.Dispose();
            throw;
        }

        _capacityFrames = ringCapacityFrames;

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
                    var totalFloats = samples * channels;

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
        var snap = new FormatSnapshot(new AudioFormat(sampleRate, channels), _capacityFrames);
        Volatile.Write(ref _state, snap);
        return snap;
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
        _cts.Cancel();
        _captureThread.Join(TimeSpan.FromSeconds(2));
        _cts.Dispose();
        _receiver.Dispose();
        _runtime.Dispose();
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
