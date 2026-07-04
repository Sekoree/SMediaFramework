namespace S.Media.Time;

/// <summary>
/// Optional behaviour for <see cref="CompositePlaybackClock"/> beyond priority snap.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HandoffCrossFade"/> ramps reported <see cref="IPlaybackClock.ElapsedSinceStart"/> when the advancing
/// <strong>winner identity</strong> changes. <see cref="CoAdvanceSmoothingTau"/> applies exponential smoothing toward
/// the same priority winner while <strong>at least two</strong> candidates are advancing together (no winner change).
/// </para>
/// </remarks>
public readonly record struct CompositePlaybackClockBlend
{
    /// <summary>
    /// When positive and multiple advancing candidates exist, reported <see cref="IPlaybackClock.ElapsedSinceStart"/>
    /// lerps from the last emitted value toward the current priority winner over this wall-clock span whenever the
    /// winner <strong>identity</strong> changes (handoff ramp).
    /// </summary>
    public TimeSpan HandoffCrossFade { get; init; }

    /// <summary>
    /// When positive and <strong>at least two</strong> candidates are advancing, each <see cref="IPlaybackClock.ElapsedSinceStart"/>
    /// read applies a first-order low-pass toward the priority winner: <c>α = 1 - exp(-Δt/τ)</c> with wall <c>Δt</c>
    /// between reads and time constant <c>τ</c> = <see cref="CoAdvanceSmoothingTau"/>. When only one candidate advances,
    /// smoothing is bypassed and the leader's elapsed is returned directly. During an active <see cref="HandoffCrossFade"/>,
    /// co-advance smoothing is deferred until the handoff completes.
    /// </summary>
    public TimeSpan CoAdvanceSmoothingTau { get; init; }

    /// <summary>True when <see cref="HandoffCrossFade"/> is positive.</summary>
    public bool HasHandoffCrossFade => HandoffCrossFade > TimeSpan.Zero;

    /// <summary>True when <see cref="CoAdvanceSmoothingTau"/> is positive.</summary>
    public bool HasCoAdvanceSmoothing => CoAdvanceSmoothingTau > TimeSpan.Zero;

    /// <summary>Priority snap only (default <see cref="CompositePlaybackClock"/> ctor).</summary>
    public static CompositePlaybackClockBlend Disabled => default;
}
