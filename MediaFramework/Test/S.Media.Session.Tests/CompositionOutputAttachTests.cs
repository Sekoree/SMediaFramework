using Xunit;

namespace S.Media.Session.Tests;

public class CompositionOutputAttachTests
{
    [Fact]
    public async Task AttachCompositionOutput_attachesToExistingComposition_andRejectsUnknown()
    {
        var registry = MediaRegistry.Build(_ => { });
        await using var session = new ShowSession(registry);
        var document = new ShowDocument(
            Version: 1,
            Cues: [],
            Clips: [],
            Compositions: [new ShowComposition("screen", "Screen", 640, 480, 24, 1)],
            Routes: []);
        session.LoadDocument(document);

        // A loaded composition accepts a live output (the UI preview path); an unknown id is rejected, not thrown.
        Assert.True(await session.AttachCompositionOutputAsync("screen", new DiscardingVideoOutput()));
        Assert.False(await session.AttachCompositionOutputAsync("does-not-exist", new DiscardingVideoOutput()));
    }
}
