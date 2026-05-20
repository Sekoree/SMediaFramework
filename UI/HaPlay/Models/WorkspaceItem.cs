namespace HaPlay.Models;

/// <summary>
/// One entry in the sidebar (§12.1). The sidebar lists these in display order; <see cref="MainViewModel"/>
/// switches the content area based on which one is selected.
/// </summary>
public sealed record WorkspaceItem(string Id, string Label, string Glyph)
{
    public static readonly WorkspaceItem Players = new("players", "Players", "▶");
    public static readonly WorkspaceItem Cues = new("cues", "Cues", "●");
    public static readonly WorkspaceItem Outputs = new("outputs", "Outputs", "⎚");
    public static readonly WorkspaceItem Project = new("project", "Project", "🗂");
}
