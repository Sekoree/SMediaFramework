namespace S.Media.Decode.FFmpeg.Audio;

/// <summary>
/// Wraps a non-master <see cref="IAudioOutput"/> and applies a small libav <c>swresample</c> rate tweak
/// driven by an external ppm-bias signal, so an output that drifts against the session master receives
/// slightly fewer/more samples per chunk - easing queue growth without retuning the global playback clock.
/// </summary>
/// <remarks>
/// <para>
/// Negative bias means the output is slow relative to production; this maps to a slightly lower output
/// sample rate (e.g. 48&nbsp;000&nbsp;Hz → 47&nbsp;997&nbsp;Hz) so the resampler shaves a few frames over
/// time. Positive bias does the opposite. Correction is clamped to <c>maxRateDeltaHz</c> around the nominal
/// device rate to keep the resampler stable.
/// </para>
/// <para>
/// This is the router-agnostic form (Core types only, so it lives in the FFmpeg decode module): the bias
/// arrives via <paramref name="getPlaybackPpmBias"/>. The router-side derivation of that bias from
/// <c>AudioRouter.PumpPressure</c> lives in <c>S.Media.Routing</c> (it can't be referenced here), wired in by
/// the player when adaptive-rate is enabled on non-master outputs.
/// </para>
/// </remarks>
public sealed class AdaptiveRateAudioOutput
    : IAudioOutput, IAudioOutputChannelCapabilities, IClockedOutput, IAdaptiveRateWrappedOutput, IDisposable
{
    private readonly IAudioOutput _inner;
    private readonly AudioFormat _format;
    private readonly Func<double> _getPpmBias;
    private readonly IDisposable? _biasSource;
    private readonly int _nominalRate;
    private readonly int _maxRateDeltaHz;
    private readonly object _resampleGate = new();
    private AudioResampler? _swr;
    private int _effectiveOutRate;
    private float[] _scratch;
    private bool _disposed;

    /// <summary>
    /// Wraps <paramref name="inner"/>, biasing its effective output rate by <paramref name="getPlaybackPpmBias"/>
    /// (parts-per-million, signed), clamped to ±<paramref name="maxRateDeltaHz"/> around the device rate.
    /// </summary>
    /// <param name="biasSource">Optional object disposed with this output - e.g. the (Routing-side) pump-pressure
    /// monitor backing <paramref name="getPlaybackPpmBias"/>, so its router subscription is released when the
    /// wrapped output is removed.</param>
    public AdaptiveRateAudioOutput(IAudioOutput inner, Func<double> getPlaybackPpmBias, int maxRateDeltaHz = 3,
        IDisposable? biasSource = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _getPpmBias = getPlaybackPpmBias ?? throw new ArgumentNullException(nameof(getPlaybackPpmBias));
        _biasSource = biasSource;
        _inner = inner;
        _format = inner.Format;
        if (_format.SampleRate <= 0 || _format.Channels <= 0)
            throw new ArgumentException("Inner output format must have positive sample rate and channels.", nameof(inner));
        _nominalRate = _format.SampleRate;
        if (maxRateDeltaHz < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRateDeltaHz));
        _maxRateDeltaHz = maxRateDeltaHz;
        _effectiveOutRate = 0;
        _scratch = RentScratch(_format.Channels);
    }

    /// <summary>Router-side format (input to <see cref="Submit"/>) - the wrapper presents the nominal rate.</summary>
    public AudioFormat Format => _format;

    public AudioOutputChannelCapabilities ChannelCapabilities =>
        _inner is IAudioOutputChannelCapabilities c
            ? c.ChannelCapabilities with { CurrentChannels = _format.Channels }
            : AudioOutputChannelCapabilities.Fixed(_format.Channels);

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
            var ppm = _getPpmBias();
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
        return _inner is IClockedOutput c
            ? c.WaitForCapacity(chunkSamples, token)
            : !token.IsCancellationRequested;
    }

    /// <summary>The output rate the resampler is currently converting to (0 until the first <see cref="Submit"/>).</summary>
    public int EffectiveOutputRate { get { lock (_resampleGate) return _effectiveOutRate; } }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(() => _biasSource?.Dispose(), "AdaptiveRateAudioOutput.Dispose: bias source");
        lock (_resampleGate)
        {
            MediaDiagnostics.SwallowDisposeErrors(() => _swr?.Dispose(), "AdaptiveRateAudioOutput.Dispose: AudioResampler");
            _swr = null;
        }
    }

    private static float[] RentScratch(int channels) => new float[checked(8192 * channels)];

    private void EnsureScratchCapacity(int minFloats)
    {
        if (_scratch.Length >= minFloats)
            return;
        _scratch = new float[Math.Max(minFloats, checked(_scratch.Length * 2))];
    }
}
