using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class PlaylistConfigTests
{
    [Fact]
    public void PlaylistTabViewModel_ToConfig_RoundTripsTabState()
    {
        var track1 = new FilePlaylistItem("/show/track-1.wav");
        var track2 = new FilePlaylistItem("/show/track-2.wav");
        var tab = new PlaylistTabViewModel("Matinee")
        {
            IsLooping = true,
            AutoAdvance = false,
        };
        tab.Items.Add(track1);
        tab.Items.Add(track2);
        tab.SelectedItem = track2;

        var config = tab.ToConfig();
        var loaded = PlaylistTabViewModel.FromConfig(config);

        Assert.Equal("Matinee", loaded.Name);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("/show/track-1.wav", ((FilePlaylistItem)loaded.Items[0]).Path);
        Assert.Equal("/show/track-2.wav", ((FilePlaylistItem)loaded.Items[1]).Path);
        Assert.NotNull(loaded.SelectedItem);
        Assert.Equal(track2.Id, loaded.SelectedItem!.Id);
        Assert.True(loaded.IsLooping);
        Assert.False(loaded.AutoAdvance);
    }

    [Fact]
    public async Task PlaylistIO_RoundTripsStandalonePlaylist()
    {
        var item = new FilePlaylistItem("/b.mp4");
        var config = new PlaylistConfig
        {
            Name = "Show",
            Items =
            {
                new FilePlaylistItem("/a.mp4"),
                item,
            },
            SelectedItemId = item.Id,
            AutoAdvance = true,
        };

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + PlaylistIO.FileExtension);
        try
        {
            await PlaylistIO.SaveAsync(config, tmp);
            var loaded = await PlaylistIO.LoadAsync(tmp);

            Assert.Equal("Show", loaded.Name);
            Assert.Equal(2, loaded.Items.Count);
            Assert.Equal(item.Id, loaded.SelectedItemId);
            Assert.True(loaded.AutoAdvance);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    /// <summary>v1 fallback — a saved playlist with the legacy <c>Paths</c> list (no <c>Items</c>) still
    /// loads via the FilePlaylistItem projection.</summary>
    [Fact]
    public void PlaylistTabViewModel_FromConfig_PromotesV1Paths()
    {
        var v1 = new PlaylistConfig
        {
            Schema = "HaPlayPlaylist/v1",
            Name = "Legacy",
            Paths = new() { "/old/a.mp3", "/old/b.mp3" },
            SelectedPath = "/old/b.mp3",
        };

        var tab = PlaylistTabViewModel.FromConfig(v1);

        Assert.Equal(2, tab.Items.Count);
        Assert.All(tab.Items, i => Assert.IsType<FilePlaylistItem>(i));
        Assert.Equal("/old/b.mp3", ((FilePlaylistItem)tab.SelectedItem!).Path);
    }

    /// <summary>An NDI input item round-trips through PlaylistIO with its discriminator and
    /// connection hints intact.</summary>
    [Fact]
    public async Task PlaylistIO_RoundTripsNDIInput()
    {
        var ndi = new NDIInputPlaylistItem("CAMERA-1 (Studio A)")
        {
            LowBandwidth = true,
            AudioOnly = false,
            RetrySeconds = 2,
        };
        var config = new PlaylistConfig
        {
            Name = "Live",
            Items = { ndi },
            SelectedItemId = ndi.Id,
        };

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + PlaylistIO.FileExtension);
        try
        {
            await PlaylistIO.SaveAsync(config, tmp);
            var loaded = await PlaylistIO.LoadAsync(tmp);

            Assert.Single(loaded.Items);
            var roundTripped = Assert.IsType<NDIInputPlaylistItem>(loaded.Items[0]);
            Assert.Equal("CAMERA-1 (Studio A)", roundTripped.SourceName);
            Assert.True(roundTripped.LowBandwidth);
            Assert.Equal(2, roundTripped.RetrySeconds);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    /// <summary>A PortAudio input item carries device descriptor + channel count + sample rate
    /// through the JSON round-trip.</summary>
    [Fact]
    public async Task PlaylistIO_RoundTripsPortAudioInput()
    {
        var mic = new PortAudioInputPlaylistItem("Scarlett 2i2 USB")
        {
            HostApiName = "ALSA",
            GlobalDeviceIndex = 3,
            Channels = 2,
            SampleRate = 48000,
        };
        var config = new PlaylistConfig { Name = "Mic", Items = { mic } };

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + PlaylistIO.FileExtension);
        try
        {
            await PlaylistIO.SaveAsync(config, tmp);
            var loaded = await PlaylistIO.LoadAsync(tmp);

            var pa = Assert.IsType<PortAudioInputPlaylistItem>(loaded.Items[0]);
            Assert.Equal("Scarlett 2i2 USB", pa.DeviceName);
            Assert.Equal("ALSA", pa.HostApiName);
            Assert.Equal(3, pa.GlobalDeviceIndex);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
