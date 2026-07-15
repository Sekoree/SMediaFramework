using S.Media.FFmpeg.Common;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

public sealed class FFmpegRuntimeResolverTests
{
    [Fact]
    public void ResolveDefaultRootPath_UsesPlatformLoaderOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Equal(string.Empty, FFmpegRuntime.ResolveDefaultRootPath());
    }

    [Fact]
    public void FindCompleteNativeDirectory_SkipsIncompleteAndAppLocalSets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mfplayer-ffmpeg-resolver-{Guid.NewGuid():N}");
        var incomplete = Path.Combine(root, "incomplete");
        var appLocal = Path.Combine(root, "app");
        var system = Path.Combine(root, "system");
        var files = new[] { "avcodec-62.dll", "avutil-60.dll" };

        try
        {
            Directory.CreateDirectory(incomplete);
            Directory.CreateDirectory(appLocal);
            Directory.CreateDirectory(system);
            File.WriteAllText(Path.Combine(incomplete, files[0]), "");
            foreach (var directory in new[] { appLocal, system })
                foreach (var file in files)
                    File.WriteAllText(Path.Combine(directory, file), "");

            var found = FFmpegRuntime.FindCompleteNativeDirectory(
                [incomplete, appLocal, system], files, appLocal);

            Assert.Equal(Path.GetFullPath(system), found);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
