using S.Media.Core.Buses;

namespace S.Media.Routing;

/// <summary>
/// An <see cref="AudioBus"/> with an ordered effect chain applied on the read side: register it on the
/// router as BOTH an output (routes send into it) and a source (routes pull the processed audio) to
/// build send/return inserts - "deck → [bus: comp → EQ] → master out". The chain is a copy-on-write
/// array: <see cref="SetEffects"/>/<see cref="AddEffect"/> swap atomically, so the audio pull path
/// never takes a lock. Effects run on the router run-loop thread and must be RT-safe
/// (<see cref="IAudioBusEffect"/> contract). The bus owns its effects and disposes them.
/// </summary>
public sealed class AudioEffectBus : IAudioOutput, IAudioOutputChannelCapabilities, IAudioSource, IDisposable
{
    private readonly AudioBus _bus;
    private readonly AudioFormat _format;
    private IAudioBusEffect[] _effects = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<IAudioBusEffect> _retired = new();
    private long _samplePosition;
    private bool _disposed;

    public AudioEffectBus(AudioFormat format, TimeSpan? maxBufferedDuration = null)
    {
        _bus = new AudioBus(format, maxBufferedDuration);
        _format = format;
    }

    public AudioFormat Format => _format;

    public AudioOutputChannelCapabilities ChannelCapabilities => _bus.ChannelCapabilities;

    public bool IsExhausted => false;

    public long OverflowFloats => _bus.OverflowFloats;

    public long UnderflowFloats => _bus.UnderflowFloats;

    /// <summary>The current chain snapshot (for UI listing).</summary>
    public IReadOnlyList<IAudioBusEffect> Effects => Volatile.Read(ref _effects);

    /// <summary>Replaces the whole chain atomically. Effects removed by the swap RETIRE on the read
    /// thread's next pass (or in <see cref="Dispose"/>) - never here, where the pull path may still be
    /// inside the old array (review M5: disposing under a live Process is unsafe for effects owning
    /// native/DSP state).</summary>
    public void SetEffects(IReadOnlyList<IAudioBusEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = effects.ToArray();
        foreach (var effect in next)
            effect.Configure(_format);
        var previous = Interlocked.Exchange(ref _effects, next);
        foreach (var old in previous)
        {
            if (!next.Contains(old))
                _retired.Enqueue(old);
        }
    }

    private void DrainRetired()
    {
        while (_retired.TryDequeue(out var old))
            MediaDiagnostics.SwallowDisposeErrors(old.Dispose, "AudioEffectBus: retired effect");
    }

    /// <summary>Appends one effect to the chain (atomic swap).</summary>
    public void AddEffect(IAudioBusEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ObjectDisposedException.ThrowIf(_disposed, this);
        effect.Configure(_format);
        while (true)
        {
            var current = Volatile.Read(ref _effects);
            var next = new IAudioBusEffect[current.Length + 1];
            current.CopyTo(next, 0);
            next[^1] = effect;
            if (Interlocked.CompareExchange(ref _effects, next, current) == current)
                return;
        }
    }

    public void Submit(ReadOnlySpan<float> packedSamples) => _bus.Submit(packedSamples);

    public int ReadInto(Span<float> destination)
    {
        // The single pull thread: anything retired by an earlier swap is out of use by now.
        DrainRetired();
        var read = _bus.ReadInto(destination);
        var effects = Volatile.Read(ref _effects);
        if (effects.Length > 0 && read > 0)
        {
            var chunk = destination[..read];
            foreach (var effect in effects)
                effect.Process(chunk, _samplePosition);
        }

        _samplePosition += read / _format.Channels;
        return read;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DrainRetired();
        var effects = Interlocked.Exchange(ref _effects, []);
        foreach (var effect in effects)
            MediaDiagnostics.SwallowDisposeErrors(effect.Dispose, "AudioEffectBus.Dispose: effect");
    }
}
