namespace S.Media.Effects;

/// <summary>How a <see cref="VideoCompositorSource.Slot"/> chooses which submitted frame to
/// expose at composite time.</summary>
public enum SlotKeepPolicy
{
    /// <summary>Most recent submit wins — default; matches pre-5.9 behavior.</summary>
    Latest,

    /// <summary>Frame whose presentation time is closest to the master clock position at
    /// composite time. Older frames are dropped; frames too far in the future are held.</summary>
    MasterAligned,
}
