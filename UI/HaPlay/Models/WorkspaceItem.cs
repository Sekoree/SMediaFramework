namespace HaPlay.Models;

/// <summary>
/// One entry in the sidebar (§12.1). The sidebar lists these in display order; <see cref="MainViewModel"/>
/// switches the content area based on which one is selected.
/// </summary>
public sealed record WorkspaceItem(string Id, string Label, string Glyph)
{
    public static readonly WorkspaceItem Players = new("players", "Players", "▶");
    public static readonly WorkspaceItem Cues = new("cues", "Cues", "●");
    public static readonly WorkspaceItem Control = new("control", "Control", "⌘");
    /// <summary>UI rewrite P1/P2: Outputs + MIDI ports (+ OSC connections from P2 on) merged into
    /// one I/O workspace. The legacy "outputs"/"midi" ids are migrated to this on settings load.</summary>
    public static readonly WorkspaceItem Io = new("io", "I/O", "⇄");
    public static readonly WorkspaceItem Project = new("project", "Project", "🗂");

    /// <summary>Maps pre-P1 persisted workspace ids onto the merged set (P1 migration).</summary>
    public static string? MigrateLegacyId(string? id) => id switch
    {
        "outputs" or "midi" => Io.Id,
        _ => id,
    };
}
