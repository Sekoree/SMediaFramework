using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;

namespace S.Media.PortAudio;

/// <summary>
/// Audio output backed by a PortAudio output stream. Producers call
/// <see cref="Submit"/> with packed float32 frames; PortAudio's audio-thread
/// callback drains an SPSC ring buffer and fills silence on underrun.
/// </summary>
/// <remarks>
/// <para>
/// Single-producer / single-consumer: the producer is whoever owns the
/// decoder/mixer thread; the consumer is PortAudio's internal audio thread.
/// The ring buffer is a power-of-two float array indexed via mask. Reads and
/// writes are <see cref="Volatile"/>-ordered around two monotonic counters.
/// </para>
/// <para>
/// <see cref="Dispose"/> calls <see cref="Stop"/> then <see cref="PortAudioRuntime.Release"/>; each step is wrapped so <strong>Debug</strong> builds log via <see cref="MediaDiagnostics.LogError"/> while <strong>Release</strong> continues best-effort.
/// </para>
/// </remarks>
public sealed unsafe class PortAudioOutput : IAudioOutput, IAudioOutputChannelCapabilities, IClockedOutput, IFlushableOutput, IPlaybackClock, IAudioOutputPlaybackStats, IDisposable
{
    private readonly AudioFormat _format;
    private readonly int _deviceIndex;
    private readonly int _maxOutputChannels;
    private readonly double _suggestedLatency;
    private readonly nuint _framesPerBuffer;
    private readonly float[] _ringBuffer;
    private readonly int _ringMask;

    private long _writeIndex;
    private long _readIndex;
    private long _playedSamples;
    private long _droppedSamples;
    private long _underrunSamples;
    private long _callbackCount;
    private long _lastSubmitDropLogTicks;
    private int _callbackFaulted;
    private Exception? _callbackFaultException;

    private nint _stream;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.PortAudio.PortAudioOutput");
    private long _waitForCapacityWarnTicks;

    /// <summary>Pa_GetStreamTime at <see cref="_segmentPlayed0Samples"/> — set on first callback after Start/Flush.</summary>
    private double _segmentStreamT0;
    /// <summary><see cref="PlayedSamples"/> snapshot paired with <see cref="_segmentStreamT0"/>.</summary>
    private long _segmentPlayed0Samples;
    /// <summary>1 after first callback calibrates stream time vs played samples for this stream segment.</summary>
    private int _streamSmoothCalibrated;

    public AudioFormat Format => _format;
    public AudioOutputChannelCapabilities ChannelCapabilities =>
        new(CurrentChannels: _format.Channels, MinChannels: 1, MaxChannels: _maxOutputChannels,
            SupportsRuntimeChannelReconfigure: false);
    public bool IsRunning => Volatile.Read(ref _isRunning);
    public int DeviceIndex => _deviceIndex;

    /// <summary>Total frames (samples per channel) PortAudio has already played from this output. Monotonic across Stop/Start.</summary>
    public long PlayedSamples => Volatile.Read(ref _playedSamples);
    /// <summary>Approximate samples-per-channel currently sitting in the ring buffer.</summary>
    public int QueuedSamples => (int)((Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex)) / _format.Channels);
    public int CapacitySamples => _ringBuffer.Length / _format.Channels;
    /// <summary>Samples dropped on Submit because the ring buffer was full.</summary>
    public long DroppedSamples => Volatile.Read(ref _droppedSamples);
    /// <summary>Samples zeroed by the callback because the ring was empty.</summary>
    public long UnderrunSamples => Volatile.Read(ref _underrunSamples);
    /// <summary>How many times the PA callback has fired (debug).</summary>
    public long CallbackCount => Volatile.Read(ref _callbackCount);
    /// <summary>Non-zero if the native stream callback caught an exception (diagnostics only; never throws across native boundary).</summary>
    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    /// <summary>
    /// First exception caught in the PortAudio stream callback, if <see cref="CallbackFaulted"/> is true.
    /// Cleared when <see cref="Start"/> begins a new stream session. Never throws from the callback thread;
    /// retain only for inspection on another thread.
    /// </summary>
    public Exception? CallbackFaultException => Volatile.Read(ref _callbackFaultException);
    /// <summary>1 = PA reports stream active, 0 = inactive, negative = error/closed.</summary>
    public int StreamActive => _stream != nint.Zero ? (int)Native.Pa_IsStreamActive(_stream) : -1;

    /// <summary>PortAudio's stream clock — wall-clock seconds since the stream started.</summary>
    public double StreamTime => _stream != nint.Zero ? Native.Pa_GetStreamTime(_stream) : 0.0;

    /// <summary>
    /// <see cref="IPlaybackClock.ElapsedSinceStart"/>: monotonic playback time aligned with
    /// <see cref="PlayedSamples"/> but advanced with PortAudio's <c>Pa_GetStreamTime</c> between
    /// callbacks so the master clock is not stuck for an entire output buffer (~10–25 ms at 48 kHz).
    /// Falls back to sample counts when the stream is inactive or before the first callback.
    /// </summary>
    public TimeSpan ElapsedSinceStart
    {
        get
        {
            if (_stream != nint.Zero
                && (int)Native.Pa_IsStreamActive(_stream) == 1
                && Volatile.Read(ref _streamSmoothCalibrated) != 0)
            {
                Thread.MemoryBarrier();
                var st = Native.Pa_GetStreamTime(_stream);
                if (double.IsFinite(st))
                {
                    var elapsedSec = _segmentPlayed0Samples / (double)_format.SampleRate + (st - _segmentStreamT0);
                    if (elapsedSec < 0)
                        elapsedSec = 0;
                    return TimeSpan.FromSeconds(elapsedSec);
                }
            }

            return TimeSpan.FromSeconds(PlayedSamples / (double)_format.SampleRate);
        }
    }

    /// <summary><see cref="IPlaybackClock.IsAdvancing"/>: true when the PA stream is open and reporting active.</summary>
    public bool IsAdvancing => _stream != nint.Zero && (int)Native.Pa_IsStreamActive(_stream) == 1;

    /// <summary>
    /// <see cref="IFlushableOutput.Flush"/>: aborts the PortAudio stream
    /// (discards anything in the OS buffer), zeroes the ring counters, and
    /// restarts the stream. There is a brief audible gap (typically a few ms);
    /// in exchange the very next <see cref="Submit"/> plays without first
    /// hearing whatever was queued before <see cref="Flush"/>.
    /// </summary>
    public void Flush()
    {
        if (_disposed || !Volatile.Read(ref _isRunning) || _stream == nint.Zero) return;
        Trace.LogDebug("Flush: aborting + restarting stream (queued={Queued}f played={Played}f)",
            QueuedSamples, Volatile.Read(ref _playedSamples));
        Native.Pa_AbortStream(_stream);
        // Reset ring positions so the queue starts empty post-restart;
        // _playedSamples is preserved (lifetime stat / monotonic clock).
        Volatile.Write(ref _writeIndex, 0);
        Volatile.Write(ref _readIndex, 0);
        Interlocked.Exchange(ref _underrunSamples, 0);
        // Reset BEFORE Pa_StartStream so the first new-segment callback re-anchors
        // _segmentStreamT0 / _segmentPlayed0Samples to this segment. Writing after
        // StartStream races the callback and leaves ElapsedSinceStart anchored to
        // the previous segment until the next stray callback happens to re-trip
        // calibration.
        Volatile.Write(ref _streamSmoothCalibrated, 0);
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
            _maxOutputChannels = Math.Max(1, devInfo.maxOutputChannels);
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

        Interlocked.Exchange(ref _callbackFaultException, null);
        Volatile.Write(ref _callbackFaulted, 0);

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

        Volatile.Write(ref _streamSmoothCalibrated, 0);
        Volatile.Write(ref _isRunning, true);
        Trace.LogDebug("Start: device={Device} channels={Ch} rate={Rate}Hz framesPerBuffer={Fpb} suggestedLatency={Latency}s ringCap={RingCapFrames}f targetQueue={TargetFrames}f",
            _deviceIndex, _format.Channels, _format.SampleRate, _framesPerBuffer, _suggestedLatency,
            _ringBuffer.Length / _format.Channels, TargetQueueSamples);
    }

    public void Stop()
    {
        if (!Volatile.Read(ref _isRunning)) return;
        Trace.LogDebug("Stop: device={Device} played={Played}f underrun={Underrun}f dropped={Dropped}f callbacks={Callbacks}",
            _deviceIndex, Volatile.Read(ref _playedSamples), Volatile.Read(ref _underrunSamples),
            Volatile.Read(ref _droppedSamples), Volatile.Read(ref _callbackCount));
        try
        {
            if (_stream != nint.Zero)
            {
                Native.Pa_StopStream(_stream);
                Native.Pa_CloseStream(_stream);
                _stream = nint.Zero;
            }
        }
        finally
        {
            if (_selfHandle.IsAllocated) _selfHandle.Free();
            Volatile.Write(ref _isRunning, false);
            Volatile.Write(ref _streamSmoothCalibrated, 0);
            Volatile.Write(ref _writeIndex, 0);
            Volatile.Write(ref _readIndex, 0);
        }
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
        {
            Interlocked.Add(ref _droppedSamples, dropped);
            var now = Environment.TickCount64;
            var prev = Volatile.Read(ref _lastSubmitDropLogTicks);
            if (now - prev >= 2000 || prev == 0)
            {
                if (Interlocked.CompareExchange(ref _lastSubmitDropLogTicks, now, prev) == prev)
                {
                    var frames = dropped / _format.Channels;
                    var total = Volatile.Read(ref _droppedSamples);
                    MediaDiagnostics.LogWarning(
                        $"PortAudioOutput: ring full — dropped {dropped} floats (~{frames} frames this Submit); " +
                        $"total DroppedSamples={total}. Prefill / TargetQueueSamples / stream-not-started windows can cause bursts.");
                }
            }
        }
    }

    /// <summary>
    /// Fills the ring from <paramref name="source"/> before <see cref="Start"/> (and before
    /// <see cref="AudioRouter.Start"/>) so the first callback has PCM ready. Optionally mirrors the
    /// same packed floats into <paramref name="mirrorPackedFloats"/> (same <see cref="AudioFormat"/>).
    /// </summary>
    /// <remarks>
    /// Target queue depth defaults to <c>max(sampleRate/10, chunkSamples×4)</c> samples per channel
    /// (same heuristic as the smoke tools). Stops if the ring stops accepting data (full / drops) so
    /// an oversized target cannot spin forever.
    /// </remarks>
    public void PrefillFrom(
        IAudioSource source,
        TimeSpan timeout,
        int chunkSamples,
        IAudioOutput? mirrorPackedFloats = null,
        int? targetQueuedSamplesOverride = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Format != _format)
            throw new ArgumentException("Source format must match this output's format.", nameof(source));
        if (mirrorPackedFloats is not null && mirrorPackedFloats.Format != _format)
            throw new ArgumentException("Mirror output format must match this output's format.", nameof(mirrorPackedFloats));
        if (chunkSamples < 16)
            throw new ArgumentOutOfRangeException(nameof(chunkSamples));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var targetQueued = targetQueuedSamplesOverride ?? Math.Max(_format.SampleRate / 10, chunkSamples * 4);
        var ch = _format.Channels;
        var bufFloats = Math.Min(65536, Math.Max(chunkSamples * ch * 8, 8192 * ch));
        var buf = System.Buffers.ArrayPool<float>.Shared.Rent(bufFloats);
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (QueuedSamples < targetQueued && DateTime.UtcNow < deadline)
            {
                var read = source.ReadInto(buf.AsSpan(0, bufFloats));
                if (read == 0) break;
                var span = buf.AsSpan(0, read);
                var q0 = QueuedSamples;
                Submit(span);
                mirrorPackedFloats?.Submit(span);
                if (QueuedSamples == q0 && read > 0) break;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// <see cref="IClockedOutput"/> implementation: paces the router against the
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
        if (!Volatile.Read(ref _isRunning))
        {
            if (Trace.IsEnabled(LogLevel.Trace))
                Trace.LogTrace("WaitForCapacity: stream not running yet — returning ready (chunk={Chunk})", chunkSamples);
            return !token.IsCancellationRequested;
        }

        var target = TargetQueueSamples;
        var startTicks = Environment.TickCount64;
        var deadlineTicks = startTicks + (long)TimeSpan.FromSeconds(5).TotalMilliseconds;
        while (!token.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= deadlineTicks)
            {
                var now = Environment.TickCount64;
                var prev = Volatile.Read(ref _waitForCapacityWarnTicks);
                if (now - prev >= 2000 || prev == 0)
                {
                    if (Interlocked.CompareExchange(ref _waitForCapacityWarnTicks, now, prev) == prev)
                        Trace.LogWarning("WaitForCapacity: timed out after 5s (queued={Queued}f target={Target}f played={Played}f underrun={Underrun}f cbCount={CB} streamActive={Active}) — router pacing will stall",
                            QueuedSamples, target, Volatile.Read(ref _playedSamples), Volatile.Read(ref _underrunSamples),
                            Volatile.Read(ref _callbackCount), StreamActive);
                }
                return false;
            }

            var queued = QueuedSamples;
            if (queued + chunkSamples <= target) return true;

            // Estimate how long until the device drains the excess. Add a 1ms
            // floor so we don't spin when we're only marginally over.
            var excessSamples = queued + chunkSamples - target;
            var waitMs = Math.Max(1, (int)Math.Ceiling(1000.0 * excessSamples / _format.SampleRate));
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
        PortAudioOutput? self = null;
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not PortAudioOutput s)
                return (int)PaStreamCallbackResult.paAbort;
            self = s;

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
                // _playedSamples is the lifetime per-channel frame counter
                // (preserved across Stop/Start) — separate from the ring read
                // pointer, which may reset on Stop or Flush.
                Interlocked.Add(ref self._playedSamples, toRead / self._format.Channels);
            }

            if (toRead < totalFloats)
            {
                output[toRead..].Clear();
                var underrunFrames = (totalFloats - toRead) / self._format.Channels;
                if (underrunFrames > 0)
                    Interlocked.Add(ref self._underrunSamples, underrunFrames);
            }

            if (Volatile.Read(ref self._streamSmoothCalibrated) == 0 && timeInfo != nint.Zero)
            {
                // PortAudio forbids Pa_GetStreamTime inside the stream callback; timeInfo is
                // synchronized with that clock (see portaudio.h PaStreamCallbackTimeInfo).
                var ti = *(PaStreamCallbackTimeInfo*)timeInfo;
                var st = ti.currentTime;
                if (double.IsFinite(st))
                {
                    var playedNow = Volatile.Read(ref self._playedSamples);
                    self._segmentPlayed0Samples = playedNow;
                    self._segmentStreamT0 = st;
                    Thread.MemoryBarrier();
                    Volatile.Write(ref self._streamSmoothCalibrated, 1);
                }
            }

            return (int)PaStreamCallbackResult.paContinue;
        }
        catch (Exception ex)
        {
            // Throwing across the unmanaged boundary would crash the process.
            if (self is not null)
            {
                Interlocked.CompareExchange(ref self._callbackFaultException, ex, null);
                Volatile.Write(ref self._callbackFaulted, 1);
            }

            return (int)PaStreamCallbackResult.paAbort;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(Stop, "PortAudioOutput.Dispose: Stop");
        MediaDiagnostics.SwallowDisposeErrors(PortAudioRuntime.Release, "PortAudioOutput.Dispose: PortAudioRuntime.Release");
    }
}
