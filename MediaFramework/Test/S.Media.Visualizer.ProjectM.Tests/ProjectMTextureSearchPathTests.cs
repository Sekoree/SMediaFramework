using Xunit;

namespace S.Media.Visualizer.ProjectM.Tests;

public sealed class ProjectMTextureSearchPathTests
{
    [Fact]
    public void Resolve_FindsInstallSibling_FromPresetRootOrPackSubdirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pm_textures_{Guid.NewGuid():N}");
        var presetRoot = Path.Combine(root, "presets");
        var presetPack = Path.Combine(presetRoot, "Milkdrop-Original");
        var textures = Path.Combine(root, "textures");
        Directory.CreateDirectory(presetPack);
        Directory.CreateDirectory(textures);

        try
        {
            Assert.Contains(textures, ProjectMTextureSearchPaths.Resolve(presetRoot));
            Assert.Contains(textures, ProjectMTextureSearchPaths.Resolve(presetPack));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ReturnsOnlyExistingDistinctDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pm_textures_{Guid.NewGuid():N}");
        var textures = Path.Combine(root, "textures");
        Directory.CreateDirectory(textures);

        try
        {
            Assert.Equal([textures], ProjectMTextureSearchPaths.Resolve(root));
            Assert.Equal([textures], ProjectMTextureSearchPaths.Resolve(Path.Combine(root, "missing")));
            Assert.Empty(ProjectMTextureSearchPaths.Resolve(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
