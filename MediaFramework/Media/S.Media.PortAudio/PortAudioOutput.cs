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

    /// <summary>
    /// Serializes native stream-lifecycle transitions so two managed threads can never call
    /// Pa_AbortStream / Pa_StopStream / Pa_StartStream on the same handle concurrently — which
    /// wedges the PortAudio backend. <see cref="Submit"/> runs on the router's per-output drainer
    /// thread while <see cref="WaitForCapacity"/> runs on the router's run-loop thread, and right
    /// after a <see cref="Flush"/> both can reach <see cref="EnsureStreamRunningAfterFlush"/>.
    /// </summary>
    private readonly Lock _streamLifecycleGate = new();

    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.PortAudio.PortAudioOutput");
    private long _waitForCapacityWarnTicks;

    /// <summary>Pa_GetStreamTime at <see cref="_segmentPlayed0Samples"/> — set on first callback after Start/Flush.</summary>
    private double _segmentStreamT0;
    /// <summary><see cref="PlayedSamples"/> snapshot paired with <see cref="_segmentStreamT0"/>.</summary>
    private long _segmentPlayed0Samples;
    /// <summary>1 after first callback calibrates stream time vs played samples for this stream segment.</summary>
    private int _streamSmoothCalibrated;
    /// <summary>
    /// <see cref="PlayedSamples"/> baseline for <see cref="IPlaybackClock"/> — reset on each
    /// <see cref="Start"/>/<see cref="Flush"/> so pause/resume drift math sees segment-local time,
    /// not lifetime output counters that keep advancing on underrun silence.
    /// </summary>
    private long _playbackEpochSamples;
    /// <summary>1 after <see cref="Flush"/> stops the PA stream until the next producer call restarts it.</summary>
    private int _streamStoppedAfterFlush;

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
            var epoch = Volatile.Read(ref _playbackEpochSamples);
            var playedNow = Volatile.Read(ref _playedSamples) - epoch;
            if (playedNow < 0) playedNow = 0;
            var sampleElapsedSec = playedNow / (double)_format.SampleRate;

            if (_stream != nint.Zero
                && (int)Native.Pa_IsStreamActive(_stream) == 1
                && Volatile.Read(ref _streamSmoothCalibrated) != 0)
            {
                Thread.MemoryBarrier();
                var st = Native.Pa_GetStreamTime(_stream);
                if (double.IsFinite(st))
                {
                    // _segmentPlayed0Samples is already segment-local (played - epoch at calibration).
                    var segmentPlayed0 = Volatile.Read(ref _segmentPlayed0Samples);
                    if (segmentPlayed0 < 0) segmentPlayed0 = 0;
                    var streamElapsedSec = segmentPlayed0 / (double)_format.SampleRate + (st - _segmentStreamT0);
                    if (streamElapsedSec < 0)
                        streamElapsedSec = 0;
                    // After Pa_AbortStream + Pa_StartStream, Pa_GetStreamTime can stall while callbacks
                    // still drain the ring — never let the master clock lag behind sample progress.
                    var elapsedSec = Math.Max(sampleElapsedSec, streamElapsedSec);
                    return TimeSpan.FromSeconds(elapsedSec);
                }
            }

            return TimeSpan.FromSeconds(sampleElapsedSec);
        }
    }

    /// <summary><see cref="IPlaybackClock.IsAdvancing"/>: true when the PA stream is open and reporting active.</summary>
    public bool IsAdvancing => _stream != nint.Zero && (int)Native.Pa_IsStreamActive(_stream) == 1;

    /// <summary>
    /// <see cref="IFlushableOutput.Flush"/>: aborts the PortAudio stream
    /// (discards anything in the OS buffer), zeroes the ring counters, re-anchors
    /// <see cref="ElapsedSinceStart"/> to zero for this segment, and stops the
    /// stream until the next <see cref="Submit"/>/<see cref="WaitForCapacity"/>.
    /// </summary>
    public void Flush()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Flush", slowWarningMs: 250);
        if (_disposed || !Volatile.Read(ref _isRunning) || _stream == nint.Zero) return;
        lock (_streamLifecycleGate)
        {
            // Re-check under the gate: a concurrent EnsureStreamRunningAfterFlush may be mid-restart,
            // or Stop/Dispose may have closed the stream while we waited for the gate.
            if (_disposed || !Volatile.Read(ref _isRunning) || _stream == nint.Zero) return;
            Trace.LogDebug("Flush: aborting stream (queued={Queued}f target={Target}f played={Played}f epoch={Epoch}f elapsed={Elapsed} active={Active})",
                QueuedSamples, TargetQueueSamples, Volatile.Read(ref _playedSamples), Volatile.Read(ref _playbackEpochSamples),
                ElapsedSinceStart, StreamActive);
            Native.Pa_AbortStream(_stream);
            // Reset ring positions so the queue starts empty; _playedSamples is preserved
            // (lifetime stat) but _playbackEpochSamples re-anchors IPlaybackClock to zero.
            Volatile.Write(ref _writeIndex, 0);
            Volatile.Write(ref _readIndex, 0);
            Interlocked.Exchange(ref _underrunSamples, 0);
            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _streamSmoothCalibrated, 0);
            // Abort stops the stream; do not restart until the next producer call so
            // underrun silence during pause cannot advance ElapsedSinceStart.
            Volatile.Write(ref _streamStoppedAfterFlush, 1);
            timing?.SetOutcome($"device={_deviceIndex} queued={QueuedSamples}");
        }
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
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Start", slowWarningMs: 1000);
        lock (_streamLifecycleGate)
        {
            if (_isRunning)
            {
                timing?.SetOutcome($"device={_deviceIndex} already-running");
                return;
            }

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

            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _streamSmoothCalibrated, 0);
            Volatile.Write(ref _streamStoppedAfterFlush, 0);
            Volatile.Write(ref _isRunning, true);
            Trace.LogDebug("Start: device={Device} channels={Ch} rate={Rate}Hz framesPerBuffer={Fpb} suggestedLatency={Latency}s ringCap={RingCapFrames}f targetQueue={TargetFrames}f",
                _deviceIndex, _format.Channels, _format.SampleRate, _framesPerBuffer, _suggestedLatency,
                _ringBuffer.Length / _format.Channels, TargetQueueSamples);
            timing?.SetOutcome($"device={_deviceIndex} format={_format} ring={_ringBuffer.Length / _format.Channels} target={TargetQueueSamples}");
        }
    }

    public void Stop()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Stop", slowWarningMs: 1000);
        lock (_streamLifecycleGate)
        {
            if (!Volatile.Read(ref _isRunning))
            {
                timing?.SetOutcome($"device={_deviceIndex} not-running");
                return;
            }
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
                Volatile.Write(ref _streamStoppedAfterFlush, 0);
                Volatile.Write(ref _writeIndex, 0);
                Volatile.Write(ref _readIndex, 0);
            }
            timing?.SetOutcome($"device={_deviceIndex} played={Volatile.Read(ref _playedSamples)} underrun={Volatile.Read(ref _underrunSamples)} dropped={Volatile.Read(ref _droppedSamples)}");
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
        EnsureStreamRunningAfterFlush();
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
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.PrefillFrom", slowWarningMs: 500);
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
        timing?.SetOutcome($"device={_deviceIndex} queued={QueuedSamples} target={targetQueued}");
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

        EnsureStreamRunningAfterFlush();

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

    private void EnsureStreamRunningAfterFlush()
    {
        if (Volatile.Read(ref _streamStoppedAfterFlush) == 0 || _stream == nint.Zero)
            return;

        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.RestartAfterFlush", slowWarningMs: 250);
        lock (_streamLifecycleGate)
        {
            // Re-check under the gate. Both the drainer (Submit) and run-loop (WaitForCapacity) threads
            // observe _streamStoppedAfterFlush==1 after a Flush and would otherwise each issue their own
            // Pa_StopStream + Pa_StartStream on the same handle; doing that concurrently wedges the native
            // backend (the deadlock this gate prevents). Whichever thread wins clears the flag, so the
            // other returns here without touching the stream.
            if (Volatile.Read(ref _streamStoppedAfterFlush) == 0 || _stream == nint.Zero)
                return;

            Trace.LogDebug("EnsureStreamRunningAfterFlush: restarting PA stream (played={Played}f epoch={Epoch}f)",
                Volatile.Read(ref _playedSamples), Volatile.Read(ref _playbackEpochSamples));
            Volatile.Write(ref _streamSmoothCalibrated, 0);
            // Abort leaves the stream stopped; Stop+Start is more reliable than Start alone on some
            // backends when rebinding stream time after a flush segment reset.
            var err = Native.Pa_StopStream(_stream);
            if (err != PaError.paNoError && err != PaError.paStreamIsStopped)
                PortAudioException.ThrowIfError(err, nameof(Native.Pa_StopStream));
            err = Native.Pa_StartStream(_stream);
            if (err != PaError.paNoError)
                PortAudioException.ThrowIfError(err, nameof(Native.Pa_StartStream));
            Volatile.Write(ref _streamStoppedAfterFlush, 0);
            timing?.SetOutcome($"device={_deviceIndex} active={StreamActive}");
        }
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
                    self._segmentPlayed0Samples = playedNow - Volatile.Read(ref self._playbackEpochSamples);
                    if (self._segmentPlayed0Samples < 0) self._segmentPlayed0Samples = 0;
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
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioOutput.Dispose", slowWarningMs: 1000);
        if (_disposed)
        {
            timing?.SetOutcome($"device={_deviceIndex} already-disposed");
            return;
        }
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(Stop, "PortAudioOutput.Dispose: Stop");
        MediaDiagnostics.SwallowDisposeErrors(PortAudioRuntime.Release, "PortAudioOutput.Dispose: PortAudioRuntime.Release");
        timing?.SetOutcome($"device={_deviceIndex} played={Volatile.Read(ref _playedSamples)} dropped={Volatile.Read(ref _droppedSamples)} underrun={Volatile.Read(ref _underrunSamples)}");
    }
}
