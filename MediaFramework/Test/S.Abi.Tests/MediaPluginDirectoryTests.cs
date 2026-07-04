using System.Diagnostics;
using S.Abi;
using S.Media.Compositor;
using S.Media.Core.Registry;
using Xunit;

namespace S.Abi.Tests;

/// <summary>
/// NXT-09 product surface: <see cref="MediaPluginDirectory"/> scans a directory, loads plugin libraries
/// fail-soft, and registers their capabilities. The real-plugin tests compile the canonical
/// <c>test_plugin.c</c> with gcc at run time and skip (with the reason) where gcc is unavailable — the
/// same gating pattern as the FFmpeg-native and remux tests.
/// </summary>
public sealed class MediaPluginDirectoryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mfp-plugins-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MissingDirectory_YieldsAnEmptyHost()
    {
        using var plugins = MediaPluginDirectory.Load(Path.Combine(_dir, "does-not-exist"));
        Assert.Empty(plugins.Plugins);
        Assert.Empty(plugins.Failures);
        Assert.Empty(plugins.Skipped);
    }

    [Fact]
    public void EmptyDirectory_YieldsAnEmptyHost()
    {
        using var plugins = MediaPluginDirectory.Load(_dir);
        Assert.Empty(plugins.Plugins);
        Assert.Empty(plugins.Failures);
    }

    [Fact]
    public void JunkLibrary_IsAFailure_NotACrash()
    {
        File.WriteAllBytes(Path.Combine(_dir, "garbage" + MediaPluginDirectory.NativeLibraryExtension),
            [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02]);
        using var plugins = MediaPluginDirectory.Load(_dir);
        Assert.Empty(plugins.Plugins);
        var failure = Assert.Single(plugins.Failures);
        Assert.Contains("garbage", failure.Path, StringComparison.Ordinal);
    }

    // --- real plugin (gcc-compiled test_plugin.c), skipped where gcc is absent ------------------------

    /// <summary>Runs only where gcc AND the repo checkout are available (the LibAssFact/RemuxFact
    /// pattern) — the plugin is compiled from the canonical <c>test_plugin.c</c> at test time.</summary>
    private sealed class GccFactAttribute : FactAttribute
    {
        public GccFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "MSVC plugin compile is CI-only work (review NXT-15 note)";
            else if (FindRepoRoot() is null)
                Skip = "repo root (test_plugin.c) not reachable from the test base directory";
            else if (!GccAvailable())
                Skip = "gcc not on PATH — real-plugin load covered by AbiSmoke locally";
        }

        private static bool GccAvailable()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("gcc", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p!.WaitForExit(10_000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Compiles the canonical AbiSmoke test plugin into <paramref name="dir"/>.</summary>
    private static string CompileTestPlugin(string dir)
    {
        var root = FindRepoRoot()!;
        var cFile = Path.Combine(root, "MediaFramework", "Tools", "AbiSmoke", "test_plugin.c");
        var include = Path.Combine(root, "MediaFramework", "Interop", "S.Abi", "include");
        var so = Path.Combine(dir, "mfp_test_plugin" + MediaPluginDirectory.NativeLibraryExtension);
        using var p = Process.Start(new ProcessStartInfo("gcc", $"-shared -fPIC -I\"{include}\" \"{cFile}\" -o \"{so}\"")
        {
            RedirectStandardError = true,
        });
        p!.WaitForExit(30_000);
        Assert.True(p.ExitCode == 0 && File.Exists(so), $"gcc failed: {p.StandardError.ReadToEnd()}");
        return so;
    }

    private static string? FindRepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "MFPlayer.sln")))
                return dir;
        return null;
    }

    [GccFact]
    public void RealPlugin_LoadsRegistersAndUnloads()
    {
        CompileTestPlugin(_dir);

        using var plugins = MediaPluginDirectory.Load(_dir);
        Assert.Empty(plugins.Failures);
        var plugin = Assert.Single(plugins.Plugins);
        Assert.False(string.IsNullOrWhiteSpace(plugin.Id));

        // Capabilities register into real registries: the media registry gains the plugin's audio
        // backend, the compositor registry gains its layer-surface kind.
        var surfaces = new CompositorRegistryBuilder();
        var registry = MediaRegistry.Build(b => plugins.RegisterInto(media: b, compositor: surfaces));
        Assert.NotEmpty(registry.AudioBackends);
        Assert.NotEmpty(surfaces.Build().LayerSurfaceKinds);
    }

    [GccFact]
    public void NonPluginLibrary_IsSkippedSilently()
    {
        CompileTestPlugin(_dir);

        // A dependency-style library (no register export): compile a trivial shared object.
        var cPath = Path.Combine(_dir, "dep.c");
        File.WriteAllText(cPath, "int mfp_unrelated(void){return 42;}\n");
        var depPath = Path.Combine(_dir, "libdep" + MediaPluginDirectory.NativeLibraryExtension);
        using (var p = Process.Start(new ProcessStartInfo("gcc", $"-shared -fPIC \"{cPath}\" -o \"{depPath}\"")))
        {
            p!.WaitForExit(30_000);
            Assert.Equal(0, p.ExitCode);
        }

        using var plugins = MediaPluginDirectory.Load(_dir);
        Assert.Single(plugins.Plugins);                       // the real plugin
        Assert.Contains(plugins.Skipped, s => s.Contains("libdep", StringComparison.Ordinal));
        Assert.Empty(plugins.Failures);
    }
}
