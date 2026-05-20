using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class PlaylistConfigTests
{
    [Fact]
    public void PlaylistTabViewModel_ToConfig_RoundTripsTabState()
    {
        var tab = new PlaylistTabViewModel("Matinee")
        {
            SelectedPath = "/show/track-2.wav",
            IsLooping = true,
            AutoAdvance = false,
        };
        tab.Paths.Add("/show/track-1.wav");
        tab.Paths.Add("/show/track-2.wav");

        var config = tab.ToConfig();
        var loaded = PlaylistTabViewModel.FromConfig(config);

        Assert.Equal("Matinee", loaded.Name);
        Assert.Equal(new[] { "/show/track-1.wav", "/show/track-2.wav" }, loaded.Paths);
        Assert.Equal("/show/track-2.wav", loaded.SelectedPath);
        Assert.True(loaded.IsLooping);
        Assert.False(loaded.AutoAdvance);
    }

    [Fact]
    public async Task PlaylistIO_RoundTripsStandalonePlaylist()
    {
        var config = new PlaylistConfig
        {
            Name = "Show",
            Paths = { "/a.mp4", "/b.mp4" },
            SelectedPath = "/b.mp4",
            AutoAdvance = true,
        };

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + PlaylistIO.FileExtension);
        try
        {
            await PlaylistIO.SaveAsync(config, tmp);
            var loaded = await PlaylistIO.LoadAsync(tmp);

            Assert.Equal("Show", loaded.Name);
            Assert.Equal("/b.mp4", loaded.SelectedPath);
            Assert.True(loaded.AutoAdvance);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
