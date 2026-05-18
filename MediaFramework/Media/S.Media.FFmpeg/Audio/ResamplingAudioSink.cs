using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.FFmpeg.Audio;

/// <summary>
/// Presents <see cref="Format"/> at the router sample rate while forwarding to an
/// <see cref="IAudioSink"/> that expects a different fixed rate (for example 48&nbsp;kHz NDI from 44.1&nbsp;kHz mux audio).
/// </summary>
/// <remarks>
/// <para>
/// Does not own <see cref="IAudioSink"/> — the host disposes the inner sink (for example an
/// <c>NDIAudioSink</c> owned by <c>NDIOutput</c>) on its own schedule.
/// </para>
/// <para>
/// Implements <see cref="IFlushableSink"/> so <see cref="AudioRouter"/> pause/seek drops buffered
/// resampler state instead of bleeding across discontinuities.
/// </para>
/// </remarks>
public sealed class ResamplingAudioSink : IAudioSink, IFlushableSink, IDisposable
{
    private readonly IAudioSink _inner;
    private readonly AudioFormat _routerFormat;
    private readonly object _gate = new();
    private AudioResampler? _swr;
    private float[] _dstScratch;
    private bool _disposed;

    /// <param name="inner">Sink at the destination sample rate (same channel count as <paramref name="routerFormat"/>).</param>
    /// <param name="routerFormat">Format the <see cref="AudioRouter"/> uses for this route (typically the decoder rate).</param>
    public ResamplingAudioSink(IAudioSink inner, AudioFormat routerFormat)
    {
        ArgumentNullException.ThrowIfNull(inner);
        routerFormat.Validate(nameof(routerFormat));
        inner.Format.Validate(nameof(inner));
        if (inner.Format.Channels != routerFormat.Channels)
            throw new ArgumentException("Inner sink channel count must match router format channels.", nameof(inner));

        _inner = inner;
        _routerFormat = routerFormat;
        _dstScratch = new float[checked(8192 * routerFormat.Channels)];
    }

    /// <inheritdoc />
    /// <summary>Router-side format (input to <see cref="Submit"/>).</summary>
    public AudioFormat Format => _routerFormat;

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ch = _routerFormat.Channels;
        if (packedSamples.Length % ch != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {ch}",
                nameof(packedSamples));

        var srcFrames = packedSamples.Length / ch;
        if (srcFrames == 0)
            return;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _swr ??= AudioResampler.Create(_routerFormat, _inner.Format);

            var dstMax = (int)Math.Ceiling(srcFrames * (double)_inner.Format.SampleRate / _routerFormat.SampleRate)
                + 64;
            EnsureScratch(checked(dstMax * ch));

            var got = _swr.Convert(packedSamples, srcFrames, _dstScratch, dstMax);
            if (got > 0)
                _inner.Submit(_dstScratch.AsSpan(0, checked(got * ch)));
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _swr?.Dispose();
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "ResamplingAudioSink.Flush: dispose resampler");
            }
#else
            catch
            {
            }
#endif
            _swr = null;
        }

        if (_inner is IFlushableSink f)
            f.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            try
            {
                _swr?.Dispose();
            }
#if DEBUG
            catch (Exception ex)
            {
                MediaDiagnostics.LogError(ex, "ResamplingAudioSink.Dispose: resampler");
            }
#else
            catch
            {
            }
#endif
            _swr = null;
        }
    }

    private void EnsureScratch(int minFloats)
    {
        if (_dstScratch.Length >= minFloats)
            return;
        _dstScratch = new float[Math.Max(minFloats, checked(_dstScratch.Length * 2))];
    }
}
