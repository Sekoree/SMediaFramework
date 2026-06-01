using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class AudioClipTests : IDisposable
{
    private readonly string _stereoWav;

    public AudioClipTests()
    {
        _stereoWav = Path.Combine(Path.GetTempPath(), $"sm_clip_{Guid.NewGuid():N}.wav");
        SineWav.Write(_stereoWav, sampleRate: 48000, channels: 2, frequencyHz: 440, durationSeconds: 0.1);
        MediaFrameworkRuntime.Init().UseFFmpeg();
    }

    public void Dispose()
    {
        MediaFrameworkRuntime.Shutdown();
        if (File.Exists(_stereoWav))
            File.Delete(_stereoWav);
    }

    [Fact]
    public void OpenFile_MixdownToMono_ReducesChannels()
    {
        var clip = AudioClip.OpenFile(_stereoWav, mixdown: new ChannelMap([0]));
        Assert.Equal(1, clip.Format.Channels);
        Assert.True(clip.SamplesPerChannel > 0);
    }

    [Fact]
    public void Voice_StopAtEndOfClip_BecomesExhausted()
    {
        // Regression: Stop() called when the cursor is already at clip end must still
        // drive the release ramp to completion so IsExhausted becomes true. Before the
        // fix the voice lingered forever returning zero samples (never reaped).
        var format = new AudioFormat(48_000, 2);
        const int frames = 480;
        var data = new float[frames * format.Channels];
        for (var i = 0; i < data.Length; i++)
            data[i] = 0.25f;
        var clip = AudioClip.FromSamples(format, data);
        var voice = clip.CreateVoice();

        // Drain the whole clip in one read; the cursor lands exactly on the end and the
        // voice is naturally exhausted (without _stopped having been set).
        var buf = new float[frames * format.Channels];
        Assert.Equal(buf.Length, voice.ReadInto(buf));
        Assert.True(voice.IsExhausted);

        // Stop() restarts the release ramp and clears natural exhaustion (the bug window).
        voice.Stop();

        // A few reads must complete the (silent) release and re-exhaust the voice
        // instead of spinning forever returning zero samples.
        for (var i = 0; i < 4 && !voice.IsExhausted; i++)
            voice.ReadInto(buf);

        Assert.True(voice.IsExhausted);
        Assert.Equal(0, voice.ReadInto(buf));
    }

    [Fact]
    public void Voice_ReadInto_NoAllocationsAfterWarmup()
    {
        var format = new AudioFormat(48_000, 2);
        var samples = 480 * 4;
        var data = new float[samples * 2];
        for (var i = 0; i < data.Length; i++)
            data[i] = 0.1f;
        var clip = AudioClip.FromSamples(format, data);
        var voice = clip.CreateVoice();
        var buf = new float[480 * format.Channels];
        Assert.True(voice.ReadInto(buf) > 0);
        voice.ReadInto(buf);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
            voice.ReadInto(buf);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }
}
