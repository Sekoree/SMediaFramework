using Android.Content;

namespace HaViz.Android.Services;

/// <summary>
/// Copies the APK-bundled projectM preset/texture packs into the app's files dir on first run
/// (projectM enumerates plain file paths, not Android assets). A marker file keyed on the app
/// version skips the copy on subsequent launches; users can drop extra .milk files into the
/// presets dir over adb/MTP and they are picked up on the next engine start.
/// </summary>
internal static class PresetDeployer
{
    public static (string PresetsDir, string TexturesDir) EnsureDeployed(Context context)
    {
        var root = context.GetExternalFilesDir(null)?.AbsolutePath
                   ?? context.FilesDir!.AbsolutePath;
        var presets = Path.Combine(root, "presets");
        var textures = Path.Combine(root, "textures");
        var marker = Path.Combine(root, ".presets-deployed-v1");
        if (File.Exists(marker))
            return (presets, textures);

        Directory.CreateDirectory(presets);
        Directory.CreateDirectory(textures);
        var assets = context.Assets!;
        CopyAssetTree(assets, "presets", presets);
        CopyAssetTree(assets, "textures", textures);
        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        return (presets, textures);
    }

    private static void CopyAssetTree(global::Android.Content.Res.AssetManager assets, string assetDir, string destDir)
    {
        var entries = assets.List(assetDir) ?? [];
        foreach (var entry in entries)
        {
            var assetPath = $"{assetDir}/{entry}";
            var children = assets.List(assetPath) ?? [];
            if (children.Length > 0)
            {
                var subDir = Path.Combine(destDir, entry);
                Directory.CreateDirectory(subDir);
                CopyAssetTree(assets, assetPath, subDir);
                continue;
            }

            using var source = assets.Open(assetPath);
            using var target = File.Create(Path.Combine(destDir, entry));
            source.CopyTo(target);
        }
    }
}
