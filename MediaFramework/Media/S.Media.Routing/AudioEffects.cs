using S.Media.Core.Buses;

namespace S.Media.Routing;

/// <summary>
/// Inserts an <see cref="IAudioBusEffect"/> chain in front of any <see cref="IAudioOutput"/> - the
/// output-side audio insert (the audio twin of <see cref="VideoEffectBusOutput"/>). Runs the chain in
/// <see cref="Submit"/> on the router's per-output pump thread; the samples are copied into a scratch
/// buffer so the router's shared chunk is never mutated (other outputs receive the same span).
/// Capability mixins forward so pacing/flush semantics are unchanged. Owns its effects; inner
/// ownership stays with the caller.
/// </summary>
public sealed class AudioEffectOutput : IAudioOutput, IAudioOutputChannelCapabilities, IFlushableOutput, IDisposable
{
    private readonly IAudioOutput _inner;
    private IAudioBusEffect[] _effects;
    private float[] _scratch = [];
    private long _samplePosition;
    private bool _disposed;

    public AudioEffectOutput(IAudioOutput inner, IReadOnlyList<IAudioBusEffect> effects)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(effects);
        _effects = effects.ToArray();
        foreach (var effect in _effects)
            effect.Configure(inner.Format);
    }

    public IAudioOutput InnerOutput => _inner;

    public IReadOnlyList<IAudioBusEffect> Effects => Volatile.Read(ref _effects);

    /// <summary>Atomic chain swap; removed effects are disposed after the swap (≤ one chunk race).</summary>
    public void SetEffects(IReadOnlyList<IAudioBusEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = effects.ToArray();
        foreach (var effect in next)
            effect.Configure(_inner.Format);
        var previous = Interlocked.Exchange(ref _effects, next);
        foreach (var old in previous)
        {
            if (!next.Contains(old))
                MediaDiagnostics.SwallowDisposeErrors(old.Dispose, "AudioEffectOutput.SetEffects: removed effect");
        }
    }

    public AudioFormat Format => _inner.Format;

    public AudioOutputChannelCapabilities ChannelCapabilities =>
        _inner is IAudioOutputChannelCapabilities caps
            ? caps.ChannelCapabilities
            : AudioOutputChannelCapabilities.Fixed(_inner.Format.Channels);

    public void Submit(ReadOnlySpan<float> packedSamples)
    {
        if (_disposed)
            return;
        var effects = Volatile.Read(ref _effects);
        if (effects.Length == 0)
        {
            _inner.Submit(packedSamples);
            return;
        }

        if (_scratch.Length < packedSamples.Length)
            _scratch = new float[packedSamples.Length];
        var chunk = _scratch.AsSpan(0, packedSamples.Length);
        packedSamples.CopyTo(chunk);
        foreach (var effect in effects)
            effect.Process(chunk, _samplePosition);
        _samplePosition += packedSamples.Length / Math.Max(1, _inner.Format.Channels);
        _inner.Submit(chunk);
    }

    public void Flush() => (_inner as IFlushableOutput)?.Flush();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var effects = Interlocked.Exchange(ref _effects, []);
        foreach (var effect in effects)
            MediaDiagnostics.SwallowDisposeErrors(effect.Dispose, "AudioEffectOutput.Dispose: effect");
    }
}

/// <summary>The proof-of-concept audio effect: a click-free smoothed gain (also genuinely useful as a
/// bus trim). Set <see cref="GainDb"/> from any thread; the ramp applies over ~10 ms of samples.
/// Registry config blob: <c>{"gainDb": -6.0}</c>.</summary>
public sealed class GainAudioEffect : IAudioBusEffect
{
    /// <summary>Builds from the registry's opaque config blob (unknown/absent fields = unity).</summary>
    public static GainAudioEffect FromJson(string? configJson)
    {
        var effect = new GainAudioEffect();
        if (string.IsNullOrWhiteSpace(configJson))
            return effect;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(configJson);
            if (doc.RootElement.TryGetProperty("gainDb", out var gain) && gain.TryGetDouble(out var db))
                effect.GainDb = db;
        }
        catch (System.Text.Json.JsonException)
        {
            // opaque blob didn't parse - unity gain beats faulting the line
        }

        return effect;
    }

    private float _targetLinear = 1f;
    private float _currentLinear = 1f;
    private float _rampPerSample;

    /// <summary>Gain in dB (0 = unity, -inf via <see cref="float.NegativeInfinity"/> = mute).</summary>
    public double GainDb
    {
        get => 20.0 * Math.Log10(Math.Max(1e-9f, Volatile.Read(ref _targetLinear)));
        set => Volatile.Write(ref _targetLinear, double.IsNegativeInfinity(value)
            ? 0f
            : (float)Math.Pow(10.0, value / 20.0));
    }

    public void Configure(AudioFormat format) =>
        _rampPerSample = 1f / Math.Max(1, format.SampleRate / 100); // ~10 ms ramp

    public void Process(Span<float> interleaved, long samplePosition)
    {
        var target = Volatile.Read(ref _targetLinear);
        var current = _currentLinear;
        if (Math.Abs(current - target) < 1e-6f)
        {
            if (Math.Abs(target - 1f) < 1e-6f)
                return; // unity, settled - nothing to do
            for (var i = 0; i < interleaved.Length; i++)
                interleaved[i] *= target;
            return;
        }

        for (var i = 0; i < interleaved.Length; i++)
        {
            current = current < target
                ? Math.Min(target, current + _rampPerSample)
                : Math.Max(target, current - _rampPerSample);
            interleaved[i] *= current;
        }

        _currentLinear = current;
    }

    public void Dispose()
    {
    }
}
