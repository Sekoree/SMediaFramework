using HaPlay.ViewModels;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Session;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The I/O Debug page's poll: row creation/reuse/retirement against a real headless ShowSession.</summary>
public sealed class PipelineStatsViewModelTests
{
    private sealed class FakeAudioProvider : IMediaDecoderProvider
    {
        public string Name => "fake-audio";
        public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;
        public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => throw new NotSupportedException();
        public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new SilentSource();
    }

    private sealed class SilentSource : IAudioSource, ISeekableSource
    {
        private int _remaining = 4096;
        public AudioFormat Format { get; } = new(48_000, 2);
        public bool IsExhausted => Volatile.Read(ref _remaining) <= 0;

        public int ReadInto(Span<float> destination)
        {
            if (Interlocked.Decrement(ref _remaining) < 0)
                return 0;
            destination.Clear();
            return destination.Length - (destination.Length % Format.Channels);
        }

        public TimeSpan Duration => TimeSpan.FromSeconds(60);
        public TimeSpan Position { get; private set; }
        public void Seek(TimeSpan position) => Position = position;
    }

    private static ShowDocument OneAudioCue() => new(
        Version: 1,
        Cues: [new CueDefinition("cue1", 1, "One")],
        Clips: [new ShowClipBinding("cue1", "fake://1")],
        Compositions: [],
        Routes: []);

    private static ShowSession NewSession() =>
        new(MediaRegistry.Build(b => b.AddDecoder(new FakeAudioProvider())));

    [Fact]
    public async Task Refresh_CreatesRowsForActiveClip_AndRetiresThemAfterStop()
    {
        await using var session = NewSession();
        session.LoadDocument(OneAudioCue());
        Assert.Equal(CueExecutionStatus.Fired, await session.FireCueAsync("cue1"));

        var vm = new PipelineStatsViewModel
        {
            ActivePlayersProbe = () => [],
            CueSessionProbe = () => session,
        };

        vm.Refresh();
        Assert.True(vm.HasRows);
        var row = Assert.Single(vm.Rows, r => r.Kind == "Audio mix");
        Assert.Contains("Cues", row.Name);

        // Rows are keyed and reused across ticks - the sparkline keeps accumulating on the same instance.
        vm.Refresh();
        var again = Assert.Single(vm.Rows, r => r.Kind == "Audio mix");
        Assert.Same(row, again);
        Assert.Equal(2, again.SparklineSamples.Count);

        await session.StopAsync(fade: false);
        vm.Refresh();
        Assert.DoesNotContain(vm.Rows, r => r.Kind == "Audio mix");
    }

    [Fact]
    public void Refresh_NoSessions_NoRows()
    {
        var vm = new PipelineStatsViewModel
        {
            ActivePlayersProbe = () => [],
            CueSessionProbe = () => null,
        };

        vm.Refresh();

        Assert.False(vm.HasRows);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void RowSparkline_RollsAtCapacityAndTracksPeak()
    {
        var row = new PipelineStatsRowViewModel("k", "Playback", "row");

        for (var i = 1; i <= PipelineStatsRowViewModel.SparklineCapacity + 5; i++)
            row.RecordSparklineSample(i);

        Assert.Equal(PipelineStatsRowViewModel.SparklineCapacity, row.SparklineSamples.Count);
        Assert.Equal(PipelineStatsRowViewModel.SparklineCapacity + 5, row.SparklineLastSample);
        Assert.Equal(PipelineStatsRowViewModel.SparklineCapacity + 5, row.SparklinePeakSample);
        // Oldest samples rolled off the front.
        Assert.Equal(6, row.SparklineSamples[0]);
    }
}
