using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.MiniAudio;

public sealed unsafe class MiniAudioInput : IAudioSource, IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.MiniAudio.MiniAudioInput");
    private static readonly delegate* unmanaged[Cdecl]<nint, float*, float*, uint, void> CallbackPtr = &Callback;

    private readonly AudioFormat _format;
    private readonly string? _deviceId;
    private readonly uint _periodSizeFrames;
    private readonly float[] _ringBuffer;
    private readonly int _ringMask;
    private readonly Lock _deviceLifecycleGate = new();

    private long _writeIndex;
    private long _readIndex;
    private long _samplesEmitted;
    private long _overflowSamples;
    private int _callbackFaulted;
    private Exception? _callbackFaultException;
    private nint _device;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    public MiniAudioInput(
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

        var capacityFloats = ringCapacityFrames * format.Channels;
        var rounded = 1;
        while (rounded < capacityFloats) rounded <<= 1;
        _ringBuffer = new float[rounded];
        _ringMask = rounded - 1;
    }

    public AudioFormat Format => _format;

    public bool IsRunning => Volatile.Read(ref _isRunning);

    public bool IsExhausted => _disposed;

    public int AvailableSamples => (int)((Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex)) / _format.Channels);

    public int CapacitySamples => _ringBuffer.Length / _format.Channels;

    public long OverflowSamples => Volatile.Read(ref _overflowSamples);

    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    public Exception? CallbackFaultException => Volatile.Read(ref _callbackFaultException);

    public int DeviceState => _device != nint.Zero ? MiniAudioNative.DeviceGetState(_device) : 0;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioInput.Start", slowWarningMs: 1000);
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
                    (int)MiniAudioDeviceType.Capture,
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
                    MiniAudioException.ThrowIfError(createResult, "sma_device_create(capture)");
                }
            }

            var startResult = MiniAudioNative.DeviceStart(_device);
            if (startResult != MiniAudioNative.Success)
            {
                MiniAudioNative.DeviceDestroy(_device);
                _device = nint.Zero;
                _selfHandle.Free();
                MiniAudioException.ThrowIfError(startResult, "sma_device_start(capture)");
            }

            Volatile.Write(ref _isRunning, true);
            Trace.LogDebug(
                "Start: channels={Channels} rate={Rate}Hz period={PeriodFrames} ringCap={RingCapFrames}f",
                _format.Channels,
                _format.SampleRate,
                _periodSizeFrames,
                CapacitySamples);
            timing?.SetOutcome($"format={_format} ring={CapacitySamples}");
        }
    }

    public void Stop()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioInput.Stop", slowWarningMs: 1000);
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
            }

            MiniAudioException.ThrowIfError(stopResult, "sma_device_stop(capture)");
            timing?.SetOutcome($"emitted={Volatile.Read(ref _samplesEmitted)} overflow={Volatile.Read(ref _overflowSamples)}");
        }
    }

    public int ReadInto(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length % _format.Channels != 0)
            throw new ArgumentException(
                $"destination length {destination.Length} is not a multiple of channel count {_format.Channels}",
                nameof(destination));

        var read = Volatile.Read(ref _readIndex);
        var write = Volatile.Read(ref _writeIndex);
        var available = (int)(write - read);
        var toRead = Math.Min(destination.Length, available);
        if (toRead == 0) return 0;

        var startIdx = (int)(read & _ringMask);
        var firstChunk = Math.Min(toRead, _ringBuffer.Length - startIdx);
        _ringBuffer.AsSpan(startIdx, firstChunk).CopyTo(destination);
        if (firstChunk < toRead)
            _ringBuffer.AsSpan(0, toRead - firstChunk).CopyTo(destination[firstChunk..]);
        Volatile.Write(ref _readIndex, read + toRead);
        Interlocked.Add(ref _samplesEmitted, toRead / _format.Channels);
        return toRead;
    }

    public void RebaseToLatest(TimeSpan keepBuffered = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (keepBuffered <= TimeSpan.Zero) keepBuffered = TimeSpan.FromMilliseconds(100);
        if (keepBuffered < TimeSpan.FromMilliseconds(20)) keepBuffered = TimeSpan.FromMilliseconds(20);

        var keepFrames = Math.Max(0, (int)(keepBuffered.TotalSeconds * _format.SampleRate));
        var keepFloats = checked(keepFrames * _format.Channels);

        var write = Volatile.Read(ref _writeIndex);
        var read = Volatile.Read(ref _readIndex);
        var buffered = (int)(write - read);
        if (buffered <= keepFloats) return;

        Volatile.Write(ref _readIndex, read + buffered - keepFloats);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Callback(nint userData, float* outputBuffer, float* inputBuffer, uint frameCount)
    {
        _ = outputBuffer;
        MiniAudioInput? self = null;
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not MiniAudioInput s)
                return;
            self = s;

            if (inputBuffer is null)
            {
                Interlocked.CompareExchange(
                    ref self._callbackFaultException,
                    new InvalidOperationException("miniaudio capture callback received a null input buffer."),
                    null);
                Volatile.Write(ref self._callbackFaulted, 1);
                return;
            }

            var totalFloats = checked((int)frameCount * self._format.Channels);
            var input = new ReadOnlySpan<float>(inputBuffer, totalFloats);

            var write = Volatile.Read(ref self._writeIndex);
            var read = Volatile.Read(ref self._readIndex);
            var freeFloats = self._ringBuffer.Length - (int)(write - read);
            var toWrite = Math.Min(totalFloats, freeFloats);

            if (toWrite > 0)
            {
                var startIdx = (int)(write & self._ringMask);
                var firstChunk = Math.Min(toWrite, self._ringBuffer.Length - startIdx);
                input[..firstChunk].CopyTo(self._ringBuffer.AsSpan(startIdx));
                if (firstChunk < toWrite)
                    input.Slice(firstChunk, toWrite - firstChunk).CopyTo(self._ringBuffer.AsSpan(0));
                Volatile.Write(ref self._writeIndex, write + toWrite);
            }

            if (toWrite < totalFloats)
                Interlocked.Add(ref self._overflowSamples, totalFloats - toWrite);
        }
        catch (Exception ex)
        {
            if (self is not null)
            {
                Interlocked.CompareExchange(ref self._callbackFaultException, ex, null);
                Volatile.Write(ref self._callbackFaulted, 1);
            }
        }
    }

    public void Dispose()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "MiniAudioInput.Dispose", slowWarningMs: 1000);
        if (_disposed)
        {
            timing?.SetOutcome("already-disposed");
            return;
        }

        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(Stop, "MiniAudioInput.Dispose: Stop");
        timing?.SetOutcome($"emitted={Volatile.Read(ref _samplesEmitted)} overflow={Volatile.Read(ref _overflowSamples)}");
    }
}
