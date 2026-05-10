using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.Core.Audio;

namespace S.Media.PortAudio;

/// <summary>
/// Audio source backed by a PortAudio input stream. PortAudio's audio-thread
/// callback writes packed float32 samples into an SPSC ring buffer; consumers
/// pull frames via <see cref="TryReadFrame"/>.
/// </summary>
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

    private nint _stream;
    private GCHandle _selfHandle;
    private bool _isRunning;
    private bool _disposed;

    public AudioFormat Format => _format;
    public bool IsRunning => _isRunning;
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

    public double StreamTime => _stream != nint.Zero ? Native.Pa_GetStreamTime(_stream) : 0.0;

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
        if (_isRunning) return;

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

    /// <summary>
    /// Pulls <paramref name="samplesPerChannel"/> samples per channel out of the
    /// capture buffer into a fresh frame. Returns false if not enough samples
    /// are available yet — caller can wait and retry.
    /// </summary>
    public bool TryReadFrame(int samplesPerChannel, out AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (samplesPerChannel <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerChannel));

        var totalFloats = samplesPerChannel * _format.Channels;
        var read = Volatile.Read(ref _readIndex);
        var write = Volatile.Read(ref _writeIndex);
        var available = (int)(write - read);
        if (available < totalFloats)
        {
            frame = default;
            return false;
        }

        var samples = new float[totalFloats];
        var startIdx = (int)(read & _ringMask);
        var firstChunk = Math.Min(totalFloats, _ringBuffer.Length - startIdx);
        _ringBuffer.AsSpan(startIdx, firstChunk).CopyTo(samples);
        if (firstChunk < totalFloats)
            _ringBuffer.AsSpan(0, totalFloats - firstChunk).CopyTo(samples.AsSpan(firstChunk));
        Volatile.Write(ref _readIndex, read + totalFloats);

        var pts = TimeSpan.FromSeconds((double)_samplesEmitted / _format.SampleRate);
        _samplesEmitted += samplesPerChannel;
        frame = new AudioFrame(pts, _format, samplesPerChannel, samples);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Callback(
        nint inputBuffer, nint outputBuffer, nuint frames,
        nint timeInfo, PaStreamCallbackFlags flags, nint userData)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is not PortAudioInput self)
                return (int)PaStreamCallbackResult.paAbort;

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
        catch
        {
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
