using S.Media.Core.Triggers;

namespace S.Media.Playback;

public enum TriggerSourceKind
{
    Midi,
    Osc,
    Keyboard,
    Hardware,
    App,
}

public enum TriggerActionKind
{
    MediaPlayer,
    CueGraph,
    Soundboard,
    Routing,
    Custom,
}

public enum TriggerRetriggerPolicy
{
    Allow,
    Debounce,
    IgnoreWhileActive,
    Restart,
}

public enum TimecodeSyncKind
{
    None,
    Mtc,
    Ltc,
    Smpte,
}

public sealed record TriggerDescriptor(
    TriggerSourceKind SourceKind,
    string SourceId,
    string Key);

public sealed record TriggerActionDescriptor(
    TriggerActionKind Kind,
    string TargetId,
    string Command);

public sealed record TriggerBinding(
    string Id,
    TriggerDescriptor Trigger,
    TriggerActionDescriptor Action,
    TimeSpan Debounce = default,
    string? RetriggerPolicy = null)
{
    public TriggerRetriggerPolicy TypedRetriggerPolicy { get; init; } =
        Debounce > TimeSpan.Zero ? TriggerRetriggerPolicy.Debounce : TriggerRetriggerPolicy.Allow;
}

public sealed record TimecodeSyncPlan(
    TimecodeSyncKind Kind,
    string SourceId,
    double FrameRate,
    bool DropFrame = false);

public sealed record TriggerDispatch(
    string BindingId,
    TriggerActionDescriptor Action,
    TriggerPayload Payload,
    DateTimeOffset Timestamp);

public sealed class TriggerBindingSet
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TriggerBinding> _bindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastDispatch = new(StringComparer.Ordinal);
    private readonly List<TriggerDispatch> _dispatches = [];
    private TimecodeSyncPlan? _timecodeSyncPlan;

    public IReadOnlyList<TriggerBinding> Bindings
    {
        get
        {
            lock (_gate)
                return _bindings.Values.OrderBy(b => b.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<TriggerDispatch> Dispatches
    {
        get
        {
            lock (_gate)
                return _dispatches.ToArray();
        }
    }

    public TimecodeSyncPlan? TimecodeSyncPlan
    {
        get
        {
            lock (_gate)
                return _timecodeSyncPlan;
        }
    }

    public void SetTimecodeSyncPlan(TimecodeSyncPlan? plan)
    {
        if (plan is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(plan.SourceId);
            if (plan.Kind != TimecodeSyncKind.None && plan.FrameRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(plan), "timecode frame rate must be positive.");
        }

        lock (_gate)
            _timecodeSyncPlan = plan;
    }

    public void AddOrReplace(TriggerBinding binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(binding.Id);
        ArgumentException.ThrowIfNullOrEmpty(binding.Trigger.SourceId);
        ArgumentException.ThrowIfNullOrEmpty(binding.Trigger.Key);
        ArgumentException.ThrowIfNullOrEmpty(binding.Action.TargetId);
        ArgumentException.ThrowIfNullOrEmpty(binding.Action.Command);
        if (binding.Debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(binding), "debounce must be >= 0");

        lock (_gate)
            _bindings[binding.Id] = binding;
    }

    public bool Remove(string bindingId)
    {
        ArgumentException.ThrowIfNullOrEmpty(bindingId);
        lock (_gate)
        {
            _lastDispatch.Remove(bindingId);
            return _bindings.Remove(bindingId);
        }
    }

    public IReadOnlyList<TriggerDispatch> Simulate(
        TriggerDescriptor trigger,
        TriggerPayload payload = default,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(trigger.SourceId);
        ArgumentException.ThrowIfNullOrEmpty(trigger.Key);
        var now = timestamp ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var matches = _bindings.Values
                .Where(binding => Matches(binding.Trigger, trigger))
                .OrderBy(binding => binding.Id, StringComparer.Ordinal)
                .ToArray();
            var dispatched = new List<TriggerDispatch>(matches.Length);
            foreach (var binding in matches)
            {
                if (_lastDispatch.TryGetValue(binding.Id, out var last)
                    && binding.Debounce > TimeSpan.Zero
                    && now - last < binding.Debounce)
                {
                    continue;
                }

                var dispatch = new TriggerDispatch(binding.Id, binding.Action, payload, now);
                _lastDispatch[binding.Id] = now;
                _dispatches.Add(dispatch);
                dispatched.Add(dispatch);
            }

            return dispatched;
        }
    }

    public void RegisterWith(TriggerBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        foreach (var binding in Bindings)
        {
            var trigger = binding.Trigger;
            bus.Register(binding.Id, (in TriggerPayload payload) => Simulate(trigger, payload));
        }
    }

    private static bool Matches(TriggerDescriptor left, TriggerDescriptor right) =>
        left.SourceKind == right.SourceKind
        && string.Equals(left.SourceId, right.SourceId, StringComparison.Ordinal)
        && string.Equals(left.Key, right.Key, StringComparison.Ordinal);
}
