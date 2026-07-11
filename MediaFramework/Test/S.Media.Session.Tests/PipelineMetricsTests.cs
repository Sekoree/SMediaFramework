using Xunit;

namespace S.Media.Session.Tests;

/// <summary>The debug-stats poll surface: lock-free per-clip pipeline metrics and all-composition stats.</summary>
public sealed class PipelineMetricsTests
{
    private static ShowDocument OneAudioCue() => new(
        Version: 1,
        Cues: [new CueDefinition("cue1", 1, "One")],
        Clips: [new ShowClipBinding("cue1", "fake://1")],
        Compositions: [],
        Routes: []);

    [Fact]
    public async Task GetActiveClipPipelineMetrics_EmptyBeforeAnyFire()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(OneAudioCue());

        Assert.Empty(session.GetActiveClipPipelineMetrics());
    }

    [Fact]
    public async Task GetActiveClipPipelineMetrics_ReportsActiveClipWithMetrics()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 512));
        session.LoadDocument(OneAudioCue());

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cue1"));

        var entry = Assert.Single(session.GetActiveClipPipelineMetrics());
        Assert.Equal(ShowSession.DefaultGroup, entry.GroupId);
        Assert.Equal("cue1", entry.CueId);
        Assert.NotNull(entry.Metrics);
        Assert.NotNull(entry.Metrics.Clock);
    }

    [Fact]
    public async Task GetActiveClipPipelineMetrics_EmptyAfterStop()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(OneAudioCue());
        await session.FireCueAsync("cue1");
        await session.StopAsync(fade: false);

        Assert.Empty(session.GetActiveClipPipelineMetrics());
    }

    [Fact]
    public async Task GetAllCompositionStats_EmptyWithoutCompositions()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(OneAudioCue());

        Assert.Empty(session.GetAllCompositionStats());
    }
}
