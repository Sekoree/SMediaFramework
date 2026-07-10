
namespace S.Media.Decode.FFmpeg.Audio;

/// <summary>
/// Presents an inner <see cref="IAudioSource"/> at a different sample rate. Mirror of
/// <see cref="ResamplingAudioOutput"/> for the source (input) direction - used by
/// <see cref="AudioRouter.AddSource(IAudioSource, string?, bool)"/> with <c>autoResample: true</c>
/// when a source's rate doesn't match the router (e.g. a 44.1 kHz clip feeding a 48 kHz router).
/// </summary>
/// <remarks>
/// <para>
/// Direct instances own their inner by default - disposing the wrapper disposes <c>inner</c>.
/// Hosts can pass <paramref name="disposeInnerWhenDisposed"/> as <see langword="false"/> when the
/// original source remains caller-owned. The default FFmpeg auto-resample factory installed for
/// <see cref="AudioRouter.AddSource(IAudioSource, string?, bool)"/> uses that caller-owned mode so
/// router disposal only disposes the wrapper it created.
/// </para>
/// <para>
/// libswresample buffers a small amount internally to honour the requested rate exactly; on the
/// first read after a discontinuity (e.g. seek), expect a one-call delay before output catches up.
/// </para>
/// </remarks>
public class ResamplingAudioSource : IAudioSource, ICooperativeAudioReadInterrupt, IDisposable
{
    /// <summary>
    /// Creates a resampling wrapper that <strong>preserves seekability</strong>: when
    /// <paramref name="inner"/> implements <see cref="ISeekableSource"/> the returned wrapper does too
    /// (forwarding <see cref="ISeekableSource.Seek"/>/<see cref="ISeekableSource.Position"/>/
    /// <see cref="ISeekableSource.Duration"/> and flushing the resampler on seek). Use this instead of
    /// the constructor so a sample-rate mismatch doesn't silently strip seek support from a file source.
    /// </summary>
    public static ResamplingAudioSource Create(IAudioSource inner, int outputSampleRate, bool disposeInnerWhenDisposed = true)
        => inner is ISeekableSource
            ? new SeekableResamplingAudioSource(inner, outputSampleRate, disposeInnerWhenDisposed)
            : new ResamplingAudioSource(inner, outputSampleRate, disposeInnerWhenDisposed);

    private readonly IAudioSource _inner;
    private readonly AudioFormat _outputFormat;
    private readonly bool _disposeInner;
    private AudioResampler? _swr;
    private float[] _srcScratch = [];
    private bool _disposed;
    private bool _drained;

    /// <param name="inner">The source presenting samples at <see cref="IAudioSource.Format"/>'s rate.</param>
    /// <param name="outputSampleRate">Rate the wrapper presents to its consumer (typically the router rate).</param>
    /// <param name="disposeInnerWhenDisposed">When true (default), disposing this wrapper disposes <paramref name="inner"/>.</param>
    public ResamplingAudioSource(IAudioSource inner, int outputSampleRate, bool disposeInnerWhenDisposed = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        inner.Format.Validate(nameof(inner));
        if (outputSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        _inner = inner;
        _outputFormat = new AudioFormat(outputSampleRate, inner.Format.Channels);
        _disposeInner = disposeInnerWhenDisposed;
    }

    public AudioFormat Format => _outputFormat;
    public bool IsExhausted => _inner.IsExhausted && _drained;
    public IAudioSource Inner => _inner;

    public void RequestYieldBetweenReads()
    {
        if (_inner is ICooperativeAudioReadInterrupt interrupt)
            interrupt.RequestYieldBetweenReads();
    }

    public void ClearYieldRequest()
    {
        if (_inner is ICooperativeAudioReadInterrupt interrupt)
            interrupt.ClearYieldRequest();
    }

    public int ReadInto(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channels = _outputFormat.Channels;
        if (destination.Length == 0)
            return 0;
        if (destination.Length % channels != 0)
            throw new ArgumentException(
                $"destination length {destination.Length} is not a multiple of channel count {channels}",
                nameof(destination));

        _swr ??= AudioResampler.Create(_inner.Format, _outputFormat);

        var dstFrames = destination.Length / channels;
        var srcRate = _inner.Format.SampleRate;
        var dstRate = _outputFormat.SampleRate;
        // +32-frame headroom covers swresample's internal lag without over-reading on tiny chunks.
        var srcFramesNeeded = (int)Math.Ceiling((double)dstFrames * srcRate / dstRate) + 32;
        var srcFloatsNeeded = checked(srcFramesNeeded * channels);
        EnsureSrcScratch(srcFloatsNeeded);

        var produced = 0;
        if (!_inner.IsExhausted)
        {
            var srcFloatsRead = _inner.ReadInto(_srcScratch.AsSpan(0, srcFloatsNeeded));
            var srcFramesActual = srcFloatsRead / channels;
            produced = _swr.Convert(
                _srcScratch.AsSpan(0, srcFloatsRead),
                srcFramesActual,
                destination,
                dstFrames);
        }

        // Drain whatever swresample buffered once the inner runs dry.
        if (_inner.IsExhausted && !_drained)
        {
            var remaining = dstFrames - produced;
            if (remaining > 0)
            {
                var drainOffset = checked(produced * channels);
                var drained = _swr.Drain(destination[drainOffset..], remaining);
                produced += drained;
                if (drained == 0)
                    _drained = true;
            }
        }

        return produced * channels;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(() => _swr?.Dispose(), "ResamplingAudioSource.Dispose: swr");
        _swr = null;
        if (_disposeInner && _inner is IDisposable d)
        {
            MediaDiagnostics.SwallowDisposeErrors(d.Dispose, "ResamplingAudioSource.Dispose: inner");
        }
    }

    /// <summary>Flushes resampler + drain state after the inner source jumped (seek), so the next read
    /// starts cleanly at the new location. Used by <see cref="SeekableResamplingAudioSource"/>.</summary>
    private protected void ResetAfterInnerSeek()
    {
        MediaDiagnostics.SwallowDisposeErrors(() => _swr?.Dispose(), "ResamplingAudioSource.ResetAfterInnerSeek: swr");
        _swr = null; // recreated lazily on the next ReadInto
        _drained = false;
    }

    private void EnsureSrcScratch(int minFloats)
    {
        if (_srcScratch.Length >= minFloats)
            return;
        _srcScratch = new float[Math.Max(minFloats, checked(_srcScratch.Length * 2))];
    }
}

/// <summary>Seekable variant of <see cref="ResamplingAudioSource"/> - forwards the inner source's
/// <see cref="ISeekableSource"/> surface and flushes the resampler on seek. Created by
/// <see cref="ResamplingAudioSource.Create"/> when the inner source is seekable.</summary>
internal sealed class SeekableResamplingAudioSource : ResamplingAudioSource, ISeekableSource
{
    private readonly ISeekableSource _seekableInner;

    public SeekableResamplingAudioSource(IAudioSource inner, int outputSampleRate, bool disposeInnerWhenDisposed)
        : base(inner, outputSampleRate, disposeInnerWhenDisposed)
        => _seekableInner = (ISeekableSource)inner;

    public TimeSpan Duration => _seekableInner.Duration;
    public TimeSpan Position => _seekableInner.Position;

    public void Seek(TimeSpan position)
    {
        _seekableInner.Seek(position);
        ResetAfterInnerSeek();
    }
}
