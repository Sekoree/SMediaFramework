using HaPlay.Models;
using S.Media.Core.Video;
using Xunit;

namespace HaPlay.Tests;

public sealed class HaPlayProjectIOTests
{
    [Fact]
    public void RoundTrip_EmptyProject_PreservesSchemaVersion()
    {
        var project = new HaPlayProject();
        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));
        Assert.Equal(HaPlayProject.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.Empty(roundTripped.Outputs);
        Assert.Empty(roundTripped.Players);
        Assert.Empty(roundTripped.CueLists);
    }

    [Fact]
    public void RoundTrip_PortAudioOutput_PreservesAllFields()
    {
        var id = Guid.NewGuid();
        var def = new PortAudioOutputDefinition(
            id, "Main Speakers", HostApiIndex: 2, "ALSA", GlobalDeviceIndex: 7, "USB Codec",
            ChannelCount: 4, SampleRate: 96000);

        var project = new HaPlayProject { Outputs = { def } };
        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var pa = Assert.IsType<PortAudioOutputDefinition>(Assert.Single(roundTripped.Outputs));
        Assert.Equal(id, pa.Id);
        Assert.Equal("Main Speakers", pa.DisplayName);
        Assert.Equal(2, pa.HostApiIndex);
        Assert.Equal("ALSA", pa.HostApiName);
        Assert.Equal(7, pa.GlobalDeviceIndex);
        Assert.Equal("USB Codec", pa.DeviceName);
        Assert.Equal(4, pa.ChannelCount);
        Assert.Equal(96000, pa.SampleRate);
    }

    [Fact]
    public void RoundTrip_LocalVideoOutput_PreservesCloneOfId()
    {
        var parentId = Guid.NewGuid();
        var cloneId = Guid.NewGuid();
        var parent = new LocalVideoOutputDefinition(
            parentId, "Program", VideoOutputEngine.SdlOpenGl, VideoSurfaceMode.FullScreen,
            ScreenIndex: 1, WindowWidth: null, WindowHeight: null);
        var clone = new LocalVideoOutputDefinition(
            cloneId, "Confidence", VideoOutputEngine.AvaloniaOpenGl, VideoSurfaceMode.Windowed,
            ScreenIndex: 0, WindowWidth: 640, WindowHeight: 360, CloneOfId: parentId);

        var project = new HaPlayProject { Outputs = { parent, clone } };
        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var loadedParent = Assert.IsType<LocalVideoOutputDefinition>(roundTripped.Outputs[0]);
        var loadedClone = Assert.IsType<LocalVideoOutputDefinition>(roundTripped.Outputs[1]);
        Assert.Null(loadedParent.CloneOfId);
        Assert.Equal(parentId, loadedClone.CloneOfId);
        Assert.Equal(VideoOutputEngine.AvaloniaOpenGl, loadedClone.Engine);
        Assert.Equal(VideoSurfaceMode.Windowed, loadedClone.SurfaceMode);
        Assert.Equal(640, loadedClone.WindowWidth);
        Assert.Equal(360, loadedClone.WindowHeight);
    }

    [Fact]
    public void RoundTrip_NDIOutput_PreservesPixelFormatAndResolutionLock()
    {
        var def = new NDIOutputDefinition(
            Guid.NewGuid(), "NDI Program", "MachineA (HaPlay)", Groups: "Studio",
            NDIOutputStreamMode.VideoAndAudio, AudioChannelCount: 4, AudioSampleRate: 48000,
            PixelFormatLock: PixelFormat.Uyvy, ResolutionLockWidth: 1280, ResolutionLockHeight: 720);

        var project = new HaPlayProject { Outputs = { def } };
        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));
        var nd = Assert.IsType<NDIOutputDefinition>(Assert.Single(roundTripped.Outputs));

        Assert.Equal("MachineA (HaPlay)", nd.SourceName);
        Assert.Equal("Studio", nd.Groups);
        Assert.Equal(NDIOutputStreamMode.VideoAndAudio, nd.StreamMode);
        Assert.Equal(4, nd.AudioChannelCount);
        Assert.Equal(48000, nd.AudioSampleRate);
        Assert.Equal(PixelFormat.Uyvy, nd.PixelFormatLock);
        Assert.Equal(1280, nd.ResolutionLockWidth);
        Assert.Equal(720, nd.ResolutionLockHeight);
    }

    [Fact]
    public void RoundTrip_MixedOutputs_DiscriminatorIsKindString()
    {
        var paId = Guid.NewGuid();
        var lvId = Guid.NewGuid();
        var ndiId = Guid.NewGuid();
        var project = new HaPlayProject
        {
            Outputs =
            {
                new PortAudioOutputDefinition(paId, "PA", 0, "Alsa", 1, "dev", 2, 48000),
                new LocalVideoOutputDefinition(lvId, "LV", VideoOutputEngine.SdlOpenGl,
                    VideoSurfaceMode.Windowed, 0, 1280, 720),
                new NDIOutputDefinition(ndiId, "NDI", "src", null,
                    NDIOutputStreamMode.AudioOnly, 2, 48000),
            },
        };

        var json = ProjectIO.Serialize(project);
        // Polymorphic discriminator is the "kind" property as configured on OutputDefinition.
        Assert.Contains("\"kind\": \"portAudio\"", json);
        Assert.Contains("\"kind\": \"localVideo\"", json);
        Assert.Contains("\"kind\": \"ndi\"", json);

        var roundTripped = ProjectIO.Deserialize(json);
        Assert.IsType<PortAudioOutputDefinition>(roundTripped.Outputs[0]);
        Assert.IsType<LocalVideoOutputDefinition>(roundTripped.Outputs[1]);
        Assert.IsType<NDIOutputDefinition>(roundTripped.Outputs[2]);
    }

    [Fact]
    public void RoundTrip_PlayerConfig_PreservesPlaylistAndRouting()
    {
        var player = new MediaPlayerConfig
        {
            Name = "VT Player",
            PlaylistTabs =
            {
                new PlaylistConfig
                {
                    Name = "Set A",
                    Paths = { "/show/opener.mp4", "/show/main.mkv" },
                    SelectedPath = "/show/main.mkv",
                    AutoAdvance = true,
                },
                new PlaylistConfig
                {
                    Name = "Encore",
                    Paths = { "/show/stinger.mov" },
                    SelectedPath = "/show/stinger.mov",
                    IsLooping = true,
                },
            },
            SelectedPlaylistTabIndex = 1,
            PlaylistPaths = { "/show/opener.mp4", "/show/main.mkv" },
            SelectedPlaylistPath = "/show/main.mkv",
            MediaFilePath = "/show/main.mkv",
            FallbackImagePath = "/show/slate.png",
            IsLooping = false,
            AutoAdvancePlaylist = true,
            HoldFallbackVideo = true,
            MasterVolumeDb = -3,
            OutputPreset = PlayerOutputPreset.Preset1080p60,
            TransitionMode = PlayerTransitionMode.Fade,
            TransitionDurationMs = 750,
            SelectedOutputDisplayNames = { "Main Speakers", "NDI Program" },
            OutputGains =
            {
                new OutputGainConfig
                {
                    OutputDisplayName = "Main Speakers",
                    GainDb = -6,
                    Muted = false,
                    MixMode = AudioRouteMixMode.Swap,
                },
            },
        };

        var project = new HaPlayProject { Players = { player } };
        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var loaded = Assert.Single(roundTripped.Players);
        Assert.Equal("VT Player", loaded.Name);
        Assert.Equal(2, loaded.PlaylistTabs.Count);
        Assert.Equal("Encore", loaded.PlaylistTabs[1].Name);
        Assert.Equal(1, loaded.SelectedPlaylistTabIndex);
        Assert.Equal(2, loaded.PlaylistPaths.Count);
        Assert.Equal("/show/main.mkv", loaded.SelectedPlaylistPath);
        Assert.True(loaded.AutoAdvancePlaylist);
        Assert.True(loaded.HoldFallbackVideo);
        Assert.Equal(-3, loaded.MasterVolumeDb);
        Assert.Equal(PlayerOutputPreset.Preset1080p60, loaded.OutputPreset);
        Assert.Equal(PlayerTransitionMode.Fade, loaded.TransitionMode);
        Assert.Equal(750, loaded.TransitionDurationMs);
        Assert.Equal(new[] { "Main Speakers", "NDI Program" }, loaded.SelectedOutputDisplayNames);
        var gain = Assert.Single(loaded.OutputGains);
        Assert.Equal(-6, gain.GainDb);
        Assert.Equal(AudioRouteMixMode.Swap, gain.MixMode);
    }

    [Fact]
    public void OutputGainConfig_DefaultMixMode_IsStereo()
    {
        // Phase C (§4.3.4) — older configs that pre-date AudioRouteMixMode must keep their behavior.
        // Default is "Stereo" so a freshly-loaded MediaPlayerConfig.OutputGains entry reroutes via the
        // identity ChannelMap (no surprise reorder on load).
        var fresh = new OutputGainConfig { OutputDisplayName = "Any", GainDb = 0 };
        Assert.Equal(AudioRouteMixMode.Stereo, fresh.MixMode);
        Assert.Empty(fresh.MatrixCells);
    }

    [Fact]
    public void RoundTrip_PlayerConfig_PreservesMatrixCells()
    {
        // Phase C (§4.3.4) — per-cell matrix survives a project save/load cycle.
        var player = new MediaPlayerConfig
        {
            Name = "Matrix Player",
            SelectedOutputDisplayNames = { "Main Speakers" },
            OutputGains =
            {
                new OutputGainConfig
                {
                    OutputDisplayName = "Main Speakers",
                    GainDb = 0,
                    MixMode = AudioRouteMixMode.Stereo,
                    MatrixCells =
                    {
                        new AudioMatrixCellConfig { InputChannel = 0, OutputChannel = 0, GainDb =  0.0, Muted = false },
                        new AudioMatrixCellConfig { InputChannel = 1, OutputChannel = 1, GainDb = -6.0, Muted = false },
                        new AudioMatrixCellConfig { InputChannel = 0, OutputChannel = 1, GainDb = -12.0, Muted = false },
                    },
                },
            },
        };

        var project = new HaPlayProject { Players = { player } };
        var loaded = ProjectIO.Deserialize(ProjectIO.Serialize(project));
        var gain = Assert.Single(Assert.Single(loaded.Players).OutputGains);
        Assert.Equal(3, gain.MatrixCells.Count);
        Assert.Equal(-6.0, gain.MatrixCells[1].GainDb);
        Assert.Equal(-12.0, gain.MatrixCells[2].GainDb);
        Assert.Equal(1, gain.MatrixCells[2].OutputChannel);
    }

    [Fact]
    public void AudioMatrixViewModel_Resize_StereoSource_StereoSink_IsIdentity()
    {
        // Phase C — Resize's identity default sets the diagonal to 0 dB unmuted and the off-diagonal to
        // -60 dB muted, matching the legacy stereo MixMode.
        var m = new HaPlay.ViewModels.AudioMatrixViewModel();
        m.Resize(2, 2);
        var diag00 = m.Cell(0, 0)!;
        var diag11 = m.Cell(1, 1)!;
        var off01 = m.Cell(0, 1)!;
        Assert.False(diag00.Muted);
        Assert.Equal(0.0, diag00.GainDb);
        Assert.False(diag11.Muted);
        Assert.True(off01.Muted);
    }

    [Fact]
    public void AudioMatrixViewModel_ApplyPreset_Swap_MovesLToRAndViceVersa()
    {
        var m = new HaPlay.ViewModels.AudioMatrixViewModel();
        m.Resize(2, 2);
        m.ApplyPreset(AudioRouteMixMode.Swap);
        Assert.False(m.Cell(1, 0)!.Muted); // L of out fed from R of in
        Assert.False(m.Cell(0, 1)!.Muted); // R of out fed from L of in
        Assert.True(m.Cell(0, 0)!.Muted);
        Assert.True(m.Cell(1, 1)!.Muted);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_PreservesProject()
    {
        var project = new HaPlayProject
        {
            HaPlayVersion = "test-1.0",
            Outputs =
            {
                new PortAudioOutputDefinition(Guid.NewGuid(), "PA1", 0, "Alsa", 1, "dev", 2, 44100),
            },
            Players = { new MediaPlayerConfig { Name = "P1" } },
        };

        var tmp = Path.GetTempFileName();
        try
        {
            await ProjectIO.SaveAsync(project, tmp);
            var loaded = await ProjectIO.LoadAsync(tmp);
            Assert.Equal("test-1.0", loaded.HaPlayVersion);
            Assert.Single(loaded.Outputs);
            Assert.Equal("P1", loaded.Players[0].Name);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task LoadAsync_FutureSchemaVersion_ThrowsUnsupportedSchemaVersion()
    {
        // Hand-crafted JSON simulating a project written by a future build.
        var json = """
        {
          "schemaVersion": 99,
          "outputs": [],
          "players": [],
          "cueLists": []
        }
        """;
        var tmp = Path.GetTempFileName();
        await File.WriteAllTextAsync(tmp, json);
        try
        {
            var ex = await Assert.ThrowsAsync<UnsupportedSchemaVersionException>(() => ProjectIO.LoadAsync(tmp));
            Assert.Equal(99, ex.FileVersion);
            Assert.Equal(HaPlayProject.CurrentSchemaVersion, ex.SupportedVersion);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void CurrentSchemaVersion_IsOne()
    {
        // §9.4: schemaVersion = 1 on the first ship of the project file format.
        // This test exists so a future schema bump is intentional — bumping requires also adding the
        // migration path; this guard makes sure that decision is conscious.
        Assert.Equal(1, HaPlayProject.CurrentSchemaVersion);
    }
}
