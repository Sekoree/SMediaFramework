using Xunit;

namespace S.Media.Session.Tests;

public sealed class ShowDocumentTests
{
    [Fact]
    public void ToJson_FromJson_RoundTripsCuesClipsAndCompositions()
    {
        var doc = new ShowDocument(
            Version: 1,
            Cues:
            [
                new CueDefinition("cue1", 1, "Audio", PreWait: TimeSpan.FromMilliseconds(250)),
                new CueDefinition("cue2", 2, "Video", GroupId: "screens", AutoContinue: true, FollowOnCueId: "cue1"),
            ],
            Clips:
            [
                new ShowClipBinding("cue1", "/music/a.flac"),
                new ShowClipBinding("cue2", "/video/b.mp4", CompositionId: "screen", LayerIndex: 2),
            ],
            Compositions: [new ShowComposition("screen", "Main", 1920, 1080, 30, 1)],
            Routes: []);

        var reloaded = ShowDocument.FromJson(doc.ToJson());

        // Element-wise (Assert.Equal on IEnumerable) — proves a lossless D10 round-trip. Whole-record
        // equality won't do: a record compares its list *properties* by reference (array vs List), not deep.
        Assert.Equal(doc.Version, reloaded.Version);
        Assert.Equal(doc.Cues, reloaded.Cues);
        Assert.Equal(doc.Clips, reloaded.Clips);
        Assert.Equal(doc.Compositions, reloaded.Compositions);
        Assert.Equal(TimeSpan.FromMilliseconds(250), reloaded.Cues[0].PreWait);
        Assert.Equal("screen", reloaded.Clips[1].CompositionId);
        Assert.Equal(2, reloaded.Clips[1].LayerIndex);
        Assert.Equal(1920, reloaded.Compositions[0].Width);
    }

    [Fact]
    public void FromJson_IgnoresRemovedOutputsAndDevicesFields_ForBackwardCompatibility()
    {
        // DOC-02: the dead `Outputs` + `Devices` collections were removed from the schema. A show saved by an
        // older build still carries those JSON properties; loading it must succeed (the unknown properties are
        // skipped), so no existing project file breaks on upgrade.
        const string legacyJson = """
            {
              "Version": 1,
              "Cues": [ { "Id": "c1", "Number": 1, "Label": "One" } ],
              "Clips": [],
              "Compositions": [],
              "Outputs": [ { "SourceId": "c1", "OutputId": "_master" } ],
              "Routes": [ { "SourceId": "c1", "OutputId": "_master" } ],
              "Devices": [ "dev-a", "dev-b" ]
            }
            """;

        var doc = ShowDocument.FromJson(legacyJson);

        Assert.Equal(1, doc.Version);
        Assert.Equal("c1", Assert.Single(doc.Cues).Id);
        Assert.Equal("_master", Assert.Single(doc.Routes).OutputId); // the still-supported field loads
    }

    [Fact]
    public void Empty_IsVersionOneWithNoContent()
    {
        Assert.Equal(1, ShowDocument.Empty.Version);
        Assert.Empty(ShowDocument.Empty.Cues);
        Assert.Empty(ShowDocument.Empty.Clips);
        Assert.Empty(ShowDocument.Empty.Compositions);
    }

    [Fact]
    public void FromJson_EmptyOrInvalid_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ShowDocument.FromJson("null"));
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsClipAudioTrackSelection()
    {
        // Multi-track select (03 §6): a stem file picks track 2; a video clip disables audio (-1).
        var doc = ShowDocument.Empty with
        {
            Clips =
            [
                new ShowClipBinding("cue1", "/stems/song.mka", AudioStreamIndex: 2),
                new ShowClipBinding("cue2", "/video/b.mp4", AudioStreamIndex: -1),
            ],
        };

        var reloaded = ShowDocument.FromJson(doc.ToJson());

        Assert.Equal(2, reloaded.Clips[0].AudioStreamIndex);
        Assert.Equal(-1, reloaded.Clips[1].AudioStreamIndex);
        Assert.Null(ShowDocument.Empty.Clips.FirstOrDefault()?.AudioStreamIndex); // default = automatic
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsNoneOneManySubtitleSelection()
    {
        var doc = ShowDocument.Empty with
        {
            Clips =
            [
                new ShowClipBinding("none", "/video/none.mkv"),
                new ShowClipBinding("legacy", "/video/legacy.mkv", SubtitlePath: "/subs/legacy.srt"),
                new ShowClipBinding("many", "/video/many.mkv", Subtitles:
                [
                    new ShowSubtitleSelection(StreamIndex: 7),
                    new ShowSubtitleSelection("/subs/commentary.ass"),
                ]),
            ],
        };

        var clips = ShowDocument.FromJson(doc.ToJson()).Clips;

        Assert.Empty(clips[0].GetSubtitleSelections());
        Assert.Equal("/subs/legacy.srt", Assert.Single(clips[1].GetSubtitleSelections()).Path);
        Assert.Equal(7, clips[2].GetSubtitleSelections()[0].StreamIndex);
        Assert.Equal("/subs/commentary.ass", clips[2].GetSubtitleSelections()[1].Path);
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsRouteChannelMatrix_AndMaterializes()
    {
        // N→M routing (03 §6) as serializable show data: mono → stereo duplicate.
        var doc = ShowDocument.Empty with
        {
            Routes = [new OutputPatchRoute("clip", "_master", ChannelMatrix: [0, 0])],
        };

        var route = ShowDocument.FromJson(doc.ToJson()).Routes[0];

        Assert.Equal(new[] { 0, 0 }, route.ChannelMatrix);
        var map = route.ToChannelMap();
        Assert.True(map.HasValue);
        Assert.Equal(2, map!.Value.OutputChannels);
        Assert.Equal(0, map.Value[0]);
        Assert.Equal(0, map.Value[1]);
    }

    [Fact]
    public void OutputPatchRoute_ToChannelMap_NullWhenNoMatrix()
    {
        // null/empty matrix → router uses its source-derived default (ChannelMap.DefaultFor).
        Assert.Null(new OutputPatchRoute("s", "o").ToChannelMap());
        Assert.Null(new OutputPatchRoute("s", "o", ChannelMatrix: []).ToChannelMap());
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsClipFullGainMatrix()
    {
        var doc = ShowDocument.Empty with
        {
            Clips =
            [
                new ShowClipBinding("cue", "file.wav")
                {
                    AudioRoutes =
                    [
                        new ShowClipAudioRoute("device", Gain: 0.5f)
                        {
                            MatrixOutputChannels = 1,
                            MatrixCells =
                            [
                                new ShowAudioMatrixCell(0, 0, 1f),
                                new ShowAudioMatrixCell(1, 0, 0.25f),
                            ],
                        },
                    ],
                },
            ],
        };

        var route = Assert.Single(Assert.Single(ShowDocument.FromJson(doc.ToJson()).Clips).AudioRoutes!);
        Assert.Equal(1, route.MatrixOutputChannels);
        Assert.Equal(2, route.MatrixCells!.Count);
        Assert.Equal(new ShowAudioMatrixCell(1, 0, 0.25f), route.MatrixCells[1]);
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsCompositionOutputMapping()
    {
        var doc = ShowDocument.Empty with
        {
            Compositions =
            [
                new ShowComposition("screen", "S", 1920, 1080, OutputMapping: new ClipOutputMappingSpec(
                    [new ClipOutputMappingSection("a", Enabled: true, 0, 0, 0.5, 1, 0, 0, 960, 1080, RotationDegrees: 15)],
                    OutputWidth: 1920, OutputHeight: 1080)),
            ],
        };

        var section = ShowDocument.FromJson(doc.ToJson()).Compositions[0].OutputMapping!.Sections[0];

        Assert.Equal("a", section.Id);
        Assert.Equal(0.5, section.SrcWidth);
        Assert.Equal(15, section.RotationDegrees);
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsAudioOutputs()
    {
        var doc = ShowDocument.Empty with
        {
            AudioOutputs = [new ShowAudioOutput("main"), new ShowAudioOutput("monitor", DeviceId: "hw:1", GroupId: "fx")],
        };

        var reloaded = ShowDocument.FromJson(doc.ToJson());

        Assert.Equal(doc.AudioOutputs, reloaded.AudioOutputs); // element-wise — proves the init property round-trips
        Assert.Equal("hw:1", reloaded.AudioOutputs[1].DeviceId);
        Assert.Equal("fx", reloaded.AudioOutputs[1].GroupId);
    }
}
