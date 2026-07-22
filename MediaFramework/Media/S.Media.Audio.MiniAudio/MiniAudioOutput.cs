using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace S.Media.Audio.MiniAudio;

public sealed unsafe class MiniAudioOutput :
    IAudioOutput,
    IAudioOutputChannelCapabilities,
    IClockedOutput,
    IFlushableOutput,
    IPlaybackClock,
    IAudioOutputPlaybackStats,
    IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.MiniAudio.MiniAudioOutput");
    private static readonly delegate* unmanaged[Cdecl]<nint, float*, float*, uint, void> CallbackPtr = &Callback;

    private readonly AudioFormat _format;
    private readonly string? _deviceId;
    private readonly uint _periodSizeFrames;
    private readonly FrameAlignedFloatRing _ring;
    private readonly Lock _deviceLifecycleGate = new();

    private long _playedSamples;
    private long _droppedSamples;
    private long _underrunSamples;
    private long _callbackCount;
    private long _playbackEpochSamples;
    private long _lastSubmitDropLogTicks;
    private int _deviceStoppedAfterFlush;
    private int _callbackFaulted;
    private Exception? _callbackFaultException;
    private nint _device;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    public MiniAudioOutput(
        AudioFormat format,
        string? deviceId = null,
        int framesPerBuffer = 0,
        int ringCapacityFrames = 16384)
    {
        format.Validate(nameof(format));
        if (framesPerBuffer < 0) throw new ArgumentOutOfRangeException(nameof(framesPerBuffer));
        if (ringCapacityFrames < 64) throw new ArgumentOutOfRangeException(nameof(ringCapacityFrames), "must be >= 64");

        _format = format;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        _periodSizeFrames = (uint)framesPerBuffer;

        _ring = new FrameAlignedFloatRing(format.Channels, (long)ringCapacityFrames * format.Channels);
        TargetQueueSamples = _ring.CapacityFrames / 2;
    }

    public AudioFormat Format => _format;

    public AudioOutputChannelCapabilities ChannelCapabilities =>
        AudioOutputChannelCapabilities.Fixed(_format.Channels);

    public bool IsRunning => Volatile.Read(ref _isRunning);

    public int DeviceState => _device != nint.Zero ? MiniAudioNative.DeviceGetState(_device) : 0;

    public long PlayedSamples => Volatile.Read(ref _playedSamples);

    public int QueuedSamples => _ring.BufferedFrames;

    public int CapacitySamples => _ring.CapacityFrames;

    public long DroppedSamples => Volatile.Read(ref _droppedSamples);

    public long UnderrunSamples => Volatile.Read(ref _underrunSamples);

    public long CallbackCount => Volatile.Read(ref _callbackCount);

    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    public Exception? CallbackFaultException => Volatile.Read(ref _callbackFaultException);

    public int TargetQueueSamples { get; set; }

    public TimeSpan ElapsedSinceStart
    {
        get
        {
            var samples = Volatile.Read(ref _playedSamples) - Volatile.Read(ref _playbackEpochSamples);
            if (samples < 0) samples = 0;
            return TimeSpan.FromSeconds(samples / (double)_format.SampleRate);
        }
    }

    public bool IsAdvancing =>
        Volatile.Read(ref _isRunning)
        && Volatile.Read(ref _deviceStoppedAfterFlush) == 0
        && _device != nint.Zero
        && MiniAudioNative.DeviceIsStarted(_device) != 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Start", slowWarningMs: 1000);
        lock (_deviceLifecycleGate)
        {
            if (_isRunning)
            {
                timing?.SetOutcome("already-running");
                return;
            }

            Interlocked.Exchange(ref _callbackFaultException, null);
            Volatile.Write(ref _callbackFaulted, 0);

            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            var deviceIdBytes = MiniAudioNative.ToUtf8NullTerminated(_deviceId);
            fixed (byte* deviceIdPtr = deviceIdBytes)
            {
                var createResult = MiniAudioNative.DeviceCreate(
                    (int)MiniAudioDeviceType.Playback,
                    deviceIdPtr,
                    (uint)_format.SampleRate,
                    (uint)_format.Channels,
                    _periodSizeFrames,
                    CallbackPtr,
                    GCHandle.ToIntPtr(_selfHandle),
                    out _device);
                if (createResult != MiniAudioNative.Success)
                {
                    _selfHandle.Free();
                    MiniAudioException.ThrowIfError(createResult, "ma_device_init(playback)");
                }
            }

            var startResult = MiniAudioNative.DeviceStart(_device);
            if (startResult != MiniAudioNative.Success)
            {
                MiniAudioNative.DeviceDestroy(_device);
                _device = nint.Zero;
                _selfHandle.Free();
                MiniAudioException.ThrowIfError(startResult, "ma_device_start(playback)");
            }

            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _deviceStoppedAfterFlush, 0);
            Volatile.Write(ref _isRunning, true);
            Trace.LogDebug(
                "Start: channels={Channels} rate={Rate}Hz period={PeriodFrames} ringCap={RingCapFrames}f targetQueue={TargetFrames}f",
                _format.Channels,
                _format.SampleRate,
                _periodSizeFrames,
                CapacitySamples,
                TargetQueueSamples);
            timing?.SetOutcome($"format={_format} ring={CapacitySamples} target={TargetQueueSamples}");
        }
    }

    public void Stop()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Stop", slowWarningMs: 1000);
        lock (_deviceLifecycleGate)
        {
            if (!Volatile.Read(ref _isRunning))
            {
                timing?.SetOutcome("not-running");
                return;
            }

            var device = _device;
            var stopResult = MiniAudioNative.Success;
            try
            {
                if (device != nint.Zero)
                {
                    stopResult = MiniAudioNative.DeviceStop(device);
                    MiniAudioNative.DeviceDestroy(device);
                    _device = nint.Zero;
                }
            }
            finally
            {
                if (_selfHandle.IsAllocated)
                    _selfHandle.Free();
                Volatile.Write(ref _isRunning, false);
                Volatile.Write(ref _deviceStoppedAfterFlush, 0);
                _ring.Clear();
            }

            MiniAudioException.ThrowIfError(stopResult, "ma_device_stop(playback)");
            timing?.SetOutcome(
                $"played={Volatile.Read(ref _playedSamples)} underrun={Volatile.Read(ref _underrunSamples)} dropped={Volatile.Read(ref _droppedSamples)}");
        }
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDeviceRunningAfterFlush();
        if (packedSamples.Length % _format.Channels != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {_format.Channels}",
                nameof(packedSamples));

        var toWrite = _ring.Write(packedSamples);

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
                    Trace.LogWarning(
                        "MiniAudioOutput: ring full; dropped {DroppedFloats} floats (~{Frames} frames this Submit); total DroppedSamples={Total}",
                        dropped,
                        frames,
                        Volatile.Read(ref _droppedSamples));
                }
            }
        }
    }

    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        if (chunkSamples <= 0) return !token.IsCancellationRequested;
        if (!Volatile.Read(ref _isRunning))
            return !token.IsCancellationRequested;

        EnsureDeviceRunningAfterFlush();

        var target = TargetQueueSamples;
        var deadlineTicks = Environment.TickCount64 + (long)TimeSpan.FromSeconds(5).TotalMilliseconds;
        while (!token.IsCancellationRequested)
        {
            if (Environment.TickCount64 >= deadlineTicks)
                return false;

            if (QueuedSamples + chunkSamples <= target)
                return true;

            var excessSamples = QueuedSamples + chunkSamples - target;
            var waitMs = Math.Max(1, (int)Math.Ceiling(1000.0 * excessSamples / _format.SampleRate));
            if (token.WaitHandle.WaitOne(waitMs))
                return false;
        }

        return false;
    }

    public void Flush()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Flush", slowWarningMs: 250);
        if (_disposed || !Volatile.Read(ref _isRunning) || _device == nint.Zero) return;
        lock (_deviceLifecycleGate)
        {
            if (_disposed || !Volatile.Read(ref _isRunning) || _device == nint.Zero) return;
            MiniAudioException.ThrowIfError(MiniAudioNative.DeviceStop(_device), "ma_device_stop(playback flush)");
            _ring.Clear();
            Interlocked.Exchange(ref _underrunSamples, 0);
            Volatile.Write(ref _playbackEpochSamples, Volatile.Read(ref _playedSamples));
            Volatile.Write(ref _deviceStoppedAfterFlush, 1);
            timing?.SetOutcome($"queued={QueuedSamples}");
        }
    }

    internal int TryDrainForTest(Span<float> destination) => _ring.Read(destination);

    private void EnsureDeviceRunningAfterFlush()
    {
        if (Volatile.Read(ref _deviceStoppedAfterFlush) == 0 || _device == nint.Zero)
            return;

        lock (_deviceLifecycleGate)
        {
            if (Volatile.Read(ref _deviceStoppedAfterFlush) == 0 || _device == nint.Zero)
                return;

            MiniAudioException.ThrowIfError(MiniAudioNative.DeviceStart(_device), "ma_device_start(playback after flush)");
            Volatile.Write(ref _deviceStoppedAfterFlush, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(nint userData, float* outputBuffer, float* inputBuffer, uint frameCount)
    {
        _ = inputBuffer;
        MiniAudioOutput? self = null;
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not MiniAudioOutput s)
                return;
            self = s;

            Interlocked.Increment(ref self._callbackCount);
            var totalFloats = checked((int)frameCount * self._format.Channels);
            if (outputBuffer is null)
            {
                Interlocked.CompareExchange(
                    ref self._callbackFaultException,
                    new InvalidOperationException("miniaudio playback callback received a null output buffer."),
                    null);
                Volatile.Write(ref self._callbackFaulted, 1);
                return;
            }

            var output = new Span<float>(outputBuffer, totalFloats);
            var toRead = self._ring.Read(output);

            if (toRead < totalFloats)
            {
                output[toRead..].Clear();
                var underrunFrames = (totalFloats - toRead) / self._format.Channels;
                if (underrunFrames > 0)
                    Interlocked.Add(ref self._underrunSamples, underrunFrames);
            }

            // Count only CONSUMED frames, matching PortAudioOutput: _playedSamples drives
            // ElapsedSinceStart (the A/V master clock when this output paces playback), and
            // counting underrun silence as played skipped the clock forward past the submitted
            // content on every dropout - permanent lip-sync drift on the miniaudio backend.
            if (toRead > 0)
                Interlocked.Add(ref self._playedSamples, toRead / self._format.Channels);
        }
        catch (Exception ex)
        {
            if (self is not null)
            {
                Interlocked.CompareExchange(ref self._callbackFaultException, ex, null);
                Volatile.Write(ref self._callbackFaulted, 1);
            }

            if (outputBuffer is not null && self is not null)
                new Span<float>(outputBuffer, checked((int)frameCount * self._format.Channels)).Clear();
        }
    }

    public void Dispose()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioOutput.Dispose", slowWarningMs: 1000);
        if (_disposed)
        {
            timing?.SetOutcome("already-disposed");
            return;
        }

        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(Stop, "MiniAudioOutput.Dispose: Stop");
        timing?.SetOutcome(
            $"played={Volatile.Read(ref _playedSamples)} dropped={Volatile.Read(ref _droppedSamples)} underrun={Volatile.Read(ref _underrunSamples)}");
    }
}
