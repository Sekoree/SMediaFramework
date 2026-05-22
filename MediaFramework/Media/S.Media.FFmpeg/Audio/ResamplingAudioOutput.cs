using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;

namespace S.Media.FFmpeg.Audio;

/// <summary>
/// Presents <see cref="Format"/> at the router sample rate while forwarding to an
/// <see cref="IAudioOutput"/> that expects a different fixed rate (for example 48&nbsp;kHz NDI from 44.1&nbsp;kHz mux audio).
/// </summary>
/// <remarks>
/// <para>
/// Does not own <see cref="IAudioOutput"/> — the host disposes the inner output (for example an
/// <c>NDIAudioOutput</c> owned by <c>NDIOutput</c>) on its own schedule.
/// </para>
/// <para>
/// Implements <see cref="IFlushableOutput"/> so <see cref="AudioRouter"/> pause/seek drops buffered
/// resampler state instead of bleeding across discontinuities.
/// </para>
/// </remarks>
public sealed class ResamplingAudioOutput : IAudioOutput, IAudioOutputChannelCapabilities, IFlushableOutput, IDisposable
{
    private readonly IAudioOutput _inner;
    private readonly AudioFormat _routerFormat;
    private readonly object _gate = new();
    private AudioResampler? _swr;
    private float[] _dstScratch;
    private bool _disposed;

    /// <param name="inner">Output at the destination sample rate (same channel count as <paramref name="routerFormat"/>).</param>
    /// <param name="routerFormat">Format the <see cref="AudioRouter"/> uses for this route (typically the decoder rate).</param>
    public ResamplingAudioOutput(IAudioOutput inner, AudioFormat routerFormat)
    {
        ArgumentNullException.ThrowIfNull(inner);
        routerFormat.Validate(nameof(routerFormat));
        inner.Format.Validate(nameof(inner));
        if (inner.Format.Channels != routerFormat.Channels)
            throw new ArgumentException("Inner output channel count must match router format channels.", nameof(inner));

        _inner = inner;
        _routerFormat = routerFormat;
        _dstScratch = new float[checked(8192 * routerFormat.Channels)];
    }

    /// <inheritdoc />
    /// <summary>Router-side format (input to <see cref="Submit"/>).</summary>
    public AudioFormat Format => _routerFormat;
    public AudioOutputChannelCapabilities ChannelCapabilities =>
        _inner is IAudioOutputChannelCapabilities c
            ? c.ChannelCapabilities with { CurrentChannels = _routerFormat.Channels }
            : AudioOutputChannelCapabilities.Fixed(_routerFormat.Channels);

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
            MediaDiagnostics.SwallowDisposeErrors(() => _swr?.Dispose(), "ResamplingAudioOutput.Flush: dispose resampler");
            _swr = null;
        }

        if (_inner is IFlushableOutput f)
            f.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_gate)
        {
            MediaDiagnostics.SwallowDisposeErrors(() => _swr?.Dispose(), "ResamplingAudioOutput.Dispose: resampler");
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
