namespace S.Media.Effects;

/// <summary>Timed change to a layer's <see cref="LayerConfig"/> (evaluated on each composite).</summary>
public abstract record Transition
{
    public LayerEasing Easing { get; init; } = LayerEasing.Linear;

    /// <summary>Instant cut to <paramref name="config"/> at the scheduled time.</summary>
    public static Transition Cut(LayerConfig config) => new CutTransition(config);

    /// <summary>Fade opacity to <paramref name="opacity"/> over <paramref name="duration"/>.</summary>
    public static Transition FadeTo(float opacity, TimeSpan duration, LayerEasing easing = LayerEasing.EaseInOutSine) =>
        new FadeToTransition(opacity, duration) { Easing = easing };

    /// <summary>Move to a new position preset over <paramref name="duration"/>.</summary>
    public static Transition MoveTo(LayerPosition position, TimeSpan duration) =>
        new MoveToTransition(position, duration);

    /// <summary>Scale to <paramref name="scale"/> over <paramref name="duration"/>.</summary>
    public static Transition ScaleTo(float scale, TimeSpan duration) =>
        new ScaleToTransition(scale, duration);

    /// <summary>Run transitions in parallel from the same start time.</summary>
    public static Transition Combo(params Transition[] transitions) => new ComboTransition(transitions);

    /// <summary>Run transitions one after another.</summary>
    public static Transition Sequence(params Transition[] transitions) => new SequenceTransition(transitions);

    internal sealed record CutTransition(LayerConfig Config) : Transition;
    internal sealed record FadeToTransition(float Opacity, TimeSpan Duration) : Transition;
    internal sealed record MoveToTransition(LayerPosition Position, TimeSpan Duration) : Transition;
    internal sealed record ScaleToTransition(float Scale, TimeSpan Duration) : Transition;
    internal sealed record ComboTransition(Transition[] Items) : Transition;
    internal sealed record SequenceTransition(Transition[] Items) : Transition;
}
