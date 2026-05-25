namespace S.Media.Effects;

/// <summary>Easing curves for <see cref="LayerOpacityTween"/>.</summary>
public enum LayerEasing
{
    /// <summary>Linear interpolation: <c>t</c>.</summary>
    Linear = 0,

    /// <summary>Sinusoidal ease-in-out: <c>0.5 - 0.5 * cos(π * t)</c>. Smooth at both ends.</summary>
    EaseInOutSine = 1,

    /// <summary>Cubic ease-in-out: <c>4t³</c> for <c>t &lt; 0.5</c>, else <c>1 - (-2t + 2)³ / 2</c>.</summary>
    EaseInOutCubic = 2,
}

/// <summary>
/// Tween helper for driving <see cref="VideoCompositorSource.Slot.Opacity"/> over time. Stateless —
/// callers feed a timeline-relative elapsed time and apply the result to the slot.
/// </summary>
/// <param name="StartOpacity">Opacity at <c>elapsed = 0</c>.</param>
/// <param name="EndOpacity">Opacity at <c>elapsed &gt;= Duration</c>.</param>
/// <param name="Duration">Tween span. Zero duration jumps straight to <paramref name="EndOpacity"/>.</param>
/// <param name="Easing">Interpolation curve.</param>
/// <remarks>
/// <para>
/// Typical use: pair with a wall-clock or playback-clock baseline, then on each compositor tick:
/// <c>slot.Opacity = tween.OpacityAt(clock.Now - tweenStart);</c>.
/// </para>
/// <para>
/// Out-of-range elapsed values are clamped — negative returns <paramref name="StartOpacity"/>,
/// past-duration returns <paramref name="EndOpacity"/>. Output is always in <c>[0, 1]</c> after
/// clamping the inputs.
/// </para>
/// </remarks>
public readonly record struct LayerOpacityTween(
    float StartOpacity,
    float EndOpacity,
    TimeSpan Duration,
    LayerEasing Easing = LayerEasing.Linear)
{
    /// <summary>Sample the tween at <paramref name="elapsed"/> since the tween's start.</summary>
    public float OpacityAt(TimeSpan elapsed)
    {
        var s = Math.Clamp(StartOpacity, 0f, 1f);
        var e = Math.Clamp(EndOpacity, 0f, 1f);
        if (Duration <= TimeSpan.Zero || elapsed >= Duration) return e;
        if (elapsed <= TimeSpan.Zero) return s;
        var t = (float)(elapsed.TotalSeconds / Duration.TotalSeconds);
        var eased = Ease(t, Easing);
        return s + (e - s) * eased;
    }

    /// <summary>True when <paramref name="elapsed"/> is at or past <see cref="Duration"/>.</summary>
    public bool IsComplete(TimeSpan elapsed) => elapsed >= Duration;

    internal static float Ease(float t, LayerEasing easing) => easing switch
    {
        LayerEasing.EaseInOutSine => 0.5f - 0.5f * MathF.Cos(MathF.PI * t),
        LayerEasing.EaseInOutCubic => t < 0.5f
            ? 4f * t * t * t
            : 1f - MathF.Pow(-2f * t + 2f, 3f) / 2f,
        _ => t,
    };
}
