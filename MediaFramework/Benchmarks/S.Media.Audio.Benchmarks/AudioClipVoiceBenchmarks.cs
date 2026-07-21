using BenchmarkDotNet.Attributes;
using S.Media.Core.Audio;

namespace S.Media.Audio.Benchmarks;

/// <summary>
/// Measures <see cref="AudioClipVoice.ReadInto"/>. The settled-gain path is segmented/vectorized;
/// <see cref="VoiceReadIntoRamping"/> pins the attack/release fade path, which stays a scalar
/// per-frame loop (<see cref="IdealGainCopy"/> is the theoretical bulk-copy floor). The soundboard
/// runs many voices at once, so any delta multiplies.
/// </summary>
[MemoryDiagnoser]
public class AudioClipVoiceBenchmarks
{
    private const int ChunkFloats = 960; // 480 frames stereo = one 10 ms mix chunk at 48 kHz

    private AudioClipVoice _voice = null!;
    private AudioClipVoice _rampingVoice = null!;
    private float[] _clipData = null!;
    private float[] _dst = null!;
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _clipData = new float[48_000 * 2];
        for (var i = 0; i < _clipData.Length; i++)
            _clipData[i] = MathF.Sin(i * 0.005f) * 0.5f;

        var clip = AudioClip.FromSamples(new AudioFormat(48_000, 2), _clipData);
        _voice = clip.CreateVoice(AudioClipVoiceOptions.Default with { Loop = true, StartGain = 0.8f });
        // Attack far longer than the run so every measured chunk stays mid-ramp.
        _rampingVoice = clip.CreateVoice(AudioClipVoiceOptions.Default with
        {
            Loop = true,
            StartGain = 0.8f,
            AttackFade = TimeSpan.FromHours(1),
        });
        _dst = new float[ChunkFloats];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _voice.Dispose();
        _rampingVoice.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int VoiceReadInto() => _voice.ReadInto(_dst);

    /// <summary>Mid-fade chunk: the scalar per-frame ramp segment.</summary>
    [Benchmark]
    public int VoiceReadIntoRamping() => _rampingVoice.ReadInto(_dst);

    [Benchmark]
    public int IdealGainCopy()
    {
        const float gain = 0.8f;
        var src = _clipData.AsSpan();
        var dst = _dst.AsSpan();
        var remaining = src.Length - _cursor;
        if (remaining < ChunkFloats)
            _cursor = 0;
        var chunk = src.Slice(_cursor, ChunkFloats);
        for (var i = 0; i < chunk.Length; i++)
            dst[i] = chunk[i] * gain;
        _cursor += ChunkFloats;
        return ChunkFloats;
    }
}
