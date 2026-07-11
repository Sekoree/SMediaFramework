using S.Media.Core.Buses;
using Xunit;

namespace S.Media.Session.Tests;

/// <summary>Phase 4 session integration: audio taps ride along on fired clips; metadata publishes on fire.</summary>
public sealed class BusIntegrationTests
{
    private static ShowDocument OneAudioCue(string mediaPath = "fake://song") => new(
        Version: 1,
        Cues: [new CueDefinition("cue1", 1, "One")],
        Clips: [new ShowClipBinding("cue1", mediaPath)],
        Compositions: [],
        Routes: []);

    private sealed class CountingTap : IAudioOutput
    {
        private long _samples;
        public AudioFormat Format { get; } = new(48_000, 2);
        public long Samples => Volatile.Read(ref _samples);
        public void Submit(ReadOnlySpan<float> packedSamples) => Interlocked.Add(ref _samples, packedSamples.Length);
    }

    private sealed class RecordingSink : IBusMetadataSink
    {
        public readonly List<MediaItemMetadata> Items = [];
        public void OnItemMetadata(MediaItemMetadata metadata)
        {
            lock (Items) Items.Add(metadata);
        }

        public void OnFrameStats(in FrameStatsMetadata stats) { }
    }

    [Fact]
    public async Task RegisteredAudioTap_ReceivesSamplesFromAFiredClip()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 2048));
        session.LoadDocument(OneAudioCue());

        var tap = new CountingTap();
        await session.RegisterAudioTapAsync(tap);

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cue1"));

        // The router pumps chunks on its own thread; poll briefly for the tap to see audio.
        for (var i = 0; i < 100 && tap.Samples == 0; i++)
            await Task.Delay(20);
        Assert.True(tap.Samples > 0, "tap received no audio from the fired clip");

        await session.StopAsync(fade: false);
    }

    [Fact]
    public async Task UnregisteredTap_IsExcludedFromLaterFires()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry(chunks: 2048));
        session.LoadDocument(OneAudioCue());

        var tap = new CountingTap();
        var id = await session.RegisterAudioTapAsync(tap);
        await session.UnregisterAudioTapAsync(id);

        await session.FireCueAsync("cue1");
        await Task.Delay(200);
        Assert.Equal(0, tap.Samples);

        await session.StopAsync(fade: false);
    }

    [Fact]
    public async Task Fire_PublishesItemMetadata_WithFilenameFallbackTitle()
    {
        await using var session = new ShowSession(FakeAudioDecoderProvider.Registry());
        session.LoadDocument(OneAudioCue("fake://tracks/my-song.flac"));

        var sink = new RecordingSink();
        session.MetadataHub.Attach(sink);

        await session.FireCueAsync("cue1");

        MediaItemMetadata? published;
        lock (sink.Items) published = sink.Items.FirstOrDefault();
        Assert.NotNull(published);
        Assert.Equal("my-song", published.Title);
        Assert.Equal("fake://tracks/my-song.flac", published.SourceUri);
        Assert.Equal("my-song", session.MetadataHub.CurrentItem?.Title);
    }

    [Fact]
    public async Task Fire_RichMetadataProbe_RefinesTheFallback()
    {
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new ShowSession(
            FakeAudioDecoderProvider.Registry(),
            metadataProbe: _ =>
            {
                probed.TrySetResult();
                return new MediaItemMetadata("Real Title", "Real Artist", "Real Album");
            });
        session.LoadDocument(OneAudioCue());

        await session.FireCueAsync("cue1");
        await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The probe publishes asynchronously right after it runs; poll briefly.
        for (var i = 0; i < 100 && session.MetadataHub.CurrentItem?.Artist is null; i++)
            await Task.Delay(10);

        Assert.Equal("Real Title", session.MetadataHub.CurrentItem?.Title);
        Assert.Equal("Real Artist", session.MetadataHub.CurrentItem?.Artist);
    }
}
