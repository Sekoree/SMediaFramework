using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.PortAudio;

/// <summary>
/// Audio source backed by a PortAudio input stream. PortAudio's audio-thread
/// callback writes packed float32 samples into an SPSC ring buffer; consumers
/// pull frames via <see cref="TryReadFrame"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Dispose"/> calls <see cref="Stop"/> then <see cref="PortAudioRuntime.Release"/>; each step is wrapped so <strong>Debug</strong> builds log via <see cref="MediaDiagnostics.LogError"/> while <strong>Release</strong> continues best-effort.
/// </para>
/// </remarks>
public sealed unsafe class PortAudioInput : IAudioSource, IDisposable
{
    private static readonly delegate* unmanaged[Cdecl]<nint, nint, nuint, nint, PaStreamCallbackFlags, nint, int>
        CallbackPtr = &Callback;

    private readonly AudioFormat _format;
    private readonly int _deviceIndex;
    private readonly double _suggestedLatency;
    private readonly nuint _framesPerBuffer;
    private readonly float[] _ringBuffer;
    private readonly int _ringMask;

    private long _writeIndex;
    private long _readIndex;
    private long _samplesEmitted;
    private long _overflowSamples;
    private int _callbackFaulted;
    private Exception? _callbackFaultException;
    private int _streamInactiveDetected;

    private nint _stream;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    public AudioFormat Format => _format;
    public bool IsRunning => Volatile.Read(ref _isRunning);
    public int DeviceIndex => _deviceIndex;

    /// <summary>Live source — only "exhausted" if disposed; otherwise more samples may yet arrive.</summary>
    public bool IsExhausted => _disposed;

    public int ReadInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dst.Length % _format.Channels != 0)
            throw new ArgumentException(
                $"dst length {dst.Length} is not a multiple of channel count {_format.Channels}", nameof(dst));

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
        _samplesEmitted += toRead / _format.Channels;
        return toRead;
    }

    /// <summary>Approximate samples-per-channel currently sitting in the ring buffer.</summary>
    public int AvailableSamples => (int)((Volatile.Read(ref _writeIndex) - Volatile.Read(ref _readIndex)) / _format.Channels);
    public int CapacitySamples => _ringBuffer.Length / _format.Channels;
    /// <summary>Samples dropped by the callback because the ring buffer was full.</summary>
    public long OverflowSamples => Volatile.Read(ref _overflowSamples);

    /// <summary>
    /// Skips ahead to the most recent samples by advancing the read pointer so the ring holds no
    /// more than <paramref name="keepBuffered"/> of capture. Mirrors
    /// <c>NDIAudioReceiver.RebaseToLatest</c>: the capture callback runs continuously from
    /// <see cref="Start"/>, so by the time a HaPlay session reaches Play the ring may already hold
    /// seconds of stale audio. Without this call the router would consume that backlog in FIFO order
    /// and audio would play back <c>Tconnect</c> seconds behind real time.
    /// </summary>
    /// <param name="keepBuffered">Default 100 ms keeps a small jitter cushion. Clamped to ≥ 20 ms.</param>
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

        var skip = buffered - keepFloats;
        Volatile.Write(ref _readIndex, read + skip);
    }

    /// <summary>Non-zero if the native stream callback caught an exception.</summary>
    public bool CallbackFaulted => Volatile.Read(ref _callbackFaulted) != 0;

    /// <summary>
    /// First exception caught in the PortAudio stream callback, if <see cref="CallbackFaulted"/> is true.
    /// Cleared when <see cref="Start"/> begins a new stream session.
    /// </summary>
    public Exception? CallbackFaultException => Volatile.Read(ref _callbackFaultException);

    /// <summary>1 = PA reports stream active, 0 = inactive, negative = error/closed.</summary>
    public int StreamActive => _stream != nint.Zero ? (int)Native.Pa_IsStreamActive(_stream) : -1;

    /// <summary>True when the input stream is open and PortAudio reports it active.</summary>
    public bool IsAdvancing => _stream != nint.Zero && (int)Native.Pa_IsStreamActive(_stream) == 1;

    /// <summary>Latched once <see cref="CheckStreamActive"/> observes a running stream that is no longer active.</summary>
    public bool StreamInactiveDetected => Volatile.Read(ref _streamInactiveDetected) != 0;

    /// <summary>True when capture has faulted, or a running stream is no longer reported active.</summary>
    public bool HasInputFault
    {
        get
        {
            if (CallbackFaulted || StreamInactiveDetected)
                return true;
            return IsRunning && StreamActive != 1;
        }
    }

    public double StreamTime => _stream != nint.Zero ? Native.Pa_GetStreamTime(_stream) : 0.0;

    /// <summary>
    /// Polls PortAudio's active flag and latches <see cref="StreamInactiveDetected"/>
    /// when a stream that should be running has stopped or reports an error.
    /// </summary>
    public bool CheckStreamActive()
    {
        if (!IsRunning || _stream == nint.Zero)
            return false;

        var active = (int)Native.Pa_IsStreamActive(_stream);
        if (active == 1)
            return true;

        Volatile.Write(ref _streamInactiveDetected, 1);
        return false;
    }

    public PortAudioInput(
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

        PortAudioRuntime.Acquire();
        try
        {
            _deviceIndex = deviceIndex ?? Native.Pa_GetDefaultInputDevice();
            if (_deviceIndex < 0)
                throw new InvalidOperationException("no default PortAudio input device available");

            var devInfo = Native.Pa_GetDeviceInfo(_deviceIndex)
                ?? throw new InvalidOperationException($"invalid PortAudio device index {_deviceIndex}");
            if (devInfo.maxInputChannels < format.Channels)
                throw new InvalidOperationException(
                    $"device '{devInfo.Name}' supports {devInfo.maxInputChannels} input channels, requested {format.Channels}");

            _suggestedLatency = suggestedLatency ?? devInfo.defaultLowInputLatency;
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
        if (Volatile.Read(ref _isRunning)) return;

        Interlocked.Exchange(ref _callbackFaultException, null);
        Volatile.Write(ref _callbackFaulted, 0);
        Volatile.Write(ref _streamInactiveDetected, 0);

        _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);

        var inParams = new PaStreamParameters
        {
            device = _deviceIndex,
            channelCount = _format.Channels,
            sampleFormat = PaSampleFormat.paFloat32,
            suggestedLatency = _suggestedLatency,
            hostApiSpecificStreamInfo = nint.Zero,
        };

        var err = Native.Pa_OpenStream(
            out _stream,
            inputParameters: inParams,
            outputParameters: null,
            sampleRate: _format.SampleRate,
            framesPerBuffer: _framesPerBuffer,
            streamFlags: PaStreamFlags.paNoFlag,
            streamCallback: CallbackPtr,
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

        Volatile.Write(ref _isRunning, true);
    }

    public void Stop()
    {
        if (!Volatile.Read(ref _isRunning)) return;
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
        }
    }

    /// <summary>
    /// Pulls <paramref name="samplesPerChannel"/> samples per channel out of the
    /// capture buffer into a fresh frame. Returns false if not enough samples
    /// are available yet — caller can wait and retry.
    /// </summary>
    /// <remarks>
    /// Allocates a fresh sample array per call. For high-frequency capture
    /// loops prefer <see cref="ReadInto"/> with a reusable destination, or
    /// <see cref="TryReadFrame(Memory{float}, out AudioFrame)"/> to supply a
    /// pooled buffer.
    /// </remarks>
    public bool TryReadFrame(int samplesPerChannel, out AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samplesPerChannel <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerChannel));
        return TryReadFrame(new float[samplesPerChannel * _format.Channels], samplesPerChannel, out frame);
    }

    /// <summary>
    /// Same as <see cref="TryReadFrame(int, out AudioFrame)"/> but writes into
    /// a caller-supplied destination — eliminates the per-call allocation.
    /// <paramref name="destination"/>'s length must equal
    /// <c>samplesPerChannel × Format.Channels</c>; the resulting <see cref="AudioFrame"/>
    /// references the same memory, so callers must not reuse it until the
    /// frame has been consumed.
    /// </summary>
    public bool TryReadFrame(Memory<float> destination, int samplesPerChannel, out AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samplesPerChannel <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerChannel));
        var totalFloats = samplesPerChannel * _format.Channels;
        if (destination.Length != totalFloats)
            throw new ArgumentException(
                $"destination length {destination.Length} must equal samplesPerChannel × channels ({totalFloats})",
                nameof(destination));

        var read = Volatile.Read(ref _readIndex);
        var write = Volatile.Read(ref _writeIndex);
        var available = (int)(write - read);
        if (available < totalFloats)
        {
            frame = default;
            return false;
        }

        var dst = destination.Span;
        var startIdx = (int)(read & _ringMask);
        var firstChunk = Math.Min(totalFloats, _ringBuffer.Length - startIdx);
        _ringBuffer.AsSpan(startIdx, firstChunk).CopyTo(dst);
        if (firstChunk < totalFloats)
            _ringBuffer.AsSpan(0, totalFloats - firstChunk).CopyTo(dst[firstChunk..]);
        Volatile.Write(ref _readIndex, read + totalFloats);

        var pts = TimeSpan.FromSeconds((double)_samplesEmitted / _format.SampleRate);
        _samplesEmitted += samplesPerChannel;
        frame = new AudioFrame(pts, _format, samplesPerChannel, destination);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Callback(
        nint inputBuffer, nint outputBuffer, nuint frames,
        nint timeInfo, PaStreamCallbackFlags flags, nint userData)
    {
        PortAudioInput? self = null;
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not PortAudioInput s)
                return (int)PaStreamCallbackResult.paAbort;
            self = s;

            if (inputBuffer == nint.Zero)
            {
                Interlocked.CompareExchange(
                    ref self._callbackFaultException,
                    new InvalidOperationException("PortAudio input callback received a null input buffer."),
                    null);
                Volatile.Write(ref self._callbackFaulted, 1);
                return (int)PaStreamCallbackResult.paAbort;
            }

            var totalFloats = (int)frames * self._format.Channels;
            var input = new ReadOnlySpan<float>((float*)inputBuffer, totalFloats);

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

            return (int)PaStreamCallbackResult.paContinue;
        }
        catch (Exception ex)
        {
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
        try
        {
            Stop();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "PortAudioInput.Dispose: Stop");
        }
#else
        catch
        {
        }
#endif
        try
        {
            PortAudioRuntime.Release();
        }
#if DEBUG
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "PortAudioInput.Dispose: PortAudioRuntime.Release");
        }
#else
        catch
        {
        }
#endif
    }
}
