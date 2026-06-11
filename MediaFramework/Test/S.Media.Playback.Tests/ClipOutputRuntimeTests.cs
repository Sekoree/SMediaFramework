using S.Media.Core.Audio;
using S.Media.Core.Clock;
using S.Media.Core.Video;
using S.Media.Effects;
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
    public void ClipAudioOutputRuntime_AddSource_DoesNotStartRouterUntilExplicitStart()
    {
        var format = new AudioFormat(48_000, 2);
        var output = new RecordingAudioOutput(format);
        using var runtime = new ClipAudioOutputRuntime(
            "out-a",
            output,
            new FakePlaybackClock(),
            displayName: "Output A");

        runtime.AddSource(
            new ConstantAudioSource(format),
            [new AudioRouteSpec("out-a", SourceChannel: 0, OutputChannel: 1)],
            "cue-a");

        Thread.Sleep(100);
        Assert.Equal(0, output.SubmittedFloats);

        runtime.EnsureStarted();
        Assert.True(SpinWait.SpinUntil(() => output.SubmittedFloats > 0, TimeSpan.FromSeconds(1)),
            "shared cue audio router did not start after EnsureStarted");
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
    public void ClipAudioOutputRuntime_AddSource_RollsBackWhenOutputChannelInvalid()
    {
        var format = new AudioFormat(48_000, 2);
        using var runtime = new ClipAudioOutputRuntime(
            "out-a",
            new RecordingAudioOutput(format),
            new FakePlaybackClock(),
            displayName: "Output A");

        var ex = Assert.Throws<InvalidOperationException>(() => runtime.AddSource(
            new ConstantAudioSource(format),
            [new AudioRouteSpec("out-a", SourceChannel: 0, OutputChannel: 3)],
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

    [Fact]
    public async Task ClipCompositionRuntime_MultiOutputPump_SharesCanvasBackingAcrossOutputs()
    {
        var a = new RecordingVideoOutput();
        var b = new RecordingVideoOutput();
        using var runtime = new ClipCompositionRuntime(
            new ClipCompositionDefinition("comp-a", "Comp A", 320, 180, 60, 1),
            [
                new ClipCompositionOutputLease("out-a", "A", a),
                new ClipCompositionOutputLease("out-b", "B", b),
            ]);

        runtime.EnsurePumpStarted();

        var deadline = Environment.TickCount64 + 5000;
        while ((a.Snapshot().Length == 0 || b.Snapshot().Length == 0) && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        try
        {
            var fromA = a.Snapshot();
            var fromB = b.Snapshot();
            Assert.True(fromA.Length > 0 && fromB.Length > 0, "pump did not deliver frames to both outputs");

            // Pair up composites both outputs received and assert each pair is two zero-copy views
            // over the SAME canvas memory (ReadOnlyMemory equality = same array + offset + length).
            // A regression to per-output deep copies would surface as differing backings.
            var shared = fromA
                .SelectMany(fa => fromB.Where(fb => fb.Pts == fa.Pts).Select(fb => (fa, fb)))
                .ToArray();
            Assert.NotEmpty(shared);
            Assert.All(shared, pair => Assert.True(
                pair.fa.Plane0.Equals(pair.fb.Plane0),
                "outputs received separately-backed frames — composition fan-out is copying again"));
        }
        finally
        {
            a.DisposeCaptured();
            b.DisposeCaptured();
        }
    }

    [Fact]
    public async Task ClipCompositionRuntime_MappedOutput_GetsMappingSizedFrames_WhileUnmappedGetsCanvas()
    {
        var mapped = new RecordingVideoOutput();
        var raw = new RecordingVideoOutput();
        var mapping = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("s1", true, 0, 0, 1, 1, DestX: 80, DestY: 45, DestWidth: 0, DestHeight: 0)],
            OutputWidth: 640,
            OutputHeight: 360);
        using var runtime = new ClipCompositionRuntime(
            new ClipCompositionDefinition("comp-a", "Comp A", 320, 180, 60, 1),
            [
                new ClipCompositionOutputLease("out-mapped", "Mapped", mapped, Mapping: mapping),
                new ClipCompositionOutputLease("out-raw", "Raw", raw),
            ]);

        runtime.EnsurePumpStarted();

        var deadline = Environment.TickCount64 + 5000;
        while ((mapped.Snapshot().Length == 0 || raw.Snapshot().Length == 0) && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        try
        {
            Assert.True(mapped.Snapshot().Length > 0 && raw.Snapshot().Length > 0,
                "pump did not deliver frames to both outputs");

            // The mapped output is configured for (and receives) the mapping's output size; the
            // unmapped output keeps the canvas size.
            Assert.Equal((640, 360), (mapped.Format.Width, mapped.Format.Height));
            Assert.Equal((320, 180), (raw.Format.Width, raw.Format.Height));

            // Live update: section-only change succeeds; unknown outputs are rejected.
            Assert.True(runtime.UpdateOutputMapping("out-mapped", mapping with
            {
                Sections = [new ClipOutputMappingSection("s1", true, 0, 0, 0.5, 1, 0, 0, 0, 0)],
            }));
            Assert.True(runtime.UpdateOutputMapping("out-mapped", null)); // clear → raw canvas again
            Assert.False(runtime.UpdateOutputMapping("nope", null));
        }
        finally
        {
            mapped.DisposeCaptured();
            raw.DisposeCaptured();
        }
    }

    [Fact]
    public async Task ClipCompositionRuntime_SingleMappedOutput_UsesIntegratedWarpPass()
    {
        var output = new RecordingVideoOutput();
        var compositor = new FakeWarpCompositor(new VideoFormat(320, 180, PixelFormat.Bgra32, new Rational(60, 1)));
        var mapping = new ClipOutputMappingSpec(
            [new ClipOutputMappingSection("s1", true, 0, 0, 1, 1, 0, 0, 0, 0)],
            OutputWidth: 640,
            OutputHeight: 360);
        using var runtime = new ClipCompositionRuntime(
            new ClipCompositionDefinition("comp-a", "Comp A", 320, 180, 60, 1),
            [new ClipCompositionOutputLease("out-a", "A", output, Mapping: mapping)],
            canvas => new ClipCompositionCompositor(compositor, RequiresBgraLayerConversion: true, BackendName: "Fake"));

        // The single mapped output routes through the canvas compositor's warp pass.
        Assert.NotNull(compositor.WarpOutput);
        Assert.Equal((640, 360), (compositor.WarpOutput!.Value.Width, compositor.WarpOutput.Value.Height));
        Assert.Equal(1, compositor.WarpSectionCount);

        runtime.EnsurePumpStarted();
        var deadline = Environment.TickCount64 + 5000;
        while (output.Snapshot().Length == 0 && Environment.TickCount64 < deadline)
            await Task.Delay(10);

        try
        {
            Assert.True(output.Snapshot().Length > 0, "pump did not deliver frames");
            // Frames come straight from the (fake-warped) canvas compositor — output configured at warp size.
            Assert.Equal((640, 360), (output.Format.Width, output.Format.Height));

            // Clearing the mapping disables the warp pass.
            Assert.True(runtime.UpdateOutputMapping("out-a", null));
            Assert.Null(compositor.WarpOutput);
        }
        finally
        {
            output.DisposeCaptured();
        }
    }

    /// <summary>CPU-style stand-in implementing the integrated warp capability: Composite returns a
    /// frame sized to the warp output when a warp pass is configured (content irrelevant).</summary>
    private sealed class FakeWarpCompositor(VideoFormat output) : IWarpPassVideoCompositor
    {
        private VideoFormat _output = output;

        public VideoFormat? WarpOutput { get; private set; }

        public int WarpSectionCount { get; private set; }

        public VideoFormat OutputFormat => _output;

        public IReadOnlyList<PixelFormat> AcceptedLayerPixelFormats => [PixelFormat.Bgra32];

        public void Configure(VideoFormat output) => _output = output;

        public void SetWarpPass(VideoFormat warpOutput, IReadOnlyList<WarpSection>? sections)
        {
            WarpOutput = sections is null ? null : warpOutput;
            WarpSectionCount = sections?.Count ?? 0;
        }

        public VideoFrame Composite(IReadOnlyList<CompositorLayer> layersBackToFront, TimeSpan presentationTime)
        {
            var fmt = WarpOutput ?? _output;
            var stride = fmt.Width * 4;
            return new VideoFrame(presentationTime, fmt, new ReadOnlyMemory<byte>(new byte[stride * fmt.Height]), stride);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingVideoOutput : IVideoOutput
    {
        private readonly object _gate = new();
        private readonly List<VideoFrame> _captured = new();

        public VideoFormat Format { get; private set; } = new(16, 16, PixelFormat.Bgra32, new Rational(30, 1));

        public IReadOnlyList<PixelFormat> AcceptedPixelFormats => [];

        public void Configure(VideoFormat format) => Format = format;

        public void Submit(VideoFrame frame)
        {
            lock (_gate)
            {
                if (_captured.Count < 4)
                {
                    _captured.Add(frame);
                    return;
                }
            }

            frame.Dispose();
        }

        public (TimeSpan Pts, ReadOnlyMemory<byte> Plane0)[] Snapshot()
        {
            lock (_gate)
                return _captured.Select(f => (f.PresentationTime, f.Planes[0])).ToArray();
        }

        public void DisposeCaptured()
        {
            lock (_gate)
            {
                foreach (var f in _captured)
                    f.Dispose();
                _captured.Clear();
            }
        }
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
        private long _submittedFloats;

        public AudioFormat Format { get; } = format;

        public long SubmittedFloats => Volatile.Read(ref _submittedFloats);

        public void Submit(ReadOnlySpan<float> packedSamples) =>
            Interlocked.Add(ref _submittedFloats, packedSamples.Length);
    }

    private sealed class FakePlaybackClock : IPlaybackClock
    {
        public TimeSpan ElapsedSinceStart => TimeSpan.Zero;

        public bool IsAdvancing => true;
    }
}
