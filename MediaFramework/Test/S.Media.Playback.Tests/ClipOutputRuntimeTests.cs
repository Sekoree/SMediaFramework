using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;
using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class ClipOutputRuntimeTests
{
    [Fact]
    public void ClipAudioOutputRuntime_AddRemoveSource_TracksSourceAndReleasesOutput()
    {
        var released = false;
        var format = new AudioFormat(48_000, 2);
        using (var runtime = new ClipAudioOutputRuntime(
                   "out-a",
                   new RecordingAudioOutput(format),
                   new FakePlaybackClock(),
                   releaseOutput: () => released = true,
                   displayName: "Output A"))
        {
            var sourceId = runtime.AddSource(
                new ConstantAudioSource(format),
                [new AudioRouteSpec("out-a", SourceChannel: 0, OutputChannel: 1)],
                "cue-a");

            Assert.Equal(1, runtime.SourceCount);

            runtime.RemoveSource(sourceId);

            Assert.Equal(0, runtime.SourceCount);
        }

        Assert.True(released);
    }

    [Fact]
    public void ClipAudioOutputRuntime_UpdateAndRemoveRoute_UsesExplicitRouteId()
    {
        var format = new AudioFormat(48_000, 2);
        using var runtime = new ClipAudioOutputRuntime(
            "out-a",
            new RecordingAudioOutput(format),
            new FakePlaybackClock(),
            displayName: "Output A");

        var sourceId = runtime.AddSource(
            new ConstantAudioSource(format),
            [new AudioRouteSpec("out-a", SourceChannel: 0, OutputChannel: 1)],
            "cue-a",
            (_, _) => "cue-a_route0");

        runtime.SetRouteGain(sourceId, "cue-a_route0", -6, muted: false);
        runtime.UpdateRoute(
            sourceId,
            "cue-a_route0",
            new AudioRouteSpec("out-a", SourceChannel: 1, OutputChannel: 2, GainDb: -3));

        Assert.True(runtime.RemoveRoute(sourceId, "cue-a_route0"));
        Assert.Equal(1, runtime.SourceCount);
    }

    [Fact]
    public void ClipAudioOutputRuntime_AddSource_RollsBackWhenRouteInvalid()
    {
        var format = new AudioFormat(48_000, 1);
        using var runtime = new ClipAudioOutputRuntime(
            "out-a",
            new RecordingAudioOutput(format),
            new FakePlaybackClock(),
            displayName: "Output A");

        var ex = Assert.Throws<InvalidOperationException>(() => runtime.AddSource(
            new ConstantAudioSource(format),
            [new AudioRouteSpec("out-a", SourceChannel: 1, OutputChannel: 1)],
            "cue-a"));

        Assert.Contains("Failed to route cue source", ex.Message);
        Assert.Equal(0, runtime.SourceCount);
    }

    [Fact]
    public void ClipCompositionRuntime_SetMasterAndAddLayer_StartsSinglePumpAndTracksLayer()
    {
        using var runtime = new ClipCompositionRuntime(
            new ClipCompositionDefinition("comp-a", "Comp A", 320, 180, 30, 1),
            []);

        runtime.SetClockMaster(new FakePlaybackClock());
        runtime.EnsurePumpStarted();
        runtime.EnsurePumpStarted();

        Assert.Equal(1, runtime.PumpStartCount);
        Assert.True(runtime.GetStats().ClockMastered);

        using (runtime.AddLayer(
                   new VideoFormat(160, 90, PixelFormat.Bgra32, new Rational(30, 1)),
                   new VideoPlacementSpec("comp-a", LayerIndex: 1, Opacity: 0.5, Placement: "Letterbox")))
        {
            Assert.Equal(1, runtime.LayerCount);
        }

        Assert.Equal(0, runtime.LayerCount);
    }

    [Fact]
    public void ClipCompositionRuntime_LayerSlotDispose_IsIdempotent()
    {
        using var runtime = new ClipCompositionRuntime(
            new ClipCompositionDefinition("comp-a", "Comp A", 320, 180, 30, 1),
            []);

        var layer = runtime.AddLayer(
            new VideoFormat(160, 90, PixelFormat.Bgra32, new Rational(30, 1)),
            new VideoPlacementSpec("comp-a", LayerIndex: 1, Opacity: 0.5, Placement: "Letterbox"));

        Assert.Equal(1, runtime.LayerCount);
        layer.Dispose();
        layer.Dispose();

        Assert.Equal(0, runtime.LayerCount);
    }

    [Fact]
    public void ClipCompositionRuntime_LayerSlotUpdatePlacement_ReappliesMutableLayerState()
    {
        using var runtime = new ClipCompositionRuntime(
            new ClipCompositionDefinition("comp-a", "Comp A", 320, 180, 30, 1),
            []);

        using var layer = runtime.AddLayer(
            new VideoFormat(160, 90, PixelFormat.Bgra32, new Rational(30, 1)),
            new VideoPlacementSpec("comp-a", LayerIndex: 1, Opacity: 0.5, Placement: "Letterbox"));

        layer.UpdatePlacement(new VideoPlacementSpec(
            "comp-a",
            LayerIndex: 4,
            Opacity: 0.25,
            Placement: "Stretch",
            DestX: 0.25,
            DestY: 0.1,
            DestWidth: 0.5,
            DestHeight: 0.8,
            CropLeft: 0.1,
            CropRight: 0.2));

        Assert.Equal(4, layer.LayerIndex);
        Assert.Equal(0.25f, layer.Opacity);
    }

    private sealed class ConstantAudioSource(AudioFormat format) : IAudioSource
    {
        public AudioFormat Format { get; } = format;

        public bool IsExhausted => false;

        public int ReadInto(Span<float> destination)
        {
            destination.Fill(0.25f);
            return destination.Length;
        }
    }

    private sealed class RecordingAudioOutput(AudioFormat format) : IAudioOutput
    {
        public AudioFormat Format { get; } = format;

        public void Submit(ReadOnlySpan<float> packedSamples) { }
    }

    private sealed class FakePlaybackClock : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;

        public bool IsAdvancing => true;
    }
}
