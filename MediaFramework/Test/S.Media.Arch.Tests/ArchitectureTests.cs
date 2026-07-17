using System.Xml.Linq;
using Xunit;

namespace S.Media.Arch.Tests;

/// <summary>
/// Enforces the layered dependency rules: dependencies point down only, and each project may reference
/// only its allowed set. The test reads the <c>*.csproj</c> graph directly, so it is independent of build
/// order. Every first-party production project (framework libraries + native wrappers) must appear in
/// <see cref="Allowed"/>; <c>Tools/</c> and <c>Test/</c> are harness and exempt.
/// </summary>
public sealed class ArchitectureTests
{
    // project -> ProjectReference target names it is ALLOWED to have. Native-wrapper projects
    // (PALib/MALib/NDILib/OSCLib/PMLib/LibAssLib) reference no other first-party project, so their allowed
    // set is empty.
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
        // Encode module (Tier 3b slot from the rewrite docs): packet-producing encoders + mux sinks
        // behind IVideoOutput/IAudioOutput. Never references Decode.FFmpeg - shared glue lives in Common.
        ["S.Media.Encode.FFmpeg"] = ["S.Media.Core", "S.Media.FFmpeg.Common"],
        // LAN streaming server over the encode module's packet-sink seam. Sockets stay OUT of the
        // encode project; this is the one place HTTP meets the muxers.
        ["S.Media.Stream.Http"] = ["S.Media.Core", "S.Media.Encode.FFmpeg"],
        // projectM visualizer: an effect-bus visual source + NXT-10 GL layer surface (Compositor for
        // the surface contract, like Source.MMD). ProjectMLib is its dedicated P/Invoke binding.
        ["S.Media.Visualizer.ProjectM"] = ["S.Media.Core", "S.Media.Compositor", "ProjectMLib"],
        ["ProjectMLib"] = [],
        // External-source module (Gate 5): YoutubeExplode is an out-of-tree LOCAL SOURCE reference
        // (Reference/YoutubeExplode-6.6) and deliberately not part of the layering table.
        ["S.Media.Source.YouTube"] =
            ["S.Media.Core", "S.Media.FFmpeg.Common", "S.Media.Decode.FFmpeg", "S.Media.Time", "YoutubeExplode"],
        // MMD prototype (Gate 6): pure managed PMX/VMD + software render, plus the NXT-10 GL
        // layer-surface renderer (Compositor for the surface contract; Silk.NET bindings and
        // StbImageSharp are pure managed - a GL context only ever comes from the hosting compositor,
        // so the module still ships no native runtime of its own).
        ["S.Media.Source.MMD"] = ["S.Media.Core", "S.Media.Time", "S.Media.Compositor"],
        // Text cue source (SESSION-02): a pure-managed SkiaSharp text rasterizer + held-frame source. References
        // Decode.FFmpeg only for the swscale CPU converter that repacks the rendered BGRA card to the negotiated
        // output format. SkiaSharp is a NuGet package (isolated here), not a first-party project ref.
        ["S.Media.Source.Text"] = ["S.Media.Core", "S.Media.Decode.FFmpeg"],
        ["S.Media.Audio.PortAudio"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "PALib"],
        ["S.Media.Audio.MiniAudio"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "MALib"],
        ["S.Media.Present.SDL3"] = ["S.Media.Core", "S.Media.Gpu"],
        // The SDL3<->Compositor bridge (D7): the one place SDL3 + Compositor meet, kept out of the
        // Present.SDL3 presenter so that stays [Core, Gpu]. References Present.SDL3 for SDL3Runtime only.
        ["S.Media.Present.SDL3.Compositor"] = ["S.Media.Core", "S.Media.Gpu", "S.Media.Compositor", "S.Media.Present.SDL3"],
        ["S.Media.Present.Avalonia"] = ["S.Media.Core", "S.Media.Gpu"],
        ["S.Media.NDI"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "NDILib"],
        ["S.Media.Subtitles"] = ["S.Media.Core", "LibAssLib"],
        ["S.Control.Abstractions"] = ["OSCLib"],
        ["S.Control"] = ["S.Media.Core", "S.Media.Session", "S.Control.Abstractions", "PMLib", "OSCLib"],
        ["S.Abi"] = ["S.Media.Core", "S.Media.Time", "S.Media.Compositor", "S.Control.Abstractions"],
        // S.Media.Interop is the host: it bundles the backend modules it ships (Phase 7).
        ["S.Media.Interop"] =
        [
            "S.Media.Core", "S.Media.Session", "S.Media.Time", "S.Media.Routing", "S.Media.Gpu",
            "S.Media.Compositor", "S.Media.Players", "S.Media.FFmpeg.Common", "S.Media.Decode.FFmpeg",
            "S.Media.Audio.PortAudio", "S.Media.Audio.MiniAudio",
            "S.Media.Present.SDL3", "S.Media.Present.Avalonia", "S.Media.NDI",
            "S.Media.Subtitles",
        ],
        // Native-runtime wrapper projects: pure P/Invoke bindings, no first-party project references.
        ["PALib"] = [],
        ["MALib"] = [],
        ["PMLib"] = [],
        ["NDILib"] = [],
        ["OSCLib"] = [],
        ["LibAssLib"] = [],
    };

    // First-party production subtrees that MUST be covered by the Allowed map (Tools/ and Test/ are harness,
    // exempt). Includes the native-wrapper trees (TEST-02) so PALib/MALib/PMLib/NDILib/OSCLib/LibAssLib are
    // checked too, not just the S.Media.* / S.Control.* / S.Abi projects.
    private static readonly string[] FrameworkDirs =
        ["Media", "Control", "Interop", "Audio", "MIDI", "NDI", "OSC", "Subtitles", "Visualizer"];

    private static string RepoRoot()  // repo root = the directory holding MFPlayer.sln
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.sln above the test output directory.");
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
        foreach (var csproj in FrameworkProjects(RepoRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            Assert.True(Allowed.ContainsKey(name),
                $"'{name}' is not in the Allowed map. Add it here (with its allowed downward references) before adding the project.");
        }
    }

    [Fact]
    public void ProjectReferencesAreDownwardAndAllowed()
    {
        var violations = new List<string>();
        foreach (var csproj in FrameworkProjects(RepoRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (!Allowed.TryGetValue(name, out var allowed))
                continue;
            foreach (var dep in ProjectRefNames(csproj))
                if (!allowed.Contains(dep))
                    violations.Add($"{name} -> {dep}");
        }

        Assert.True(violations.Count == 0,
            "Disallowed project references (violate the layering rules - fix the ref or update the Allowed map):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void CoreHasNoProjectReferences()
    {
        var core = FrameworkProjects(RepoRoot())
            .Single(f => Path.GetFileNameWithoutExtension(f) == "S.Media.Core");
        Assert.Empty(ProjectRefNames(core));
    }

    [Theory]
    [InlineData("MIDI/PMLib/PMLib.csproj")]
    [InlineData("OSC/OSCLib/OSCLib.csproj")]
    [InlineData("Audio/PALib/PALib.csproj")]
    [InlineData("Audio/MALib/MALib.csproj")]
    [InlineData("NDI/NDILib/NDILib.csproj")]
    [InlineData("Subtitles/LibAssLib/LibAssLib.csproj")]
    public void NativeWrappersHaveNoFrameworkProjectReferences(string relativePath)
    {
        var project = Path.Combine(RepoRoot(), "MediaFramework", relativePath);
        Assert.Empty(ProjectRefNames(project));
    }

    [Theory]
    [InlineData("Audio/PALib/PALib.csproj")]
    [InlineData("Audio/MALib/MALib.csproj")]
    [InlineData("MIDI/PMLib/PMLib.csproj")]
    [InlineData("NDI/NDILib/NDILib.csproj")]
    [InlineData("Subtitles/LibAssLib/LibAssLib.csproj")]
    [InlineData("Visualizer/ProjectMLib/ProjectMLib.csproj")]
    [InlineData("Media/S.Media.Source.MMD/S.Media.Source.MMD.csproj")]
    [InlineData("Media/S.Media.Present.SDL3/S.Media.Present.SDL3.csproj")]
    public void BundledNativeWrappersUseSharedSystemFirstResolverPolicy(string relativePath)
    {
        var project = Path.Combine(RepoRoot(), "MediaFramework", relativePath);
        var linkedSources = XDocument.Load(project).Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include));

        Assert.Contains(linkedSources, include =>
            include!.Replace('\\', '/').EndsWith("Shared/SystemFirstNativeLibraryResolver.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void EverySolutionProjectExistsOnDisk()
    {
        // A fresh clone must build: an sln entry whose csproj was never committed (the review P2-5
        // meta packages were once added to the sln without `git add`ing the new Packages/ directory)
        // fails every `dotnet build MFPlayer.sln` with MSB3202. Packages/ is outside FrameworkDirs,
        // so only this check covers it.
        var root = RepoRoot();
        var missing = new List<string>();
        foreach (var line in File.ReadLines(Path.Combine(root, "MFPlayer.sln")))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, "= \"[^\"]+\", \"([^\"]+\\.csproj)\"");
            if (!match.Success)
                continue;
            var path = Path.Combine(root, match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                missing.Add(match.Groups[1].Value);
        }

        Assert.True(missing.Count == 0,
            "MFPlayer.sln references project files that do not exist on disk (forgotten `git add`?):\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryProjectReferencePathResolves()
    {
        var missing = new List<string>();
        foreach (var csproj in FrameworkProjects(RepoRoot()))
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
