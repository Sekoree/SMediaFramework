namespace S.Media.Core.Audio;

/// <summary>
/// One playback cursor over a shared <see cref="AudioClip"/> buffer. Zero heap allocations on
/// <see cref="ReadInto"/> after construction.
/// </summary>
public sealed class AudioClipVoice : IAudioSource, IDisposable
{
    private readonly AudioClip _clip;
    private readonly ReadOnlyMemory<float> _interleaved;
    private readonly int _channels;
    private readonly int _attackSamples;
    private readonly int _releaseSamples;
    private readonly float _startGain;
    private int _cursorFrames;
    private float _gain;
    private float _gainPerSampleAttack;
    private float _gainPerSampleRelease;
    private int _rampSamplesRemaining;
    private bool _releasing;
    private bool _stopped;
    private bool _disposed;

    internal AudioClipVoice(AudioClip clip, AudioClipVoiceOptions options)
    {
        _clip = clip;
        Format = clip.Format;
        _interleaved = clip.Interleaved;
        _channels = Format.Channels;
        Loop = options.Loop;
        _startGain = options.StartGain;
        _gain = options.AttackFade is { } attack && attack > TimeSpan.Zero
            ? 0f
            : _startGain;

        if (options.StartOffsetSec > 0)
        {
            var startFrames = (int)(options.StartOffsetSec * Format.SampleRate);
            _cursorFrames = Math.Clamp(startFrames, 0, clip.SamplesPerChannel);
        }

        _attackSamples = options.AttackFade is { } a && a > TimeSpan.Zero
            ? Math.Max(1, (int)(a.TotalSeconds * Format.SampleRate))
            : 0;
        _releaseSamples = options.ReleaseFade is { } r && r > TimeSpan.Zero
            ? Math.Max(1, (int)(r.TotalSeconds * Format.SampleRate))
            : Math.Max(1, (int)(0.01 * Format.SampleRate));

        if (_attackSamples > 0)
        {
            _gainPerSampleAttack = _startGain / _attackSamples;
            _rampSamplesRemaining = _attackSamples;
        }
    }

    public AudioFormat Format { get; }

    public bool Loop { get; set; }

    public TimeSpan Position =>
        TimeSpan.FromSeconds((double)_cursorFrames / Format.SampleRate);

    public bool IsExhausted => _stopped || (_cursorFrames >= _clip.SamplesPerChannel && !_releasing && !Loop);

    /// <summary>Starts a click-free release fade; voice becomes exhausted after the fade completes.</summary>
    public void Stop()
    {
        if (_stopped || _disposed) return;
        _releasing = true;
        _rampSamplesRemaining = _releaseSamples;
        if (_releaseSamples > 0)
            _gainPerSampleRelease = _gain / _releaseSamples;
    }

    public int ReadInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsExhausted)
            return 0;

        var framesRequested = dst.Length / _channels;
        if (framesRequested == 0)
            return 0;

        var writtenFrames = 0;
        while (writtenFrames < framesRequested && !IsExhausted)
        {
            if (_cursorFrames >= _clip.SamplesPerChannel)
            {
                if (Loop && !_releasing)
                {
                    _cursorFrames = 0;
                    continue;
                }

                if (_releasing)
                {
                    // Source exhausted mid-release (clip ended, or Stop() landed on the
                    // clip-end boundary). There are no more samples to carry the release
                    // ramp, so finish it now: drive gain to zero and stop. Otherwise the
                    // ramp never completes, _stopped stays false, and IsExhausted is stuck
                    // false forever - the voice lingers returning zero samples and is never
                    // reaped.
                    _gain = 0f;
                    _stopped = true;
                    break;
                }
                _stopped = true;
                break;
            }

            var src = _interleaved.Span;
            var srcBase = _cursorFrames * _channels;
            var dstBase = writtenFrames * _channels;
            for (var ch = 0; ch < _channels; ch++)
                dst[dstBase + ch] = src[srcBase + ch] * _gain;

            AdvanceGainRamp();
            _cursorFrames++;
            writtenFrames++;
        }

        if (_releasing && _gain <= 0f)
            _stopped = true;

        return writtenFrames * _channels;
    }

    private void AdvanceGainRamp()
    {
        if (_rampSamplesRemaining <= 0)
            return;

        _rampSamplesRemaining--;
        if (_releasing)
        {
            _gain = Math.Max(0f, _gain - _gainPerSampleRelease);
            return;
        }

        if (_attackSamples > 0)
            _gain = Math.Min(_startGain, _gain + _gainPerSampleAttack);
    }

    public void Dispose()
    {
        _disposed = true;
        _stopped = true;
    }
}
