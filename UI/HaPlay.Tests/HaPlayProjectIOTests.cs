using HaPlay.ControlGraph;
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
    public async Task SaveAsync_WhenCancelled_PreservesExistingProjectFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "haplay-atomic-save-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "show.haplayproj");
        const string original = """{"sentinel":true}""";
        await File.WriteAllTextAsync(path, original);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProjectIO.SaveAsync(new HaPlayProject(), path, cts.Token));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
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
        var compId = Guid.NewGuid();
        var outputLineId = Guid.NewGuid();
        var cues = new CueList
        {
            Name = "Show A",
            Compositions = [new CueComposition { Id = compId, Name = "Program", Width = 1920, Height = 1080 }],
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
                            AudioRoutes =
                            {
                                new CueAudioRoute
                                {
                                    SourceChannel = 0,
                                    OutputLineId = outputLineId,
                                    OutputChannel = 1,
                                    GainDb = -3,
                                    Muted = false,
                                },
                            },
                            VideoPlacements =
                            {
                                new CueVideoPlacement
                                {
                                    CompositionId = compId,
                                    LayerIndex = 1,
                                    Position = CueLayerPosition.Cover,
                                    Opacity = 1.0,
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
        Assert.Equal(compId, loadedCueList.Compositions[0].Id);
        var group = Assert.IsType<CueGroupNode>(loadedCueList.Nodes[0]);
        var media = Assert.IsType<MediaCueNode>(group.Children[0]);
        Assert.IsType<FilePlaylistItem>(media.Source);
        Assert.Single(media.AudioRoutes);
        Assert.Equal(outputLineId, media.AudioRoutes[0].OutputLineId);
        Assert.Single(media.VideoPlacements);
        Assert.Equal(compId, media.VideoPlacements[0].CompositionId);
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
            OutputPreset = PlayerOutputPreset.Custom,
            CustomOutputWidth = 1366,
            CustomOutputHeight = 768,
            TransitionMode = PlayerTransitionMode.Fade,
            TransitionDurationMs = 750,
            HeadphonesCueEnabled = true,
            HeadphonesCueOutputId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            HeadphonesCueTapPoint = HeadphonesCueTapPoint.PostFader,
            HeadphonesCueGainDb = -9.5,
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
        Assert.Equal(PlayerOutputPreset.Custom, loaded.OutputPreset);
        Assert.Equal(1366, loaded.CustomOutputWidth);
        Assert.Equal(768, loaded.CustomOutputHeight);
        Assert.Equal(PlayerTransitionMode.Fade, loaded.TransitionMode);
        Assert.Equal(750, loaded.TransitionDurationMs);
        Assert.True(loaded.HeadphonesCueEnabled);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), loaded.HeadphonesCueOutputId);
        Assert.Equal(HeadphonesCueTapPoint.PostFader, loaded.HeadphonesCueTapPoint);
        Assert.Equal(-9.5, loaded.HeadphonesCueGainDb);
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
            AudioMatrixInputChannels = 6,
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
        Assert.Equal(6, loaded.Players[0].AudioMatrixInputChannels);
        Assert.Equal(2, loaded.Players[0].InputTrims.Count);
        Assert.True(loaded.Players[0].InputTrims[1].Muted);
    }

    [Fact]
    public void AudioMatrixViewModel_Resize_StereoSource_StereoOutput_IsIdentity()
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
    public void AudioMatrixViewModel_Resize_StereoSource_FourOutput_DefaultsToFirstPair()
    {
        var m = new HaPlay.ViewModels.AudioMatrixViewModel();
        m.Resize(2, 4);

        Assert.False(m.Cell(0, 0)!.Muted);
        Assert.False(m.Cell(1, 1)!.Muted);
        Assert.True(m.Cell(0, 2)!.Muted);
        Assert.True(m.Cell(1, 3)!.Muted);
    }

    [Fact]
    public void AudioMatrixViewModel_ApplyPreset_MonoLeft_DrivesAllOutputChannels()
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
    public void RoundTrip_SharedHeadphonesBuses_PreservesIdsAndTargets()
    {
        var busAId = Guid.NewGuid();
        var busBId = Guid.NewGuid();
        var paOutId = Guid.NewGuid();
        var project = new HaPlayProject
        {
            SharedHeadphonesBuses =
            {
                new SharedHeadphonesBus
                {
                    Id = busAId,
                    Label = "Booth A",
                    PortAudioOutputId = paOutId,
                },
                new SharedHeadphonesBus
                {
                    Id = busBId,
                    Label = "Booth B",
                    PortAudioOutputId = null,
                },
            },
            Players =
            {
                new MediaPlayerConfig
                {
                    Name = "Deck 1",
                    HeadphonesCueEnabled = true,
                    HeadphonesCueSharedBusId = busAId,
                    HeadphonesCueOutputId = paOutId,
                    HeadphonesCueTapPoint = HeadphonesCueTapPoint.PostFader,
                    HeadphonesCueGainDb = -3.0,
                },
            },
        };

        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        Assert.Equal(2, roundTripped.SharedHeadphonesBuses.Count);
        Assert.Equal("Booth A", roundTripped.SharedHeadphonesBuses[0].Label);
        Assert.Equal(paOutId, roundTripped.SharedHeadphonesBuses[0].PortAudioOutputId);
        Assert.Equal(busBId, roundTripped.SharedHeadphonesBuses[1].Id);
        Assert.Null(roundTripped.SharedHeadphonesBuses[1].PortAudioOutputId);

        var loadedPlayer = Assert.Single(roundTripped.Players);
        Assert.Equal(busAId, loadedPlayer.HeadphonesCueSharedBusId);
        Assert.Equal(paOutId, loadedPlayer.HeadphonesCueOutputId);
        Assert.True(loadedPlayer.HeadphonesCueEnabled);
        Assert.Equal(HeadphonesCueTapPoint.PostFader, loadedPlayer.HeadphonesCueTapPoint);
        Assert.Equal(-3.0, loadedPlayer.HeadphonesCueGainDb);
    }

    [Fact]
    public void RoundTrip_ControlGraphs_PreservesNodesConnectionsAndSettings()
    {
        var midiNodeId = Guid.NewGuid();
        var mapNodeId = Guid.NewGuid();
        var x32NodeId = Guid.NewGuid();
        var oscNodeId = Guid.NewGuid();
        var midiOutNodeId = Guid.NewGuid();
        var scriptNodeId = Guid.NewGuid();

        var project = new HaPlayProject
        {
            ControlGraphs =
            [
                new ControlGraphConfig
                {
                    Name = "BCF2000 Layer",
                    IsEnabled = true,
                    Nodes =
                    [
                        new ControlNodeConfig
                        {
                            Id = midiNodeId,
                            DisplayName = "Fader 1",
                            Kind = ControlNodeKind.MidiInput,
                            Settings = new MidiInputControlNodeSettings
                            {
                                Channel = 1,
                                Controller = 0,
                                HighResolution14Bit = true,
                                SoftTakeoverEnabled = true,
                                SoftTakeoverTolerance = 0.03,
                            },
                        },
                        new ControlNodeConfig
                        {
                            Id = mapNodeId,
                            DisplayName = "Normalize",
                            Kind = ControlNodeKind.MapRange,
                            Settings = new MapRangeControlNodeSettings
                            {
                                InputMax = 16383,
                                OutputMax = 1,
                            },
                        },
                        new ControlNodeConfig
                        {
                            Id = x32NodeId,
                            DisplayName = "X32 Ch 1",
                            Kind = ControlNodeKind.X32ChannelFader,
                            Settings = new X32ChannelFaderControlNodeSettings
                            {
                                Host = "192.168.1.50",
                                Channel = 1,
                                MinSendIntervalMs = 25,
                            },
                        },
                        new ControlNodeConfig
                        {
                            Id = oscNodeId,
                            DisplayName = "X32 Feedback",
                            Kind = ControlNodeKind.OscInput,
                            Settings = new OscInputControlNodeSettings
                            {
                                LocalPort = 10023,
                                AddressPattern = "/ch/01/mix/fader",
                            },
                        },
                        new ControlNodeConfig
                        {
                            Id = midiOutNodeId,
                            DisplayName = "BCF Motor Fader",
                            Kind = ControlNodeKind.MidiOutput,
                            Settings = new MidiOutputControlNodeSettings
                            {
                                Channel = 1,
                                Controller = 0,
                                HighResolution14Bit = true,
                                FeedbackMode = ControlFeedbackMode.MotorFeedbackOnly,
                                MinSendIntervalMs = 15,
                            },
                        },
                        new ControlNodeConfig
                        {
                            Id = scriptNodeId,
                            DisplayName = "Script",
                            Kind = ControlNodeKind.ScriptTransform,
                            Settings = new ScriptTransformControlNodeSettings
                            {
                                Source = "return emit.scalar(event.value);",
                                InstructionLimit = 5000,
                            },
                        },
                    ],
                    Connections =
                    [
                        new ControlConnectionConfig { FromNodeId = midiNodeId, ToNodeId = mapNodeId },
                        new ControlConnectionConfig { FromNodeId = mapNodeId, ToNodeId = x32NodeId },
                        new ControlConnectionConfig { FromNodeId = oscNodeId, ToNodeId = midiOutNodeId },
                    ],
                },
            ],
        };

        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var graph = Assert.Single(roundTripped.ControlGraphs);
        Assert.Equal("BCF2000 Layer", graph.Name);
        Assert.True(graph.IsEnabled);
        Assert.Equal(6, graph.Nodes.Count);
        Assert.Equal(3, graph.Connections.Count);
        var midi = Assert.IsType<MidiInputControlNodeSettings>(graph.Nodes[0].Settings);
        Assert.True(midi.HighResolution14Bit);
        Assert.True(midi.SoftTakeoverEnabled);
        Assert.Equal(0.03, midi.SoftTakeoverTolerance);
        var x32 = Assert.IsType<X32ChannelFaderControlNodeSettings>(graph.Nodes[2].Settings);
        Assert.Equal("192.168.1.50", x32.Host);
        Assert.Equal(25, x32.MinSendIntervalMs);
        var osc = Assert.IsType<OscInputControlNodeSettings>(graph.Nodes[3].Settings);
        Assert.Equal(10023, osc.LocalPort);
        Assert.Equal("/ch/01/mix/fader", osc.AddressPattern);
        var midiOut = Assert.IsType<MidiOutputControlNodeSettings>(graph.Nodes[4].Settings);
        Assert.Equal(ControlFeedbackMode.MotorFeedbackOnly, midiOut.FeedbackMode);
        Assert.Equal(15, midiOut.MinSendIntervalMs);
        var script = Assert.IsType<ScriptTransformControlNodeSettings>(graph.Nodes[5].Settings);
        Assert.Equal("return emit.scalar(event.value);", script.Source);
        Assert.Equal(5000, script.InstructionLimit);
    }

    [Fact]
    public void RoundTrip_ControlSystem_PreservesScriptCentricSettings()
    {
        var xtouchId = Guid.NewGuid();
        var backupMidiId = Guid.NewGuid();
        var x32Id = Guid.NewGuid();
        var lightingOscId = Guid.NewGuid();
        var mainListenerId = Guid.NewGuid();
        var auxListenerId = Guid.NewGuid();
        var layerId = Guid.NewGuid();
        var projectScriptId = Guid.NewGuid();
        var deviceScriptId = Guid.NewGuid();
        var layerScriptId = Guid.NewGuid();
        var periodicId = Guid.NewGuid();

        var project = new HaPlayProject
        {
            ControlSystem = new ControlSystemConfig
            {
                IsArmed = true,
                OscListeners =
                [
                    new ControlOscListenerConfig
                    {
                        Id = mainListenerId,
                        Name = "Main OSC Listener",
                        LocalPort = 10020,
                        SocketMode = ControlOscSocketMode.SharedAppListener,
                    },
                    new ControlOscListenerConfig
                    {
                        Id = auxListenerId,
                        Name = "Aux OSC Listener",
                        LocalPort = 10021,
                        SocketMode = ControlOscSocketMode.SharedAppListener,
                    },
                ],
                OscCacheUpdateMode = ControlOscCacheUpdateMode.OptimisticSendAndIncoming,
                OscCacheOverrides =
                [
                    new ControlOscCacheCommandOverride
                    {
                        AddressPattern = "/ch/*/mix/fader",
                        DeviceInstanceId = x32Id,
                        Mode = ControlOscCacheUpdateMode.IncomingOnly,
                    },
                ],
                Monitor = new ControlMonitorOptions
                {
                    MaxVisibleMessages = 1000,
                    CaptureFormat = ControlMonitorCaptureFormat.JsonLines,
                    IncludeRawBytes = false,
                },
                Devices =
                [
                    new ControlDeviceInstanceConfig
                    {
                        Id = xtouchId,
                        Name = "X-Touch Mini",
                        ProfileId = "behringer.xtouch-mini.mc",
                        Protocol = ControlDeviceProtocol.Midi,
                        ProfileMode = ControlDeviceProfileMode.Suggestion,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "xtouch",
                            MidiInputDeviceId = 3,
                            MidiInputDeviceName = "X-Touch MINI",
                            MidiOutputDeviceId = 4,
                            MidiOutputDeviceName = "X-Touch MINI",
                        },
                        ScriptIds = [deviceScriptId],
                    },
                    new ControlDeviceInstanceConfig
                    {
                        Id = backupMidiId,
                        Name = "Backup MIDI Surface",
                        ProfileId = "generic.midi.surface",
                        Protocol = ControlDeviceProtocol.Midi,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "backup-midi",
                            MidiInputDeviceName = "Backup MIDI In",
                            MidiOutputDeviceName = "Backup MIDI Out",
                        },
                    },
                    new ControlDeviceInstanceConfig
                    {
                        Id = x32Id,
                        Name = "X32 Emulator",
                        ProfileId = "behringer.x32.osc",
                        Protocol = ControlDeviceProtocol.Osc,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "x32",
                            OscHost = "192.168.2.76",
                            OscPort = 10023,
                            OscListenerId = mainListenerId,
                        },
                        PeriodicOscSends =
                        [
                            new ControlPeriodicOscSendConfig
                            {
                                Id = periodicId,
                                Name = "X32 xremote",
                                Address = "/xremote",
                                IntervalMs = 8000,
                            },
                        ],
                    },
                    new ControlDeviceInstanceConfig
                    {
                        Id = lightingOscId,
                        Name = "Lighting OSC",
                        ProfileId = "generic.osc",
                        Protocol = ControlDeviceProtocol.Osc,
                        Binding = new ControlDeviceBindingConfig
                        {
                            Alias = "lighting",
                            OscHost = "192.168.2.90",
                            OscPort = 9000,
                            OscListenerId = auxListenerId,
                        },
                    },
                ],
                DeviceProfileOverrides =
                [
                    new ControlDeviceProfile
                    {
                        Id = "project.custom.osc",
                        DisplayName = "Project Custom OSC",
                        Protocol = ControlDeviceProtocol.Osc,
                        Ports =
                        [
                            new ControlDevicePortProfile
                            {
                                Id = "osc-remote",
                                DisplayName = "OSC Remote",
                                Kind = ControlDevicePortKind.OscRemote,
                            },
                        ],
                        Commands =
                        [
                            new ControlCommandProfile
                            {
                                Id = "project.custom.fader",
                                DisplayName = "Custom Fader",
                                Address = "/custom/fader",
                                ValueKind = ControlCommandValueKind.NormalizedFloat,
                                MinValue = 0,
                                MaxValue = 1,
                            },
                        ],
                    },
                ],
                Layers =
                [
                    new ControlLayerConfig
                    {
                        Id = layerId,
                        Name = "Layer A",
                        IsEnabled = true,
                        Priority = 10,
                        ScriptIds = [layerScriptId],
                    },
                ],
                Scripts =
                [
                    new ControlScriptConfig
                    {
                        Id = projectScriptId,
                        Name = "Shared X32 helpers",
                        ScriptPath = "Scripts/x32Common.mnd",
                        Scope = ControlScriptScope.Project,
                        Imports = ["Scripts/mathHelpers.mnd"],
                    },
                    new ControlScriptConfig
                    {
                        Id = deviceScriptId,
                        Name = "X-Touch to X32",
                        ScriptPath = "Scripts/xtouch-x32.mnd",
                        Scope = ControlScriptScope.Device,
                        DeviceInstanceId = xtouchId,
                        FailurePolicy = new ControlScriptFailurePolicy
                        {
                            Mode = ControlScriptFailureMode.DisableScript,
                            MaxConsecutiveFailures = 3,
                        },
                        Imports = ["Scripts/x32Common.mnd"],
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.MidiControlChange,
                                FunctionName = "onEncoder1",
                                DeviceInstanceId = xtouchId,
                                MidiChannel = 1,
                                MidiController = 16,
                            },
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.OscCacheChanged,
                                FunctionName = "onX32FaderCached",
                                DeviceInstanceId = x32Id,
                                OscAddressPattern = "/ch/01/mix/fader",
                            },
                        ],
                    },
                    new ControlScriptConfig
                    {
                        Id = layerScriptId,
                        Name = "Layer A startup",
                        ScriptPath = "Scripts/layer-a.mnd",
                        Scope = ControlScriptScope.Layer,
                        LayerId = layerId,
                        Triggers =
                        [
                            new ControlScriptTriggerConfig
                            {
                                Kind = ControlScriptTriggerKind.LayerEnabled,
                                FunctionName = "onLayerEnabled",
                                LayerId = layerId,
                            },
                        ],
                    },
                ],
            },
        };

        var roundTripped = ProjectIO.Deserialize(ProjectIO.Serialize(project));

        var control = roundTripped.ControlSystem;
        Assert.True(control.IsArmed);
        Assert.Equal(ControlOscCacheUpdateMode.OptimisticSendAndIncoming, control.OscCacheUpdateMode);
        var cacheOverride = Assert.Single(control.OscCacheOverrides);
        Assert.Equal("/ch/*/mix/fader", cacheOverride.AddressPattern);
        Assert.Equal(x32Id, cacheOverride.DeviceInstanceId);
        Assert.Equal(ControlOscCacheUpdateMode.IncomingOnly, cacheOverride.Mode);
        Assert.Equal(1000, control.Monitor.MaxVisibleMessages);
        Assert.Equal(ControlMonitorCaptureFormat.JsonLines, control.Monitor.CaptureFormat);
        Assert.False(control.Monitor.IncludeRawBytes);

        Assert.Equal(2, control.OscListeners.Count);
        Assert.Equal(mainListenerId, control.OscListeners[0].Id);
        Assert.Equal("Main OSC Listener", control.OscListeners[0].Name);
        Assert.Equal(10020, control.OscListeners[0].LocalPort);
        Assert.Equal(ControlOscSocketMode.SharedAppListener, control.OscListeners[0].SocketMode);
        Assert.Equal(auxListenerId, control.OscListeners[1].Id);
        Assert.Equal(10021, control.OscListeners[1].LocalPort);

        Assert.Equal(4, control.Devices.Count);
        var xtouch = control.Devices[0];
        Assert.Equal(xtouchId, xtouch.Id);
        Assert.Equal("behringer.xtouch-mini.mc", xtouch.ProfileId);
        Assert.Equal(ControlDeviceProtocol.Midi, xtouch.Protocol);
        Assert.Equal(ControlDeviceProfileMode.Suggestion, xtouch.ProfileMode);
        Assert.Equal("xtouch", xtouch.Binding.Alias);
        Assert.Equal(3, xtouch.Binding.MidiInputDeviceId);
        Assert.Equal("X-Touch MINI", xtouch.Binding.MidiOutputDeviceName);
        Assert.Equal(deviceScriptId, Assert.Single(xtouch.ScriptIds));

        var backupMidi = control.Devices[1];
        Assert.Equal(backupMidiId, backupMidi.Id);
        Assert.Equal(ControlDeviceProtocol.Midi, backupMidi.Protocol);
        Assert.Equal("backup-midi", backupMidi.Binding.Alias);
        Assert.Equal("Backup MIDI In", backupMidi.Binding.MidiInputDeviceName);
        Assert.Equal("Backup MIDI Out", backupMidi.Binding.MidiOutputDeviceName);

        var x32 = control.Devices[2];
        Assert.Equal("192.168.2.76", x32.Binding.OscHost);
        Assert.Equal(10023, x32.Binding.OscPort);
        Assert.Equal(mainListenerId, x32.Binding.OscListenerId);
        var xremote = Assert.Single(x32.PeriodicOscSends);
        Assert.Equal(periodicId, xremote.Id);
        Assert.Equal("/xremote", xremote.Address);
        Assert.Equal(8000, xremote.IntervalMs);

        var lighting = control.Devices[3];
        Assert.Equal(lightingOscId, lighting.Id);
        Assert.Equal(ControlDeviceProtocol.Osc, lighting.Protocol);
        Assert.Equal("192.168.2.90", lighting.Binding.OscHost);
        Assert.Equal(9000, lighting.Binding.OscPort);
        Assert.Equal(auxListenerId, lighting.Binding.OscListenerId);

        var profileOverride = Assert.Single(control.DeviceProfileOverrides);
        Assert.Equal("project.custom.osc", profileOverride.Id);
        Assert.Equal("Project Custom OSC", profileOverride.DisplayName);
        Assert.Equal(ControlDeviceProtocol.Osc, profileOverride.Protocol);
        Assert.Equal(ControlDevicePortKind.OscRemote, Assert.Single(profileOverride.Ports).Kind);
        Assert.Equal("/custom/fader", Assert.Single(profileOverride.Commands).Address);

        var layer = Assert.Single(control.Layers);
        Assert.Equal(layerId, layer.Id);
        Assert.Equal("Layer A", layer.Name);
        Assert.True(layer.IsEnabled);
        Assert.Equal(10, layer.Priority);
        Assert.Equal(layerScriptId, Assert.Single(layer.ScriptIds));

        Assert.Equal(3, control.Scripts.Count);
        Assert.Equal("Scripts/x32Common.mnd", control.Scripts[0].ScriptPath);
        Assert.Equal("Scripts/mathHelpers.mnd", Assert.Single(control.Scripts[0].Imports));
        var deviceScript = control.Scripts[1];
        Assert.Equal(ControlScriptScope.Device, deviceScript.Scope);
        Assert.Equal(xtouchId, deviceScript.DeviceInstanceId);
        Assert.Equal(ControlScriptFailureMode.DisableScript, deviceScript.FailurePolicy.Mode);
        Assert.Equal(3, deviceScript.FailurePolicy.MaxConsecutiveFailures);
        Assert.Equal(2, deviceScript.Triggers.Count);
        Assert.Equal(ControlScriptTriggerKind.MidiControlChange, deviceScript.Triggers[0].Kind);
        Assert.Equal("onEncoder1", deviceScript.Triggers[0].FunctionName);
        Assert.Equal(16, deviceScript.Triggers[0].MidiController);
        Assert.Equal(ControlScriptTriggerKind.OscCacheChanged, deviceScript.Triggers[1].Kind);
        Assert.Equal("/ch/01/mix/fader", deviceScript.Triggers[1].OscAddressPattern);

        var layerScript = control.Scripts[2];
        Assert.Equal(ControlScriptScope.Layer, layerScript.Scope);
        Assert.Equal(layerId, layerScript.LayerId);
        Assert.Equal(ControlScriptTriggerKind.LayerEnabled, Assert.Single(layerScript.Triggers).Kind);
    }

    [Fact]
    public void ControlSystem_Defaults_MatchRewriteDecisions()
    {
        var control = new HaPlayProject().ControlSystem;

        Assert.False(control.IsArmed);
        // No default app-level OSC listener: device replies use the client socket, so a standing inbound
        // UDP port is opt-in (added only for separate external OSC control sources).
        Assert.Empty(control.OscListeners);
        Assert.Equal(ControlOscCacheUpdateMode.IncomingOnly, control.OscCacheUpdateMode);
        Assert.Empty(control.OscCacheOverrides);
        Assert.Equal(1000, control.Monitor.MaxVisibleMessages);
        Assert.Equal(ControlMonitorCaptureFormat.JsonLines, control.Monitor.CaptureFormat);
        Assert.True(control.Monitor.IncludeRawBytes);

        var script = new ControlScriptConfig();
        Assert.Equal(ControlScriptScope.Project, script.Scope);
        Assert.Equal(ControlScriptFailureMode.DisableScript, script.FailurePolicy.Mode);
        Assert.Equal(3, script.FailurePolicy.MaxConsecutiveFailures);

        var periodic = new ControlPeriodicOscSendConfig();
        Assert.Equal("/xremote", periodic.Name);
        Assert.Equal("/xremote", periodic.Address);
        Assert.Equal(8000, periodic.IntervalMs);
    }

    [Fact]
    public void CurrentSchemaVersion_IsThree()
    {
        // §9.4: schemaVersion = 3 adds the script-centric MIDI/OSC control system.
        // This test exists so a future schema bump is intentional — bumping requires also adding the
        // migration path; this guard makes sure that decision is conscious.
        Assert.Equal(3, HaPlayProject.CurrentSchemaVersion);
    }
}
