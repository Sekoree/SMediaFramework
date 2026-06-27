using System.Xml.Linq;
using Xunit;

namespace S.Media.Arch.Tests;

/// <summary>
/// Enforces the layered dependency rules from <c>Next/01-Architecture-and-Principles.md</c> §3:
/// dependencies point down only, and each project may reference only its allowed set. The test reads
/// the <c>next/</c> <c>*.csproj</c> graph directly, so it is independent of build order.
/// </summary>
public sealed class ArchitectureTests
{
    // project -> ProjectReference target names it is ALLOWED to have. Native-wrapper names
    // (PALib/MALib/NDILib/OSCLib/PMLib/LibAssLib) are pre-listed so the phase that moves them in needs
    // no edit here; they simply don't exist on disk yet. Keep in sync with 01 §3.
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        ["S.Media.Core"] = [],
        ["S.Media.Time"] = ["S.Media.Core"],
        ["S.Media.Routing"] = ["S.Media.Core", "S.Media.Time"],
        ["S.Media.Gpu"] = ["S.Media.Core"],
        ["S.Media.Compositor"] = ["S.Media.Core", "S.Media.Gpu"],
        ["S.Media.Players"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing"],
        ["S.Media.Session"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "S.Media.Players", "S.Media.Compositor"],
        ["S.Media.FFmpeg.Common"] = ["S.Media.Core"],
        // Time: the FFmpeg-backed audio output wrappers (ResamplingAudioOutput / AdaptiveRateAudioOutput)
        // forward IPlaybackClock, which lives in S.Media.Time. Downward ref (Time is tier 2); the module
        // keeps the cohesive FFmpeg audio-processing set together rather than spinning up a new project.
        ["S.Media.Decode.FFmpeg"] = ["S.Media.Core", "S.Media.Time", "S.Media.FFmpeg.Common"],
        ["S.Media.Encode.FFmpeg"] = ["S.Media.Core", "S.Media.FFmpeg.Common"],
        ["S.Media.Audio.PortAudio"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "PALib"],
        ["S.Media.Audio.MiniAudio"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "MALib"],
        ["S.Media.Present.SDL3"] = ["S.Media.Core", "S.Media.Gpu"],
        // The SDL3<->Compositor bridge (D7): the one place SDL3 + Compositor meet, kept out of the
        // Present.SDL3 presenter so that stays [Core, Gpu]. References Present.SDL3 for SDL3Runtime only.
        ["S.Media.Present.SDL3.Compositor"] = ["S.Media.Core", "S.Media.Gpu", "S.Media.Compositor", "S.Media.Present.SDL3"],
        ["S.Media.Present.Avalonia"] = ["S.Media.Core", "S.Media.Gpu"],
        ["S.Media.NDI"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "NDILib"],
        ["S.Media.Images.Skia"] = ["S.Media.Core"],
        ["S.Media.Subtitles"] = ["S.Media.Core", "LibAssLib"],
        ["S.Control.Abstractions"] = ["OSCLib"],
        ["S.Control"] = ["S.Media.Core", "S.Media.Session", "S.Control.Abstractions", "PMLib", "OSCLib"],
        ["S.Abi"] = ["S.Media.Core", "S.Media.Time", "S.Media.Compositor", "S.Control.Abstractions"],
        // S.Media.Interop is the host: it bundles the backend modules it ships (Phase 7).
        ["S.Media.Interop"] =
        [
            "S.Media.Core", "S.Media.Session", "S.Media.Time", "S.Media.Routing", "S.Media.Gpu",
            "S.Media.Compositor", "S.Media.Players", "S.Media.FFmpeg.Common", "S.Media.Decode.FFmpeg",
            "S.Media.Encode.FFmpeg", "S.Media.Audio.PortAudio", "S.Media.Audio.MiniAudio",
            "S.Media.Present.SDL3", "S.Media.Present.Avalonia", "S.Media.NDI", "S.Media.Images.Skia",
            "S.Media.Subtitles",
        ],
    };

    // Framework subtrees that MUST be covered by the Allowed map (Tools/ and Test/ are harness, exempt).
    private static readonly string[] FrameworkDirs = ["Media", "Control", "Interop"];

    private static string NextRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.Next.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.Next.sln above the test output directory.");
        return dir!.FullName;
    }

    private static IEnumerable<string> FrameworkProjects(string root)
    {
        var fw = Path.Combine(root, "MediaFramework");
        foreach (var sub in FrameworkDirs)
        {
            var d = Path.Combine(fw, sub);
            if (!Directory.Exists(d))
                continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
                yield return f;
        }
    }

    private static string[] ProjectRefNames(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFileNameWithoutExtension(s!.Replace('\\', '/')))
            .ToArray();

    [Fact]
    public void EveryFrameworkProjectIsRegisteredInTheRules()
    {
        foreach (var csproj in FrameworkProjects(NextRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            Assert.True(Allowed.ContainsKey(name),
                $"'{name}' is not in the Allowed map. Add it here and to Next/01 §3 before adding the project.");
        }
    }

    [Fact]
    public void ProjectReferencesAreDownwardAndAllowed()
    {
        var violations = new List<string>();
        foreach (var csproj in FrameworkProjects(NextRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (!Allowed.TryGetValue(name, out var allowed))
                continue;
            foreach (var dep in ProjectRefNames(csproj))
                if (!allowed.Contains(dep))
                    violations.Add($"{name} -> {dep}");
        }

        Assert.True(violations.Count == 0,
            "Disallowed project references (violate Next/01 §3 — fix the ref or update the rules):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void CoreHasNoProjectReferences()
    {
        var core = FrameworkProjects(NextRoot())
            .Single(f => Path.GetFileNameWithoutExtension(f) == "S.Media.Core");
        Assert.Empty(ProjectRefNames(core));
    }

    [Theory]
    [InlineData("MIDI/PMLib/PMLib.csproj")]
    [InlineData("OSC/OSCLib/OSCLib.csproj")]
    public void TransportWrappersHaveNoFrameworkProjectReferences(string relativePath)
    {
        var project = Path.Combine(NextRoot(), "MediaFramework", relativePath);
        Assert.Empty(ProjectRefNames(project));
    }

    [Fact]
    public void EveryProjectReferencePathResolves()
    {
        var missing = new List<string>();
        foreach (var csproj in FrameworkProjects(NextRoot()))
        {
            var dir = Path.GetDirectoryName(csproj)!;
            foreach (var inc in XDocument.Load(csproj).Descendants("ProjectReference")
                         .Select(e => (string?)e.Attribute("Include"))
                         .Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var full = Path.GetFullPath(Path.Combine(dir, inc!.Replace('\\', '/')));
                if (!File.Exists(full))
                    missing.Add($"{Path.GetFileNameWithoutExtension(csproj)} -> {inc}");
            }
        }

        Assert.True(missing.Count == 0, "Dangling project references:\n  " + string.Join("\n  ", missing));
    }
}
