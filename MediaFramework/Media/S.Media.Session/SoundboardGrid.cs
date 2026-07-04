using S.Media.Core.Audio;

namespace S.Media.Session;

public enum SoundboardPadMode
{
    OneShot,
    Retrigger,
    LatchToggle,
    Momentary,
    ExclusiveGroup,
    ChokeGroup,
    QuantizedLaunch,
    StopOnNoteOff,
}

public enum SoundboardLedState
{
    Off,
    Ready,
    Playing,
    Armed,
    Faulted,
}

public sealed record SoundboardPadDefinition(
    string PadId,
    string CueId,
    string Label,
    SoundboardPadMode Mode = SoundboardPadMode.OneShot,
    string? GroupId = null,
    TimeSpan? Quantize = null,
    TimeSpan? ScheduledAt = null);

public sealed record SoundboardPadFeedback(
    string PadId,
    SoundboardLedState Led,
    string? Text = null);

public sealed record SoundboardVoiceControl(
    string PadId,
    TimeSpan? Seek = null,
    float? FadeToGain = null,
    float? Pitch = null,
    float? Pan = null,
    string? OutputOverride = null,
    TimeSpan? RemainingTime = null);

public sealed record SoundboardGridBinding(
    string PadId,
    string TriggerSource,
    string TriggerKey);

public sealed record SoundboardGridSnapshot(
    IReadOnlyList<SoundboardPadDefinition> Pads,
    IReadOnlyList<string> PreloadedCueIds,
    long EstimatedBytes,
    long MemoryBudgetBytes,
    IReadOnlyList<SoundboardPadFeedback> Feedback,
    IReadOnlyList<SoundboardGridBinding> Bindings,
    IReadOnlyList<SoundboardVoiceControl> VoiceControls,
    bool AutomaticReapingEnabled);

public sealed record SoundboardScheduledFire(
    string PadId,
    string CueId,
    TimeSpan When,
    TimeSpan? Quantize);

public sealed class SoundboardGrid
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SoundboardPadDefinition> _pads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _preloaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SoundboardPadFeedback> _feedback = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SoundboardGridBinding> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SoundboardVoiceControl> _voiceControls = new(StringComparer.Ordinal);

    public long MemoryBudgetBytes { get; }

    public bool AutomaticReapingEnabled { get; set; } = true;

    public SoundboardGrid(long memoryBudgetBytes = 0)
    {
        if (memoryBudgetBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(memoryBudgetBytes), "memory budget must be >= 0");
        MemoryBudgetBytes = memoryBudgetBytes;
    }

    public void SetPad(SoundboardPadDefinition pad)
    {
        ArgumentException.ThrowIfNullOrEmpty(pad.PadId);
        ArgumentException.ThrowIfNullOrEmpty(pad.CueId);
        ArgumentException.ThrowIfNullOrEmpty(pad.Label);
        lock (_gate)
            _pads[pad.PadId] = pad;
    }

    public bool RemovePad(string padId)
    {
        ArgumentException.ThrowIfNullOrEmpty(padId);
        lock (_gate)
        {
            _feedback.Remove(padId);
            _bindings.Remove(padId);
            _voiceControls.Remove(padId);
            return _pads.Remove(padId);
        }
    }

    public bool TryPreload(string cueId, long estimatedBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        if (estimatedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedBytes), "estimated bytes must be >= 0");
        lock (_gate)
        {
            var currentWithoutCue = _preloaded.Where(kv => kv.Key != cueId).Sum(kv => kv.Value);
            if (MemoryBudgetBytes > 0 && currentWithoutCue + estimatedBytes > MemoryBudgetBytes)
                return false;
            _preloaded[cueId] = estimatedBytes;
            return true;
        }
    }

    public bool Unload(string cueId)
    {
        ArgumentException.ThrowIfNullOrEmpty(cueId);
        lock (_gate)
            return _preloaded.Remove(cueId);
    }

    public IReadOnlyList<string> EvictUntilWithinBudget()
    {
        lock (_gate)
        {
            if (MemoryBudgetBytes <= 0 || _preloaded.Values.Sum() <= MemoryBudgetBytes)
                return [];

            var evicted = new List<string>();
            foreach (var item in _preloaded.OrderByDescending(kv => kv.Value).ToArray())
            {
                _preloaded.Remove(item.Key);
                evicted.Add(item.Key);
                if (_preloaded.Values.Sum() <= MemoryBudgetBytes)
                    break;
            }

            return evicted;
        }
    }

    public void SetFeedback(SoundboardPadFeedback feedback)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedback.PadId);
        lock (_gate)
            _feedback[feedback.PadId] = feedback;
    }

    public void BindTrigger(SoundboardGridBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(binding.PadId);
        ArgumentException.ThrowIfNullOrEmpty(binding.TriggerSource);
        ArgumentException.ThrowIfNullOrEmpty(binding.TriggerKey);
        lock (_gate)
            _bindings[binding.PadId] = binding;
    }

    public void SetVoiceControl(SoundboardVoiceControl control)
    {
        ArgumentException.ThrowIfNullOrEmpty(control.PadId);
        lock (_gate)
            _voiceControls[control.PadId] = control;
    }

    public bool TryCreateScheduledFire(string padId, TimeSpan now, out SoundboardScheduledFire fire)
    {
        ArgumentException.ThrowIfNullOrEmpty(padId);
        lock (_gate)
        {
            if (!_pads.TryGetValue(padId, out var pad))
            {
                fire = default!;
                return false;
            }

            var when = pad.ScheduledAt ?? now;
            if (pad.Quantize is { } quantum && quantum > TimeSpan.Zero)
            {
                var ticks = ((when.Ticks + quantum.Ticks - 1) / quantum.Ticks) * quantum.Ticks;
                when = TimeSpan.FromTicks(ticks);
            }

            fire = new SoundboardScheduledFire(pad.PadId, pad.CueId, when, pad.Quantize);
            return true;
        }
    }

    public bool TryFirePad(Soundboard soundboard, string padId, AudioClipVoiceOptions? options, out CueVoice? voice)
    {
        ArgumentNullException.ThrowIfNull(soundboard);
        ArgumentException.ThrowIfNullOrEmpty(padId);
        SoundboardPadDefinition? pad;
        lock (_gate)
        {
            if (!_pads.TryGetValue(padId, out pad))
            {
                voice = null;
                return false;
            }
        }

        voice = soundboard.Fire(pad.CueId, options);
        SetFeedback(new SoundboardPadFeedback(
            padId,
            voice is null ? SoundboardLedState.Ready : SoundboardLedState.Playing,
            pad.Label));
        return voice is not null;
    }

    public SoundboardGridSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SoundboardGridSnapshot(
                _pads.Values.OrderBy(p => p.PadId, StringComparer.Ordinal).ToArray(),
                _preloaded.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                _preloaded.Values.Sum(),
                MemoryBudgetBytes,
                _feedback.Values.OrderBy(f => f.PadId, StringComparer.Ordinal).ToArray(),
                _bindings.Values.OrderBy(b => b.PadId, StringComparer.Ordinal).ToArray(),
                _voiceControls.Values.OrderBy(c => c.PadId, StringComparer.Ordinal).ToArray(),
                AutomaticReapingEnabled);
        }
    }
}
