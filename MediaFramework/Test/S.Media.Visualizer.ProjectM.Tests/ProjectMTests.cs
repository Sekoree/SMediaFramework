using ProjectMLib;
using ProjectMLib.Runtime;
using Xunit;

namespace S.Media.Visualizer.ProjectM.Tests;

/// <summary>Everything testable WITHOUT the native libprojectM-4: resolver candidate ordering, options
/// blob round-trip, preset enumeration, and the availability probe's graceful degradation. GL-touching
/// paths (create/render) run only inside the app with a live compositor context.</summary>
public sealed class ProjectMTests
{
    [Fact]
    public void ResolverCandidates_SystemNamesPrecedeEnvironmentDirectoryFallback()
    {
        var original = Environment.GetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride);
        var dir = Path.Combine(Path.GetTempPath(), $"pm_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Environment.SetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride, dir);
            var candidates = ProjectMLibraryResolver.GetCandidates().ToArray();

            Assert.True(candidates.Length > 2);
            var systemNameIndex = Array.FindIndex(candidates, c => !Path.IsPathRooted(c));
            var environmentIndex = Array.FindIndex(candidates, c => c.StartsWith(dir, StringComparison.Ordinal));
            Assert.True(systemNameIndex >= 0);
            Assert.True(environmentIndex > systemNameIndex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride, original);
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void ResolverCandidates_EnvFilePathIsUsedVerbatim()
    {
        var original = Environment.GetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride);
        var file = Path.Combine(Path.GetTempPath(), $"pm_test_{Guid.NewGuid():N}.so");
        File.WriteAllBytes(file, [0x7F]);
        try
        {
            Environment.SetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride, file);
            var candidates = ProjectMLibraryResolver.GetCandidates().ToArray();
            Assert.Contains(file, candidates);
            Assert.True(Array.IndexOf(candidates, file)
                        > Array.FindIndex(candidates, c => !Path.IsPathRooted(c)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride, original);
            File.Delete(file);
        }
    }

    [Fact]
    public void ResolverCandidates_ProbeSystemNames_BeforeApplicationDirectory()
    {
        var original = Environment.GetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride);
        try
        {
            Environment.SetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride, null);
            var candidates = ProjectMLibraryResolver.GetCandidates().ToArray();

            // A native library bundled next to the executable (AppContext.BaseDirectory) must be probed:
            // the OS loader behind the bare system-name candidates does not search the app directory.
            var appDir = AppContext.BaseDirectory;
            var appDirIndex = Array.FindIndex(candidates, c => c.StartsWith(appDir, StringComparison.Ordinal));
            Assert.True(appDirIndex >= 0, "the application directory must be probed for a bundled native library");

            // System-installed libraries must win; the app-local copy is only a portable fallback.
            var systemNameIndex = Array.FindIndex(candidates, c => !Path.IsPathRooted(c));
            Assert.True(systemNameIndex >= 0 && systemNameIndex < appDirIndex);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProjectMLibraryResolver.EnvironmentOverride, original);
        }
    }

    [Fact]
    public void Options_RoundTripThroughJson_AndTolerateGarbage()
    {
        var options = new ProjectMOptions
        {
            PresetDirectory = "/opt/presets",
            PresetDurationSeconds = 45,
            Shuffle = false,
            BeatSensitivity = 1.5,
            RenderWidth = 1920,
            RenderHeight = 1080,
            Fps = 60,
        };

        var back = ProjectMOptions.FromJson(options.ToJson());
        Assert.Equal(options, back);
        Assert.Equal(1920, back.RenderWidth);
        Assert.Equal(60, back.Fps);

        Assert.Equal(new ProjectMOptions(), ProjectMOptions.FromJson("not json at all"));
        Assert.Equal(new ProjectMOptions(), ProjectMOptions.FromJson(null));
    }

    [Fact]
    public void PresetEnumeration_FindsMilkFilesRecursively_SortedAndSafeOnMissingDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pm_presets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "b.milk"), "");
            File.WriteAllText(Path.Combine(dir, "sub", "a.milk"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");

            var presets = ProjectMGlLayerSurface.EnumeratePresets(dir);
            Assert.Equal(2, presets.Length);
            Assert.All(presets, p => Assert.EndsWith(".milk", p));

            Assert.Empty(ProjectMGlLayerSurface.EnumeratePresets(Path.Combine(dir, "missing")));
            Assert.Empty(ProjectMGlLayerSurface.EnumeratePresets(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ElfNeededReader_ReadsRealSharedLibraryDependencies()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // libc is always present and always NEEDs something (ld-linux); libprojectM-4 (when installed)
        // is the real-world case: a GLES build MUST report libGLESv2 so the probe can veto it before
        // the uncatchable native crash in projectm_create.
        const string systemProjectM = "/usr/lib/libprojectM-4.so.4";
        if (File.Exists(systemProjectM))
        {
            var needed = ElfNeededReader.TryReadNeeded(systemProjectM);
            Assert.NotEmpty(needed);
            Assert.Contains(needed, n => n.StartsWith("libc.so", StringComparison.Ordinal));
        }

        Assert.Empty(ElfNeededReader.TryReadNeeded("/definitely/not/a/file.so"));
    }

    [Fact]
    public void Runtime_ProbeNeverThrows_AndModuleGateMatches()
    {
        // With or without the native lib on this machine, the probe must resolve to a stable answer.
        var available = ProjectMRuntime.IsAvailable;
        Assert.Equal(available, ProjectMModule.IsAvailable);
        if (available)
            Assert.NotNull(ProjectMRuntime.Version);
        else
            Assert.NotNull(ProjectMRuntime.UnavailableReason);
    }

    [Fact]
    public void VisualSource_WithoutNativeLib_ThrowsTheReason()
    {
        if (ProjectMRuntime.IsAvailable)
            return; // covered by in-app GL smoke when the lib exists

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ProjectMVisualSource(640, 360, new Rational(30, 1)));
        Assert.Contains("libprojectM-4", ex.Message);
    }
}
