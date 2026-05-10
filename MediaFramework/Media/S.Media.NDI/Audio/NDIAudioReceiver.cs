using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Audio;

namespace S.Media.NDI.Audio;

/// <summary>
/// <see cref="IAudioSource"/> backed by an NDI receiver. Captures the audio
/// stream from a discovered NDI source on a background thread, converts the
/// native planar float (FLTP) frames to packed float32 via NDIlib's
/// interleaving utility, and queues into a lock-free SPSC ring buffer that
/// <see cref="TryReadFrame"/> drains.
/// </summary>
/// <remarks>
/// The first audio frame from the source determines the
/// <see cref="AudioFormat"/> (sample rate × channel count). Until that frame
/// arrives <see cref="Format"/> is unknown — wait for <see cref="IsConnected"/>
/// or for a successful read before binding routes.
/// </remarks>
public sealed unsafe class NDIAudioReceiver : IAudioSource, IDisposable
{
    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly Thread _captureThread;
    private readonly CancellationTokenSource _cts = new();

    private readonly Lock _formatGate = new();
    private AudioFormat _format;
    private bool _formatKnown;

    private float[] _ringBuffer = new float[1];
    private int _ringMask;
    private long _writeIndex;
    private long _readIndex;
    private long _overflowSamples;
    private bool _disposed;

    /// <summary>True once at least one audio frame has been received and
    /// <see cref="Format"/> is meaningful.</summary>
    public bool IsConnected
    {
        get { lock (_formatGate) return _formatKnown; }
    }

    public AudioFormat Format
    {
        get
        {
            lock (_formatGate)
            {
                return _formatKnown
                    ? _format
                    : throw new InvalidOperationException("NDI source has not delivered an audio frame yet — wait until IsConnected is true");
            }
        }
    }

    public bool IsExhausted => _disposed;

    /// <summary>Approximate samples-per-channel currently buffered.</summary>
    public int AvailableSamples
    {
        get
        {
            lock (_formatGate)
            {
                if (!_formatKnown) return 0;
                return (int)((Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex)) / _format.Channels);
            }
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

        // Allocate a 1-element placeholder; real ring is built once we know the channel count.
        _ringMask = 0;

        var initialCapacity = ringCapacityFrames;
        // Pre-allocate assuming the worst case is 16 channels; resized on first frame anyway.
        AllocateRing(initialCapacity, channels: 1);

        _captureThread = new Thread(() => CaptureLoop(_cts.Token, initialCapacity))
        {
            IsBackground = true,
            Name = "NDIAudioReceiver",
        };
        _captureThread.Start();
    }

    public int ReadInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Hold the format gate for the entire operation. The capture thread's
        // EnsureFormat (and the AllocateRing it calls) take the same lock, so
        // a mid-stream reformat cannot reassign _ringBuffer or reset the
        // indices while we're reading. Lock duration is just a memcpy of the
        // chunk — microseconds.
        lock (_formatGate)
        {
            if (!_formatKnown) return 0;
            var channels = _format.Channels;
            if (dst.Length % channels != 0)
                throw new ArgumentException(
                    $"dst length {dst.Length} is not a multiple of channel count {channels}", nameof(dst));

            var read = Volatile.Read(ref _readIndex);
            var write = Volatile.Read(ref _writeIndex);
            var available = (int)(write - read);
            var toRead = Math.Min(dst.Length, available);
            if (toRead == 0) return 0;

            var startIdx = (int)(read & _ringMask);
            var firstChunk = Math.Min(toRead, _ringBuffer.Length - startIdx);
            _ringBuffer.AsSpan(startIdx, firstChunk).CopyTo(dst);
            if (firstChunk < toRead)
                _ringBuffer.AsSpan(0, toRead - firstChunk).CopyTo(dst[firstChunk..]);
            Volatile.Write(ref _readIndex, read + toRead);
            return toRead;
        }
    }

    private void CaptureLoop(CancellationToken token, int desiredCapacityFrames)
    {
        // Buffer for the packed-interleaved conversion result. Resized on demand.
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

                    EnsureFormat(sampleRate, channels, desiredCapacityFrames);

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

                    EnqueueSamples(heldBuffer.AsSpan(0, totalFloats));
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

    private void EnsureFormat(int sampleRate, int channels, int capacityFrames)
    {
        lock (_formatGate)
        {
            if (_formatKnown)
            {
                if (_format.SampleRate != sampleRate || _format.Channels != channels)
                {
                    // Source format changed mid-stream — drop the queue and re-init.
                    _format = new AudioFormat(sampleRate, channels);
                    AllocateRing(capacityFrames, channels);
                }
                return;
            }
            _format = new AudioFormat(sampleRate, channels);
            AllocateRing(capacityFrames, channels);
            _formatKnown = true;
        }
    }

    private void AllocateRing(int capacityFrames, int channels)
    {
        var capFloats = capacityFrames * channels;
        var rounded = 1;
        while (rounded < capFloats) rounded <<= 1;
        _ringBuffer = new float[rounded];
        _ringMask = rounded - 1;
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _readIndex, 0);
    }

    private void EnqueueSamples(ReadOnlySpan<float> src)
    {
        var write = Volatile.Read(ref _writeIndex);
        var read = Volatile.Read(ref _readIndex);
        var freeFloats = _ringBuffer.Length - (int)(write - read);
        var toWrite = Math.Min(src.Length, freeFloats);

        if (toWrite > 0)
        {
            var startIdx = (int)(write & _ringMask);
            var firstChunk = Math.Min(toWrite, _ringBuffer.Length - startIdx);
            src[..firstChunk].CopyTo(_ringBuffer.AsSpan(startIdx));
            if (firstChunk < toWrite)
                src.Slice(firstChunk, toWrite - firstChunk).CopyTo(_ringBuffer.AsSpan(0));
            Volatile.Write(ref _writeIndex, write + toWrite);
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
}