namespace HaPlay.Models;

/// <summary>
/// Section ids for scoped project save/export and section-aware load (save/load rework, 2026-06-10).
/// </summary>
/// <remarks>
/// <para>
/// One format covers everything: a <see cref="HaPlayProject"/> file whose
/// <see cref="HaPlayProject.SavedSections"/> lists which sections its payload actually carries.
/// <c>null</c> means a full project (back-compat with every existing file). Opening a partial file
/// applies <em>only</em> the listed sections and leaves the rest of the live state untouched, so the
/// same mechanism is both "export outputs only" and "import a colleague's cue lists into my show".
/// </para>
/// <para>
/// Dedicated single-item formats (one cue list, one player config, one playlist tab, the control
/// config) keep their existing commands/extensions — this layer is for section-level scoping.
/// </para>
/// </remarks>
public static class ProjectSections
{
    // Parent sections.
    public const string Outputs = "outputs";
    public const string ActionTargets = "targets";
    public const string Players = "players";
    public const string CueLists = "cueLists";
    public const string Soundboards = "soundboards";
    public const string Control = "control";

    // Sub-sections (finer I/O split — the "checkboxes instead of a menu per combination" ask).
    public const string OutputsAudio = "outputs.audio";
    public const string OutputsVideo = "outputs.video";
    public const string TargetsMIDI = "targets.midi";
    public const string TargetsOSC = "targets.osc";

    /// <summary>Every selectable leaf section, in display order.</summary>
    public static readonly IReadOnlyList<string> Leaves =
    [
        OutputsAudio,
        OutputsVideo,
        TargetsMIDI,
        TargetsOSC,
        Players,
        CueLists,
        Soundboards,
        Control,
    ];

    /// <summary>True when <paramref name="savedSections"/> (null = full project) covers
    /// <paramref name="leaf"/> — either directly or via its parent section id.</summary>
    public static bool Includes(IReadOnlyCollection<string>? savedSections, string leaf)
    {
        if (savedSections is null)
            return true;
        if (savedSections.Contains(leaf))
            return true;
        var dot = leaf.IndexOf('.');
        return dot > 0 && savedSections.Contains(leaf[..dot]);
    }

    /// <summary>Normalizes a leaf set: when both children of a parent are present they collapse to
    /// the parent id, keeping saved files tidy and order-independent.</summary>
    public static List<string> Normalize(IEnumerable<string> leaves)
    {
        var set = new HashSet<string>(leaves, StringComparer.Ordinal);
        CollapsePair(set, OutputsAudio, OutputsVideo, Outputs);
        CollapsePair(set, TargetsMIDI, TargetsOSC, ActionTargets);
        return set.Order(StringComparer.Ordinal).ToList();
    }

    private static void CollapsePair(HashSet<string> set, string a, string b, string parent)
    {
        if (set.Contains(a) && set.Contains(b))
        {
            set.Remove(a);
            set.Remove(b);
            set.Add(parent);
        }
    }
}
