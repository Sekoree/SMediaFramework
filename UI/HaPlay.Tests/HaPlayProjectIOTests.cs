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
        Assert.Empty(roundTripped.ActionEndpoints);
        Assert.Empty(roundTripped.CueLists);
    }

    [Fact]
    public void RoundTrip_ActionEndpoints_PreservesKindsAndFields()
    {
        var oscId = Guid.NewGuid();
        var midiId = Guid.NewGuid();
        var project = new HaPlayProject
        {
            ActionEndpoints =
            {
                new OscActionEndpoint
                {
                    Id = oscId,
                    Name = "FOH OSC",
                    Host = "10.0.0.12",
                    Port = 8000,
                },
                new MidiActionEndpoint
                {
                    Id = midiId,
                    Name = "Lighting MIDI",
                    DeviceId = 3,
                    DeviceName = "USB MIDI",
                    Channel = 1,
                },
            },
        };

        var loaded = ProjectIO.Deserialize(ProjectIO.Serialize(project));
        var osc = Assert.IsType<OscActionEndpoint>(loaded.ActionEndpoints[0]);
        var midi = Assert.IsType<MidiActionEndpoint>(loaded.ActionEndpoints[1]);
        Assert.Equal(oscId, osc.Id);
        Assert.Equal("10.0.0.12", osc.Host);
        Assert.Equal(8000, osc.Port);
        Assert.Equal(midiId, midi.Id);
        Assert.Equal(3, midi.DeviceId);
        Assert.Equal("USB MIDI", midi.DeviceName);
        Assert.Equal(1, midi.Channel);
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
    public void RoundTrip_ProjectCueList_PreservesCueNodeTypes()
    {
        var cues = new CueList
        {
            Name = "Show A",
            VirtualOutputs =
            {
                new CueVirtualOutputChannel { Channel = 1, Label = "Main L" },
                new CueVirtualOutputChannel { Channel = 2, Label = "Main R" },
            },
            Nodes =
            {
                new CueGroupNode
                {
                    Number = "1",
                    Label = "Pre-show",
                    FireMode = CueGroupFireMode.FirstCueOnly,
                    Children =
                    {
                        new MediaCueNode
                        {
                            Number = "1.1",
                            Label = "Walk-in",
                            Source = new FilePlaylistItem("/show/walkin.mp3"),
                            VirtualOutputChannels = { 1, 2 },
                            RouteConnections =
                            {
                                new CueRouteConnectionOverride
                                {
                                    InputChannel = 0,
                                    VirtualOutputChannel = 1,
                                    GainDb = -3,
                                    Muted = false,
                                },
                            },
                        },
                        new CommentCueNode
                        {
                            Number = "1.2",
                            Label = "Stage note",
                            Text = "House lights at 50%.",
                        },
                    },
                },
                new ActionCueNode
                {
                    Number = "2",
                    Label = "Lighting GO",
                    ActionKind = CueActionKind.OscOut,
                    AddressOrMessage = "/lighting/go",
                    Arguments = { "12" },
                },
            },
        };

        var project = new HaPlayProject { CueLists = { cues } };
        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var loadedCueList = Assert.Single(roundTripped.CueLists);
        Assert.Equal("Show A", loadedCueList.Name);
        Assert.Equal(2, loadedCueList.VirtualOutputs.Count);
        Assert.Equal(1, loadedCueList.VirtualOutputs[0].Channel);
        Assert.Equal("Main L", loadedCueList.VirtualOutputs[0].Label);
        var group = Assert.IsType<CueGroupNode>(loadedCueList.Nodes[0]);
        var media = Assert.IsType<MediaCueNode>(group.Children[0]);
        Assert.IsType<FilePlaylistItem>(media.Source);
        Assert.Equal(2, media.VirtualOutputChannels.Count);
        Assert.Single(media.RouteConnections);
        Assert.IsType<CommentCueNode>(group.Children[1]);
        Assert.IsType<ActionCueNode>(loadedCueList.Nodes[1]);
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
                    Items =
                    {
                        new FilePlaylistItem("/show/opener.mp4"),
                        new FilePlaylistItem("/show/main.mkv"),
                    },
                    AutoAdvance = true,
                },
                new PlaylistConfig
                {
                    Name = "Encore",
                    Items = { new FilePlaylistItem("/show/stinger.mov") },
                    IsLooping = true,
                },
            },
            SelectedPlaylistTabIndex = 1,
            // Legacy v1 fields — round-trip the value as-is to preserve back-compat readers.
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
        // Set A: two FilePlaylistItem entries round-tripped through the discriminator.
        Assert.Equal(2, loaded.PlaylistTabs[0].Items.Count);
        Assert.IsType<FilePlaylistItem>(loaded.PlaylistTabs[0].Items[0]);
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
            InputTrims =
            {
                new InputChannelTrimConfig { InputChannel = 0, GainDb = -3.0, Muted = false },
                new InputChannelTrimConfig { InputChannel = 1, GainDb =  2.5, Muted = true },
            },
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
        Assert.Equal(2, loaded.Players[0].InputTrims.Count);
        Assert.True(loaded.Players[0].InputTrims[1].Muted);
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
    public void AudioMatrixViewModel_Resize_StereoSource_FourSink_DefaultsToFirstPair()
    {
        var m = new HaPlay.ViewModels.AudioMatrixViewModel();
        m.Resize(2, 4);

        Assert.False(m.Cell(0, 0)!.Muted);
        Assert.False(m.Cell(1, 1)!.Muted);
        Assert.True(m.Cell(0, 2)!.Muted);
        Assert.True(m.Cell(1, 3)!.Muted);
    }

    [Fact]
    public void AudioMatrixViewModel_ApplyPreset_MonoLeft_DrivesAllSinkChannels()
    {
        var m = new HaPlay.ViewModels.AudioMatrixViewModel();
        m.Resize(2, 4);
        m.ApplyPreset(AudioRouteMixMode.MonoLeft);

        for (var oc = 0; oc < 4; oc++)
        {
            Assert.False(m.Cell(0, oc)!.Muted);
            Assert.True(m.Cell(1, oc)!.Muted);
        }
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
    public async Task CueListIO_SaveLoad_RoundTripsCueTree()
    {
        var cueList = new CueList
        {
            Name = "Cue Test",
            Nodes =
            {
                new MediaCueNode
                {
                    Number = "1",
                    Label = "Opener",
                    Source = new FilePlaylistItem("/show/opener.mp4"),
                    TriggerMode = CueTriggerMode.Manual,
                },
            },
        };

        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "." + CueListIO.FileExtension);
        try
        {
            await CueListIO.SaveAsync(cueList, tmp);
            var loaded = await CueListIO.LoadAsync(tmp);
            Assert.Equal("Cue Test", loaded.Name);
            var media = Assert.IsType<MediaCueNode>(Assert.Single(loaded.Nodes));
            Assert.IsType<FilePlaylistItem>(media.Source);
        }
        finally
        {
            if (File.Exists(tmp))
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
