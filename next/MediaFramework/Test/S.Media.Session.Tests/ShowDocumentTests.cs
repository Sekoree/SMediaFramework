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
            Outputs: [],
            Routes: [],
            Devices: ["dev-a"]);

        var reloaded = ShowDocument.FromJson(doc.ToJson());

        // Element-wise (Assert.Equal on IEnumerable) — proves a lossless D10 round-trip. Whole-record
        // equality won't do: a record compares its list *properties* by reference (array vs List), not deep.
        Assert.Equal(doc.Version, reloaded.Version);
        Assert.Equal(doc.Cues, reloaded.Cues);
        Assert.Equal(doc.Clips, reloaded.Clips);
        Assert.Equal(doc.Compositions, reloaded.Compositions);
        Assert.Equal(doc.Devices, reloaded.Devices);
        Assert.Equal(TimeSpan.FromMilliseconds(250), reloaded.Cues[0].PreWait);
        Assert.Equal("screen", reloaded.Clips[1].CompositionId);
        Assert.Equal(2, reloaded.Clips[1].LayerIndex);
        Assert.Equal(1920, reloaded.Compositions[0].Width);
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
}
