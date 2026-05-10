using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.Core.Audio;
using S.Media.Core.Clock;

namespace S.Media.PortAudio;

/// <summary>
/// Audio sink backed by a PortAudio output stream. Producers call
/// <see cref="Submit"/> with packed float32 frames; PortAudio's audio-thread
/// callback drains an SPSC ring buffer and fills silence on underrun.
/// </summary>
/// <remarks>
/// Single-producer / single-consumer: the producer is whoever owns the
/// decoder/mixer thread; the consumer is PortAudio's internal audio thread.
/// The ring buffer is a power-of-two float array indexed via mask. Reads and
/// writes are <see cref="Volatile"/>-ordered around two monotonic counters.
/// </remarks>
public sealed unsafe class PortAudioOutput : IAudioSink, IClockedSink, IFlushableSink, IPlaybackClock, IDisposable
{
    private readonly AudioFormat _format;
    private readonly int _deviceIndex;
    private readonly double _suggestedLatency;
    private readonly nuint _framesPerBuffer;
    private readonly float[] _ringBuffer;
    private readonly int _ringMask;

    private long _writeIndex;
    private long _readIndex;
    private long _droppedSamples;
    private long _underrunSamples;
    private long _callbackCount;

    private nint _stream;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    public AudioFormat Format => _format;
    public bool IsRunning => _isRunning;
    public int DeviceIndex => _deviceIndex;

    /// <summary>Total samples (per channel × channels) PortAudio has already played from this output.</summary>
    public long PlayedSamples => Volatile.Read(ref _readIndex) / _format.Channels;
    /// <summary>Approximate samples-per-channel currently sitting in the ring buffer.</summary>
    public int QueuedSamples => (int)((Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex)) / _format.Channels);
    public int CapacitySamples => _ringBuffer.Length / _format.Channels;
    /// <summary>Samples dropped on Submit because the ring buffer was full.</summary>
    public long DroppedSamples => Volatile.Read(ref _droppedSamples);
    /// <summary>Samples zeroed by the callback because the ring was empty.</summary>
    public long UnderrunSamples => Volatile.Read(ref _underrunSamples);
    /// <summary>How many times the PA callback has fired (debug).</summary>
    public long CallbackCount => Volatile.Read(ref _callbackCount);
    /// <summary>1 = PA reports stream active, 0 = inactive, negative = error/closed.</summary>
    public int StreamActive => _stream != nint.Zero ? (int)Native.Pa_IsStreamActive(_stream) : -1;

    /// <summary>PortAudio's stream clock — wall-clock seconds since the stream started.</summary>
    public double StreamTime => _stream != nint.Zero ? Native.Pa_GetStreamTime(_stream) : 0.0;

    /// <summary>
    /// <see cref="IPlaybackClock.ElapsedSinceStart"/>: monotonic playback time
    /// derived from <see cref="PlayedSamples"/>. Stays at zero until the audio
    /// thread starts consuming the ring; freezes when the stream is stopped.
    /// </summary>
    public TimeSpan ElapsedSinceStart => TimeSpan.FromSeconds(PlayedSamples / (double)_format.SampleRate);

    /// <summary><see cref="IPlaybackClock.IsAdvancing"/>: true when the PA stream is open and reporting active.</summary>
    public bool IsAdvancing => _stream != nint.Zero && (int)Native.Pa_IsStreamActive(_stream) == 1;

    /// <summary>
    /// <see cref="IFlushableSink.Flush"/>: aborts the PortAudio stream
    /// (discards anything in the OS buffer), zeroes the ring counters, and
    /// restarts the stream. There is a brief audible gap (typically a few ms);
    /// in exchange the very next <see cref="Submit"/> plays without first
    /// hearing whatever was queued before <see cref="Flush"/>.
    /// </summary>
    public void Flush()
    {
        if (_disposed || !_isRunning || _stream == nint.Zero) return;
        Native.Pa_AbortStream(_stream);
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _readIndex, 0);
        var err = Native.Pa_StartStream(_stream);
        if (err != PaError.paNoError)
            PortAudioException.ThrowIfError(err, nameof(Native.Pa_StartStream));
    }

    /// <summary>
    /// Target queue depth (samples per channel) maintained by
    /// <see cref="WaitForCapacity"/>. Defaults to half the ring's capacity —
    /// enough headroom to absorb producer jitter without piling up enough
    /// latency to feel sluggish. Set before <see cref="Start"/>.
    /// </summary>
    public int TargetQueueSamples { get; set; }

    public PortAudioOutput(
        AudioFormat format,
        int? deviceIndex = null,
        double? suggestedLatency = null,
        int framesPerBuffer = 0,
        int ringCapacityFrames = 16384)
    {
        if (format.SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(format), "sample rate must be positive");
        if (format.Channels <= 0) throw new ArgumentOutOfRangeException(nameof(format), "channel count must be positive");
        if (framesPerBuffer < 0) throw new ArgumentOutOfRangeException(nameof(framesPerBuffer));
        if (ringCapacityFrames < 64) throw new ArgumentOutOfRangeException(nameof(ringCapacityFrames), "must be >= 64");

        _format = format;
        _framesPerBuffer = (nuint)framesPerBuffer;

        var capacityFloats = ringCapacityFrames * format.Channels;
        var rounded = 1;
        while (rounded < capacityFloats) rounded <<= 1;
        _ringBuffer = new float[rounded];
        _ringMask = rounded - 1;
        TargetQueueSamples = rounded / 2 / format.Channels;

        PortAudioRuntime.Acquire();
        try
        {
            _deviceIndex = deviceIndex ?? Native.Pa_GetDefaultOutputDevice();
            if (_deviceIndex < 0)
                throw new InvalidOperationException("no default PortAudio output device available");

            var devInfo = Native.Pa_GetDeviceInfo(_deviceIndex)
                ?? throw new InvalidOperationException($"invalid PortAudio device index {_deviceIndex}");
            if (devInfo.maxOutputChannels < format.Channels)
                throw new InvalidOperationException(
                    $"device '{devInfo.Name}' supports {devInfo.maxOutputChannels} output channels, requested {format.Channels}");

            // Default to defaultHighOutputLatency: managed producers can't reliably
            // sustain the sub-5ms periods that defaultLowOutputLatency negotiates
            // on PulseAudio/ALSA. Callers who own their threading can opt in to lower.
            _suggestedLatency = suggestedLatency ?? devInfo.defaultHighOutputLatency;
        }
        catch
        {
            PortAudioRuntime.Release();
            throw;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isRunning) return;

        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        var outParams = new PaStreamParameters
        {
            device = _deviceIndex,
            channelCount = _format.Channels,
            sampleFormat = PaSampleFormat.paFloat32,
            suggestedLatency = _suggestedLatency,
            hostApiSpecificStreamInfo = nint.Zero,
        };

        delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int> cbPtr = &Callback;

        var err = Native.Pa_OpenStream(
            out _stream,
            inputParameters: null,
            outputParameters: outParams,
            sampleRate: _format.SampleRate,
            framesPerBuffer: _framesPerBuffer,
            streamFlags: PaStreamFlags.paNoFlag,
            streamCallback: cbPtr,
            userData: GCHandle.ToIntPtr(_selfHandle));

        if (err != PaError.paNoError)
        {
            _selfHandle.Free();
            PortAudioException.ThrowIfError(err, nameof(Native.Pa_OpenStream));
        }

        err = Native.Pa_StartStream(_stream);
        if (err != PaError.paNoError)
        {
            Native.Pa_CloseStream(_stream);
            _stream = nint.Zero;
            _selfHandle.Free();
            PortAudioException.ThrowIfError(err, nameof(Native.Pa_StartStream));
        }

        _isRunning = true;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        if (_stream != nint.Zero)
        {
            Native.Pa_StopStream(_stream);
            Native.Pa_CloseStream(_stream);
            _stream = nint.Zero;
        }
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _isRunning = false;
    }

    /// <summary>Convenience overload: submits a frame's samples after validating its format.</summary>
    public void Submit(in AudioFrame frame)
    {
        if (frame.Format != _format)
            throw new ArgumentException(
                $"frame format {frame.Format} does not match output format {_format}", nameof(frame));
        Submit(frame.Samples.Span);
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packedSamples.Length % _format.Channels != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {_format.Channels}",
                nameof(packedSamples));

        var write = Volatile.Read(ref _writeIndex);
        var read = Volatile.Read(ref _readIndex);
        var freeFloats = _ringBuffer.Length - (int)(write - read);
        var toWrite = Math.Min(packedSamples.Length, freeFloats);

        if (toWrite > 0)
        {
            var startIdx = (int)(write & _ringMask);
            var firstChunk = Math.Min(toWrite, _ringBuffer.Length - startIdx);
            packedSamples[..firstChunk].CopyTo(_ringBuffer.AsSpan(startIdx));
            if (firstChunk < toWrite)
                packedSamples.Slice(firstChunk, toWrite - firstChunk).CopyTo(_ringBuffer.AsSpan(0));
            Volatile.Write(ref _writeIndex, write + toWrite);
        }

        var dropped = packedSamples.Length - toWrite;
        if (dropped > 0)
            Interlocked.Add(ref _droppedSamples, dropped);
    }

    /// <summary>
    /// <see cref="IClockedSink"/> implementation: paces the router against the
    /// device's actual playback rate. Returns when adding
    /// <paramref name="chunkSamples"/> per channel would still leave the queued
    /// total at or below <see cref="TargetQueueSamples"/>; otherwise sleeps
    /// for as long as the device needs to consume the excess.
    /// </summary>
    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        if (chunkSamples <= 0) return !token.IsCancellationRequested;

        // Before the stream is started PA isn't draining yet — pretend ready,
        // so prebuffering can fill the ring up to the target before Start().
        if (!_isRunning) return !token.IsCancellationRequested;

        var target = TargetQueueSamples;
        while (!token.IsCancellationRequested)
        {
            var queued = QueuedSamples;
            if (queued + chunkSamples <= target) return true;

            // Estimate how long until the device drains the excess. Add a 1ms
            // floor so we don't spin when we're only marginally over.
            var excessSamples = queued + chunkSamples - target;
            var waitMs = Math.Max(1, (int)(1000.0 * excessSamples / _format.SampleRate));
            if (token.WaitHandle.WaitOne(waitMs)) return false;
        }
        return false;
    }

    /// <summary>
    /// Test-only drain: reads up to <paramref name="dst"/>.Length samples
    /// out of the ring buffer (bypassing the audio callback path).
    /// Used to verify wraparound and ring-buffer accounting without a real device.
    /// </summary>
    internal int TryDrainForTest(Span<float> dst)
    {
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Callback(
        nint inputBuffer, nint outputBuffer, nuint frames,
        nint timeInfo, PaStreamCallbackFlags flags, nint userData)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not PortAudioOutput self)
                return (int)PaStreamCallbackResult.paAbort;

            Interlocked.Increment(ref self._callbackCount);
            var totalFloats = (int)frames * self._format.Channels;
            var output = new Span<float>((float*)outputBuffer, totalFloats);

            var read = Volatile.Read(ref self._readIndex);
            var write = Volatile.Read(ref self._writeIndex);
            var available = (int)(write - read);
            var toRead = Math.Min(totalFloats, available);

            if (toRead > 0)
            {
                var startIdx = (int)(read & self._ringMask);
                var firstChunk = Math.Min(toRead, self._ringBuffer.Length - startIdx);
                self._ringBuffer.AsSpan(startIdx, firstChunk).CopyTo(output);
                if (firstChunk < toRead)
                    self._ringBuffer.AsSpan(0, toRead - firstChunk).CopyTo(output[firstChunk..]);
                Volatile.Write(ref self._readIndex, read + toRead);
            }

            if (toRead < totalFloats)
            {
                output[toRead..].Clear();
                Interlocked.Add(ref self._underrunSamples, totalFloats - toRead);
            }

            return (int)PaStreamCallbackResult.paContinue;
        }
        catch
        {
            // Throwing across the unmanaged boundary would crash the process.
            return (int)PaStreamCallbackResult.paAbort;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        PortAudioRuntime.Release();
    }
}
