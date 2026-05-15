using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.NDI.Audio;

/// <summary>
/// Buffers packed-float audio and forwards to an inner sink (for example <see cref="NDIAudioSink"/>)
/// in multiples of <paramref name="targetSamplesPerChannel"/> so NDI packets align with a stable
/// cadence (for example ~1 video frame of audio at 48 kHz).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Dispose"/> may flush a partial final block to the inner sink; <strong>Debug</strong> builds log a failed final
/// <see cref="IAudioSink.Submit"/> via <see cref="MediaDiagnostics.LogError"/> while <strong>Release</strong> continues.
/// </para>
/// </remarks>
public sealed class NDIAudioAggregatingSink : IAudioSink, IDisposable
{
    private readonly IAudioSink _inner;
    private readonly int _targetSamplesPerChannel;
    private float[] _buffer = [];
    private int _filledFloats;
    private bool _disposed;

    public NDIAudioAggregatingSink(IAudioSink inner, int targetSamplesPerChannel)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (targetSamplesPerChannel < 32)
            throw new ArgumentOutOfRangeException(nameof(targetSamplesPerChannel), "must be >= 32");
        _inner = inner;
        _targetSamplesPerChannel = targetSamplesPerChannel;
    }

    public AudioFormat Format => _inner.Format;

    public void Submit(in AudioFrame frame)
    {
        if (frame.Format != Format)
            throw new ArgumentException("frame format mismatch", nameof(frame));
        Submit(frame.Samples.Span);
    }

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ch = Format.Channels;
        if (packedSamples.Length % ch != 0)
            throw new ArgumentException("length is not a multiple of channel count", nameof(packedSamples));

        var need = _filledFloats + packedSamples.Length;
        if (need > _buffer.Length)
        {
            var cap = Math.Max(need, _buffer.Length == 0 ? 8192 : _buffer.Length * 2);
            Array.Resize(ref _buffer, cap);
        }

        packedSamples.CopyTo(_buffer.AsSpan(_filledFloats));
        _filledFloats += packedSamples.Length;

        var block = _targetSamplesPerChannel * ch;
        while (_filledFloats >= block)
        {
            _inner.Submit(_buffer.AsSpan(0, block));
            _buffer.AsSpan(block, _filledFloats - block).CopyTo(_buffer);
            _filledFloats -= block;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_filledFloats > 0 && _filledFloats % Format.Channels == 0)
        {
            try
            {
                _inner.Submit(_buffer.AsSpan(0, _filledFloats));
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "NDIAudioAggregatingSink.Dispose: final Submit");
            }
#else
            catch
            {
            }
#endif
        }
        _filledFloats = 0;
    }
}
