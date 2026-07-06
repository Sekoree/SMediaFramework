using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

/// <summary>
/// UX-07: user-facing UI text must go through the <c>Strings</c> resource system, not be hardcoded in AXAML. This
/// lint scans the Views for raw literal <c>Text</c>/<c>Content</c>/<c>Header</c>/<c>Title</c>/<c>ToolTip.Tip</c>/
/// <c>Watermark</c> values that look like words — exempting bindings, <c>x:Static</c>, glyph entities, and single
/// symbols — and fails if the count exceeds the tracked baseline. So no NEW hardcoded string is added, and the
/// existing debt is migrated down over time (lower <see cref="Baseline"/> as strings move to Strings.resx; the
/// test prints the new floor when it drops). See <c>MIDIDevicesView</c> for the migration pattern.
/// </summary>
public sealed class RawStringLiteralLintTests(ITestOutputHelper output)
{
    // Tracked debt of hardcoded user-facing literals still in the Views (concentrated in ControlWorkspaceView,
    // CuePlayerView, ScriptEditorWindow). RATCHET ONLY DOWNWARD — never raise this to accommodate a new literal.
    private const int Baseline = 168;

    private static readonly Regex Attr = new(
        @"\b(Text|Content|Header|Title|ToolTip\.Tip|Watermark)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex GlyphEntity = new(@"^\s*(&#x?[0-9A-Fa-f]+;\s*)+$", RegexOptions.Compiled);

    [Fact]
    public void Views_DoNotAddRawUserFacingStringLiterals()
    {
        var viewsDir = Path.Combine(RepoRoot(), "UI", "HaPlay", "Views");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(viewsDir, "*.axaml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Attr.Matches(text))
                if (IsUserFacing(m.Groups[2].Value))
                    offenders.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}=\"{m.Groups[2].Value}\"");
        }

        output.WriteLine($"raw user-facing literals: {offenders.Count} (baseline {Baseline})");

        Assert.True(offenders.Count <= Baseline,
            $"hardcoded user-facing string literals grew to {offenders.Count} (baseline {Baseline}). Route new UI " +
            $"text through Resources/Strings.resx + a Strings accessor (see MIDIDevicesView). Newest offenders:\n  " +
            string.Join("\n  ", offenders.TakeLast(15)));

        if (offenders.Count < Baseline)
            output.WriteLine($"NOTE: strings were migrated — lower the Baseline constant to {offenders.Count}.");
    }

    private static bool IsUserFacing(string value)
    {
        if (value.StartsWith('{')) return false;          // {Binding} / {x:Static ...}
        if (GlyphEntity.IsMatch(value)) return false;      // icon glyphs encoded as &#x…; entities
        return value.Count(char.IsLetter) >= 2;            // real words, not symbols / numbers / single chars
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.sln above the test output directory.");
        return dir!.FullName;
    }
}
