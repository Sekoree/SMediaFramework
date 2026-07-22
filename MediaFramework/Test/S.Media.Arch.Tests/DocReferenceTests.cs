using System.Text.RegularExpressions;
using Xunit;

namespace S.Media.Arch.Tests;

/// <summary>
/// Every <c>Doc/&lt;name&gt;.md</c> reference in tracked source/docs must point at a file that exists
/// (review P2-9): the Doc tree was once deleted wholesale, silently orphaning design/incident pointers
/// and turning guide-compiling tests into green no-ops. This is the lightweight link check that makes
/// the next deletion loud.
/// </summary>
public sealed class DocReferenceTests
{
    private static readonly Regex DocReference = new(@"Doc/[A-Za-z0-9._/-]+?\.md", RegexOptions.Compiled);

    [Fact]
    public void EveryDocReferenceInSourceResolvesToAFile()
    {
        var repoRoot = FindRepoRoot();
        var missing = new List<string>();

        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in DocReference.Matches(text))
            {
                var referenced = Path.Combine(repoRoot, match.Value);
                if (!File.Exists(referenced))
                    missing.Add($"{Path.GetRelativePath(repoRoot, file)} → {match.Value}");
            }
        }

        Assert.True(
            missing.Count == 0,
            "Dangling Doc/ references (restore the document or update the pointer):\n  "
            + string.Join("\n  ", missing.Distinct()));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string repoRoot)
    {
        string[] roots = ["MediaFramework", "UI", "Doc", "scripts"];
        string[] extensions = [".cs", ".axaml", ".md", ".sh", ".yml"];
        foreach (var root in roots)
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                var relative = Path.GetRelativePath(repoRoot, file);
                if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;
                yield return file;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MFPlayer.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not locate the repository root from the test base directory");
        return dir!;
    }
}
