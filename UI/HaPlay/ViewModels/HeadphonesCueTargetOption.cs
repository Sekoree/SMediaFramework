using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// §8.2 — combobox item shown in the per-player headphones cue picker. Either wraps a direct
/// PortAudio <see cref="OutputLineViewModel"/> or a project-level
/// <see cref="SharedHeadphonesBusViewModel"/>. Resolution to the underlying line happens via
/// <see cref="ResolvedLine"/>, which may be <c>null</c> for a bus whose PA target was removed
/// (broken bus). Equality is by <see cref="Identity"/> (the persisted Guid) so reassigning the
/// collection preserves selection.
/// </summary>
public sealed class HeadphonesCueTargetOption : IEquatable<HeadphonesCueTargetOption>
{
    public enum TargetKind { Direct, SharedBus }

    public TargetKind Kind { get; }

    /// <summary>Stable id: the PA output's id for direct targets, or the bus's id for shared buses.</summary>
    public Guid Identity { get; }

    /// <summary>Direct PA line backing a <see cref="TargetKind.Direct"/> option. <c>null</c> for bus options.</summary>
    public OutputLineViewModel? Direct { get; }

    /// <summary>Shared bus backing a <see cref="TargetKind.SharedBus"/> option. <c>null</c> for direct options.</summary>
    public SharedHeadphonesBusViewModel? Bus { get; }

    /// <summary>The PA output line that the cue audio should route through. <c>null</c> when a bus
    /// has no PA target configured (or it was removed). Direct options always resolve to
    /// <see cref="Direct"/>.</summary>
    public OutputLineViewModel? ResolvedLine { get; }

    public string Display => Kind == TargetKind.SharedBus && Bus is not null
        ? (ResolvedLine is null
            ? Strings.Format(nameof(Strings.HeadphonesCueBusNoTargetFormat), Bus.Label)
            : Strings.Format(nameof(Strings.HeadphonesCueBusTargetFormat), Bus.Label, ResolvedLine.Definition.DisplayName))
        : Direct?.Definition.DisplayName ?? string.Empty;

    public bool IsBroken => Kind == TargetKind.SharedBus && ResolvedLine is null;

    public static HeadphonesCueTargetOption ForDirect(OutputLineViewModel line) =>
        new(TargetKind.Direct, line.Definition.Id, line, bus: null, resolved: line);

    public static HeadphonesCueTargetOption ForBus(SharedHeadphonesBusViewModel bus, OutputLineViewModel? resolved) =>
        new(TargetKind.SharedBus, bus.Id, direct: null, bus: bus, resolved: resolved);

    private HeadphonesCueTargetOption(
        TargetKind kind,
        Guid identity,
        OutputLineViewModel? direct,
        SharedHeadphonesBusViewModel? bus,
        OutputLineViewModel? resolved)
    {
        Kind = kind;
        Identity = identity;
        Direct = direct;
        Bus = bus;
        ResolvedLine = resolved;
    }

    public bool Equals(HeadphonesCueTargetOption? other) =>
        other is not null && Kind == other.Kind && Identity == other.Identity;

    public override bool Equals(object? obj) => Equals(obj as HeadphonesCueTargetOption);

    public override int GetHashCode() => HashCode.Combine(Kind, Identity);

    public override string ToString() => Display;
}
