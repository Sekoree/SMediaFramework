using Avalonia.Media;
using HaPlay.Views;

namespace HaPlay.Models;

/// <summary>
/// One entry in the sidebar (§12.1). The sidebar lists these in display order; <see cref="MainViewModel"/>
/// switches the content area based on which one is selected. Icons are vector geometries from
/// <see cref="AppIcons"/> (the former emoji glyphs rendered as tofu without a color-emoji font),
/// resolved LAZILY: parsing a StreamGeometry needs the Avalonia platform, and these static
/// instances are touched by platform-free code paths (settings migration, unit tests) too.
/// </summary>
public sealed record WorkspaceItem(string Id, string Label, Func<StreamGeometry> IconFactory)
{
    /// <summary>The sidebar icon — evaluated at bind time on the UI thread (platform present).</summary>
    public StreamGeometry Icon => IconFactory();

    public static readonly WorkspaceItem Players = new("players", "Players", static () => AppIcons.Play);
    public static readonly WorkspaceItem Cues = new("cues", "Cues", static () => AppIcons.Cue);
    public static readonly WorkspaceItem Soundboard = new("soundboard", "Soundboard", static () => AppIcons.Grid);
    public static readonly WorkspaceItem Control = new("control", "Control", static () => AppIcons.Sliders);
    /// <summary>UI rewrite P1/P2: Outputs + MIDI ports (+ OSC connections from P2 on) merged into
    /// one I/O workspace. The legacy "outputs"/"midi" ids are migrated to this on settings load.</summary>
    public static readonly WorkspaceItem Io = new("io", "I/O", static () => AppIcons.SwapArrows);
    public static readonly WorkspaceItem Project = new("project", "Project", static () => AppIcons.Folder);

    /// <summary>Maps pre-P1 persisted workspace ids onto the merged set (P1 migration).</summary>
    public static string? MigrateLegacyId(string? id) => id switch
    {
        "outputs" or "midi" => Io.Id,
        _ => id,
    };
}
