using Android.Content;
using Android.Content.PM;

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
        // Keyed on versionCode so an APK update redeploys its (possibly changed) bundled packs.
        var marker = Path.Combine(root, $".presets-deployed-{GetVersionCode(context)}");
        if (File.Exists(marker))
            return (presets, textures);
        foreach (var stale in Directory.EnumerateFiles(root, ".presets-deployed-*"))
            File.Delete(stale);

        Directory.CreateDirectory(presets);
        Directory.CreateDirectory(textures);
        var assets = context.Assets!;
        var copied = CopyAssetTree(assets, "presets", presets)
                     + CopyAssetTree(assets, "textures", textures);
        // An APK built before the native/preset build ran carries no packs; without the marker a
        // later install that does carry them still deploys.
        if (copied > 0)
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
        return (presets, textures);
    }

    private static long GetVersionCode(Context context)
    {
        var manager = context.PackageManager!;
        var name = context.PackageName!;
        var info = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? manager.GetPackageInfo(name, PackageManager.PackageInfoFlags.Of(0L))!
            : manager.GetPackageInfo(name, 0)!;
        return info.LongVersionCode;
    }

    private static int CopyAssetTree(global::Android.Content.Res.AssetManager assets, string assetDir, string destDir)
    {
        var copied = 0;
        var entries = assets.List(assetDir) ?? [];
        foreach (var entry in entries)
        {
            var assetPath = $"{assetDir}/{entry}";
            var children = assets.List(assetPath) ?? [];
            if (children.Length > 0)
            {
                var subDir = Path.Combine(destDir, entry);
                Directory.CreateDirectory(subDir);
                copied += CopyAssetTree(assets, assetPath, subDir);
                continue;
            }

            using var source = assets.Open(assetPath);
            using var target = File.Create(Path.Combine(destDir, entry));
            source.CopyTo(target);
            copied++;
        }

        return copied;
    }
}
