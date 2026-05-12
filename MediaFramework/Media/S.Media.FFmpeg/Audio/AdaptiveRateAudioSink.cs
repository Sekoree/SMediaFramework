using S.Media.Core.Audio;

namespace S.Media.FFmpeg.Audio;

/// <summary>
/// Wraps an <see cref="IAudioSink"/> (and optional <see cref="IClockedSink"/>) and applies a
/// small libav <c>swresample</c> rate tweak so a sink that cannot keep up with the router
/// (sustained <see cref="AudioRouter.PumpPressure"/> drops on that sink id) receives slightly
/// fewer samples per chunk, easing queue growth without retuning the global playback clock.
/// </summary>
/// <remarks>
/// <para>
/// Negative <see cref="PumpPressurePlaybackHintMonitor.HintPpmBias"/> means the sink is slow
/// relative to production; this sink maps that to a slightly lower output sample rate
/// (e.g. 48&nbsp;000&nbsp;Hz → 47&nbsp;997&nbsp;Hz) so libav’s resampler shaves a few
/// frames over time. Positive bias does the opposite when a sink is starved (rare with bounded
/// pumps — hosts may still use a positive master-clock bias instead).
/// </para>
/// <para>
/// Rate correction is clamped to a few hertz around the nominal device rate to keep the
/// resampler stable; pass a larger <c>maxRateDeltaHz</c> only when you accept more aggressive
/// stretching.
/// </para>
/// <para>
/// When you remove the sink from the <see cref="AudioRouter"/>, dispose this
/// wrapper (after <see cref="AudioRouter.RemoveSink"/>) so the pressure monitor unsubscribes.
/// </para>
/// </remarks>
public sealed class AdaptiveRateAudioSink : IAudioSink, IClockedSink, IDisposable
{
    private readonly IAudioSink _inner;
    private readonly AudioFormat _format;
    private readonly PumpPressurePlaybackHintMonitor? _hintMonitor;
    private readonly Func<double>? _getPpmBias;
    private readonly int _nominalRate;
    private readonly int _maxRateDeltaHz;
    private readonly object _resampleGate = new();
    private AudioResampler? _swr;
    private int _effectiveOutRate;
    private float[] _scratch;
    private bool _disposed;

    /// <summary>
    /// Adapts using <see cref="AudioRouter.PumpPressure"/> for a single <paramref name="sinkId"/>.
    /// </summary>
    public AdaptiveRateAudioSink(
        IAudioSink inner,
        AudioRouter router,
        string sinkId,
        double maxAbsPpm = 40,
        double ppmPerDropPerSecond = 4,
        int maxRateDeltaHz = 3)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentException.ThrowIfNullOrWhiteSpace(sinkId);
        _inner = inner;
        _format = inner.Format;
        if (_format.SampleRate <= 0 || _format.Channels <= 0)
            throw new ArgumentException("Inner sink format must have positive sample rate and channels.", nameof(inner));
        _nominalRate = _format.SampleRate;
        _maxRateDeltaHz = maxRateDeltaHz;
        _hintMonitor = new PumpPressurePlaybackHintMonitor(router, sinkId, maxAbsPpm, ppmPerDropPerSecond);
        _getPpmBias = null;
        _effectiveOutRate = 0;
        _scratch = RentScratch(_format.Channels);
    }

    /// <summary>
    /// Adapts using an external ppm provider (tests or custom telemetry that already merged hints).
    /// </summary>
    public AdaptiveRateAudioSink(IAudioSink inner, Func<double> getPlaybackPpmBias, int maxRateDeltaHz = 3)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _getPpmBias = getPlaybackPpmBias ?? throw new ArgumentNullException(nameof(getPlaybackPpmBias));
        _inner = inner;
        _format = inner.Format;
        if (_format.SampleRate <= 0 || _format.Channels <= 0)
            throw new ArgumentException("Inner sink format must have positive sample rate and channels.", nameof(inner));
        _nominalRate = _format.SampleRate;
        _maxRateDeltaHz = maxRateDeltaHz;
        _hintMonitor = null;
        _effectiveOutRate = 0;
        _scratch = RentScratch(_format.Channels);
    }

    public AudioFormat Format => _format;

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (packedSamples.Length % _format.Channels != 0)
            throw new ArgumentException(
                $"packedSamples.Length {packedSamples.Length} is not a multiple of channel count {_format.Channels}",
                nameof(packedSamples));

        var frames = packedSamples.Length / _format.Channels;
        if (frames == 0)
            return;

        lock (_resampleGate)
        {
            var ppm = CurrentPpmBias();
            var desiredOut = (int)Math.Round(_nominalRate * (1 + ppm / 1_000_000.0));
            desiredOut = Math.Clamp(desiredOut, _nominalRate - _maxRateDeltaHz, _nominalRate + _maxRateDeltaHz);

            if (_swr is null || desiredOut != _effectiveOutRate)
            {
                _swr?.Dispose();
                _swr = AudioResampler.Create(
                    new AudioFormat(_nominalRate, _format.Channels),
                    new AudioFormat(desiredOut, _format.Channels));
                _effectiveOutRate = desiredOut;
            }

            var headroomFrames = frames + 16;
            EnsureScratchCapacity(checked(headroomFrames * _format.Channels));

            var got = _swr.Convert(packedSamples, frames, _scratch, headroomFrames);
            if (got > 0)
                _inner.Submit(_scratch.AsSpan(0, checked(got * _format.Channels)));
        }
    }

    public bool WaitForCapacity(int chunkSamples, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner is IClockedSink c
            ? c.WaitForCapacity(chunkSamples, token)
            : !token.IsCancellationRequested;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hintMonitor?.Dispose();
        lock (_resampleGate)
        {
            _swr?.Dispose();
            _swr = null;
        }
    }

    private double CurrentPpmBias() => _hintMonitor?.HintPpmBias ?? _getPpmBias!();

    private static float[] RentScratch(int channels)
        => new float[checked(8192 * channels)];

    private void EnsureScratchCapacity(int minFloats)
    {
        if (_scratch.Length >= minFloats)
            return;
        _scratch = new float[Math.Max(minFloats, checked(_scratch.Length * 2))];
    }
}
