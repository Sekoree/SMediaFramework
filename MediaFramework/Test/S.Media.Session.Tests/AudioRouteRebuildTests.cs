using Xunit;

namespace S.Media.Session.Tests;

/// <summary>The deck/cue mid-play audio-route paths: a rebuild while the clip plays must pump the re-added
/// outputs, and one un-openable device must never silence the rest (per-route isolation) — the "route an audio
/// device mid-playback → no output" bug.</summary>
public sealed class AudioRouteRebuildTests
{
    private const string GoodDevice = "good";
    private const string BadDevice = "bad";

    /// <summary>A silent audio source long enough to survive a mid-play rebuild (~20 s at 48 kHz).</summary>
    private sealed class LongSilentSource : IAudioSource
    {
        private int _remaining = 2_000;
        public AudioFormat Format { get; } = new(48_000, 2);
        public bool IsExhausted => Volatile.Read(ref _remaining) <= 0;

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remaining) < 0)
                return 0;
            destination.Clear();
            return destination.Length - (destination.Length % Format.Channels);
        }
    }

    private sealed class LongAudioProvider : IMediaDecoderProvider
    {
        public string Name => "long-audio";
        public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;
        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => throw new NotSupportedException();
        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new LongSilentSource();
        public static IMediaRegistry Registry() => MediaRegistry.Build(b => b.AddDecoder(new LongAudioProvider()));
    }

    /// <summary>Counts submitted sample chunks so a test can prove audio actually FLOWS to an output.</summary>
    private sealed class CountingAudioOutput(AudioFormat format) : IAudioOutput
    {
        private long _submits;
        public long Submits => Volatile.Read(ref _submits);
        public AudioFormat Format => format;
        public void Submit(ReadOnlySpan<float> packedSamples) => Interlocked.Increment(ref _submits);
    }

    /// <summary>A backend where device "bad" fails to open (a fixed-rate JACK graph rejecting the clip's mix
    /// rate behaves exactly like this) and device "good" counts what it receives.</summary>
    private sealed class PickyAudioBackend : IAudioBackend
    {
        private readonly List<CountingAudioOutput> _goodOutputs = [];
        public string Name => "picky";
        public IReadOnlyList<CountingAudioOutput> GoodOutputs => _goodOutputs;

        public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
        [
            new AudioDeviceInfo(GoodDevice, "Good", MaxChannels: 2, DefaultSampleRate: 48_000, IsDefault: true),
            new AudioDeviceInfo(BadDevice, "Bad", MaxChannels: 2, DefaultSampleRate: 48_000, IsDefault: false),
        ];

        public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];

        public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
        {
            if (string.Equals(deviceId, BadDevice, StringComparison.Ordinal))
                throw new InvalidOperationException("device cannot be opened (unsupported rate).");
            var output = new CountingAudioOutput(format);
            lock (_goodOutputs)
                _goodOutputs.Add(output);
            return output;
        }

        public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
            throw new NotSupportedException();
    }

    private static ShowDocument OneCue(params ShowClipAudioRoute[] routes) => new(
        Version: 1,
        Cues: [new CueDefinition("c", 1, "C")],
        Clips: [new ShowClipBinding("c", "long://x") { AudioRoutes = routes }],
        Compositions: [], Outputs: [], Routes: [], Devices: []);

    private static async Task<long> WaitForSubmitsAsync(CountingAudioOutput output, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (output.Submits > 0)
                return output.Submits;
            await Task.Delay(50);
        }

        return output.Submits;
    }

    [Fact]
    public async Task RebuildFromZeroRoutes_PumpsTheNewDeviceOutput()
    {
        // The user-visible bug shape: media playing with NO routed device (explicitly silent), then a device is
        // routed mid-play — the rebuilt output must actually receive audio (router add-on-running + master
        // promotion), not stay silent until a re-fire.
        var backend = new PickyAudioBackend();
        await using var session = new ShowSession(LongAudioProvider.Registry(), backend);
        await session.LoadDocumentAsync(OneCue()); // empty AudioRoutes ⇒ deliberately silent at fire
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        Assert.Empty(backend.GoodOutputs); // nothing routed yet

        Assert.True(await session.RebuildActiveClipAudioOutputsAsync(
            "c", [new ShowClipAudioRoute(GoodDevice, [0, 1])]));

        var output = Assert.Single(backend.GoodOutputs);
        Assert.True(await WaitForSubmitsAsync(output, TimeSpan.FromSeconds(5)) > 0,
            "the mid-play routed device received no audio");
    }

    [Fact]
    public async Task Rebuild_WithOneBadDevice_StillPumpsTheGoodOne()
    {
        // Per-route isolation: the rebuild removes every output FIRST, so a single un-openable device used to
        // fault the whole rebuild and leave the clip totally silent. The good route must survive.
        var backend = new PickyAudioBackend();
        await using var session = new ShowSession(LongAudioProvider.Registry(), backend);
        await session.LoadDocumentAsync(OneCue(new ShowClipAudioRoute(GoodDevice, [0, 1])));
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        Assert.True(await session.RebuildActiveClipAudioOutputsAsync(
            "c",
            [
                new ShowClipAudioRoute(BadDevice, [0, 1]),
                new ShowClipAudioRoute(GoodDevice, [0, 1]),
            ]));

        var rebuilt = backend.GoodOutputs[^1]; // the good output created BY the rebuild
        Assert.True(await WaitForSubmitsAsync(rebuilt, TimeSpan.FromSeconds(5)) > 0,
            "the good device was silenced by the bad one");
    }

    [Fact]
    public async Task Fire_WithOneBadDevice_StillPlaysTheGoodRoute()
    {
        // Fire-path isolation (legacy-engine parity: a bad output surfaced a banner, playback continued): a cue
        // with one un-openable routed device plays on its remaining routes instead of faulting.
        var backend = new PickyAudioBackend();
        await using var session = new ShowSession(LongAudioProvider.Registry(), backend);
        await session.LoadDocumentAsync(OneCue(
            new ShowClipAudioRoute(BadDevice, [0, 1]),
            new ShowClipAudioRoute(GoodDevice, [0, 1])));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));

        var output = Assert.Single(backend.GoodOutputs);
        Assert.True(await WaitForSubmitsAsync(output, TimeSpan.FromSeconds(5)) > 0,
            "the good route did not play");
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
    }

    [Fact]
    public async Task FullGainMatrix_MixesMultipleInputsToOneOutput_AndReappliesLive()
    {
        var backend = new PickyAudioBackend();
        var route = new ShowClipAudioRoute(GoodDevice, Gain: 0.5f)
        {
            MatrixOutputChannels = 1,
            MatrixCells =
            [
                new ShowAudioMatrixCell(0, 0, 1f),
                new ShowAudioMatrixCell(1, 0, 0.25f),
            ],
        };
        await using var session = new ShowSession(LongAudioProvider.Registry(), backend);
        await session.LoadDocumentAsync(OneCue(route));

        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("c"));
        var output = Assert.Single(backend.GoodOutputs);
        Assert.Equal(1, output.Format.Channels); // declared full-matrix destination, not legacy stereo
        Assert.True(await WaitForSubmitsAsync(output, TimeSpan.FromSeconds(5)) > 0);

        var updated = route with
        {
            Gain = 0.75f,
            MatrixCells =
            [
                new ShowAudioMatrixCell(0, 0, 0.5f),
                new ShowAudioMatrixCell(1, 0, 0.5f),
            ],
        };
        Assert.True(await session.ApplyActiveAudioRoutesAsync("c", [updated]));
        Assert.True(Assert.Single(session.Snapshot()).IsActive);
    }
}
