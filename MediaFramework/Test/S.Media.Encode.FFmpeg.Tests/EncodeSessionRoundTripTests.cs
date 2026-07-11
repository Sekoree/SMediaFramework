using S.Media.Decode.FFmpeg;
using S.Media.Decode.FFmpeg.Video;
using Xunit;

namespace S.Media.Encode.FFmpeg.Tests;

/// <summary>
/// End-to-end: synthetic frames/PCM through an <see cref="FFmpegEncodeSession"/> into a real container,
/// re-opened with the app's own decode stack. Revives the pre-rewrite FFmpegMuxRoundTripTests for the
/// packet-fanout session architecture (no external ffmpeg CLI needed - sources are synthesized in-test).
/// </summary>
public sealed class EncodeSessionRoundTripTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            try { File.Delete(p); }
            catch { /* best effort */ }
        }
    }

    private string TempPath(string extension)
    {
        var p = Path.Combine(Path.GetTempPath(), $"mf_enc_{Guid.NewGuid():N}{extension}");
        _tempPaths.Add(p);
        return p;
    }

    private static VideoFrame MakeBgraFrame(int width, int height, int index, Rational fps)
    {
        var stride = width * 4;
        var bytes = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                bytes[o] = (byte)((x * 255 / width + index * 4) & 0xFF);       // B ramp scrolls per frame
                bytes[o + 1] = (byte)(y * 255 / height);                       // G ramp
                bytes[o + 2] = (byte)(index * 8 & 0xFF);                       // R changes per frame
                bytes[o + 3] = 255;
            }
        }

        return new VideoFrame(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond * index * fps.Denominator / fps.Numerator),
            new VideoFormat(width, height, PixelFormat.Bgra32, fps),
            [bytes],
            [stride]);
    }

    private static float[] MakeSine(int sampleRate, int channels, double seconds, float amp = 0.25f)
    {
        var frames = (int)(sampleRate * seconds);
        var interleaved = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var s = amp * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate);
            for (var ch = 0; ch < channels; ch++)
                interleaved[i * channels + ch] = s;
        }

        return interleaved;
    }

    private static void PumpAudio(FFmpegEncodeAudioSink sink, float[] interleaved, int channels, int chunkFrames = 1024)
    {
        var totalFrames = interleaved.Length / channels;
        for (var off = 0; off < totalFrames; off += chunkFrames)
        {
            var n = Math.Min(chunkFrames, totalFrames - off);
            sink.Submit(interleaved.AsSpan(off * channels, n * channels));
        }
    }

    private static async Task FinishAsync(FFmpegEncodeSession session)
    {
        // The worker drains asynchronously; a healthy finish is quick, hang = test failure.
        var done = await Task.WhenAny(session.FinishAsync(), Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(session.Completion, done);
        await session.Completion;
    }

    [Fact]
    public async Task VideoAndAudio_Mp4_RoundTrips()
    {
        var outPath = TempPath(".mp4");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Mp4,
            Video = new VideoEncodeOptions { Codec = EncodeVideoCodec.H264, Crf = 30, Preset = "ultrafast", GopSize = 10 },
            AudioLegs = [new AudioLegOptions { Codec = EncodeAudioCodec.Aac, Channels = 2 }],
        };
        if (options.Validate() is { Count: > 0 } errors)
        {
            // No H.264/AAC in this FFmpeg build - nothing to test here.
            Assert.Contains(errors, e => e.Contains("encoder"));
            return;
        }

        var fps = new Rational(30, 1);
        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath)))
        {
            session.VideoSink!.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, fps));
            for (var i = 0; i < 30; i++)
                session.VideoSink.Submit(MakeBgraFrame(160, 120, i, fps));
            PumpAudio(session.AudioSinks[0], MakeSine(48_000, 2, 1.0), channels: 2);
            await FinishAsync(session);

            var metrics = session.GetMetrics();
            Assert.Equal(30, metrics.VideoFramesSubmitted);
            Assert.Equal(30, metrics.VideoFramesEncoded);
            Assert.All(metrics.Sinks, s => Assert.True(s.Healthy, s.Error));
            Assert.True(metrics.Sinks[0].BytesWritten > 256);
        }

        Assert.True(new FileInfo(outPath).Length > 256);

        using var dec = MediaContainerDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        Assert.True(dec.HasVideo);
        Assert.True(dec.HasAudio);
        Assert.Equal(160, dec.Video.Format.Width);
        Assert.Equal(120, dec.Video.Format.Height);
        Assert.True(dec.Video.TryReadNextFrame(out var frame));
        frame.Dispose();
    }

    [Fact]
    public async Task Video_Scaled_To720Wide_ProducesScaledStream()
    {
        var outPath = TempPath(".mp4");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Mp4,
            OutputMode = EncodeOutputMode.VideoOnly,
            Video = new VideoEncodeOptions
            {
                Codec = EncodeVideoCodec.H264, Crf = 30, Preset = "ultrafast",
                ScaleWidth = 96, // height derived preserving 4:3 → 72
            },
        };
        if (options.Validate().Count > 0)
            return;

        var fps = new Rational(30, 1);
        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath)))
        {
            session.VideoSink!.Configure(new VideoFormat(128, 96, PixelFormat.Bgra32, fps));
            for (var i = 0; i < 15; i++)
                session.VideoSink.Submit(MakeBgraFrame(128, 96, i, fps));
            await FinishAsync(session);
        }

        using var dec = MediaContainerDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        Assert.Equal(96, dec.Video.Format.Width);
        Assert.Equal(72, dec.Video.Format.Height);
    }

    [Fact]
    public async Task MultiAudioTrack_Matroska_CarriesBothTracksWithMetadata()
    {
        var outPath = TempPath(".mkv");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Matroska,
            OutputMode = EncodeOutputMode.AudioOnly,
            AudioLegs =
            [
                new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = 2, Name = "Program", Language = "eng" },
                new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = 1, Name = "Commentary", Language = "jpn" },
            ],
        };
        if (options.Validate().Count > 0)
            return;

        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath)))
        {
            PumpAudio(session.AudioSinks[0], MakeSine(48_000, 2, 0.5), channels: 2);
            PumpAudio(session.AudioSinks[1], MakeSine(48_000, 1, 0.5), channels: 1);
            await FinishAsync(session);
        }

        // The decode facade exposes one primary audio source, so count the container's streams directly.
        var (audioStreams, videoStreams) = ProbeStreamCounts(outPath);
        Assert.Equal(2, audioStreams);
        Assert.Equal(0, videoStreams);
    }

    private static unsafe (int Audio, int Video) ProbeStreamCounts(string path)
    {
        global::FFmpeg.AutoGen.AVFormatContext* fmt = null;
        var ret = global::FFmpeg.AutoGen.ffmpeg.avformat_open_input(&fmt, path, null, null);
        if (ret < 0)
            throw new InvalidOperationException($"avformat_open_input failed: {ret}");
        try
        {
            global::FFmpeg.AutoGen.ffmpeg.avformat_find_stream_info(fmt, null);
            int audio = 0, video = 0;
            for (var i = 0; i < fmt->nb_streams; i++)
            {
                switch (fmt->streams[i]->codecpar->codec_type)
                {
                    case global::FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_AUDIO: audio++; break;
                    case global::FFmpeg.AutoGen.AVMediaType.AVMEDIA_TYPE_VIDEO: video++; break;
                }
            }

            return (audio, video);
        }
        finally
        {
            global::FFmpeg.AutoGen.ffmpeg.avformat_close_input(&fmt);
        }
    }

    [Fact]
    public async Task AudioOnly_Flac_PreservesSignalRms()
    {
        const int sampleRate = 48_000;
        const int channels = 2;
        const float amp = 0.25f;
        var outPath = TempPath(".mka");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Matroska,
            OutputMode = EncodeOutputMode.AudioOnly,
            AudioLegs = [new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = channels }],
        };
        if (options.Validate().Count > 0)
            return;

        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath), sampleRate))
        {
            PumpAudio(session.AudioSinks[0], MakeSine(sampleRate, channels, 0.5, amp), channels);
            await FinishAsync(session);
        }

        using var dec = MediaContainerDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        var buf = new float[1024 * channels];
        double sumSq = 0;
        long count = 0;
        int read;
        while ((read = dec.Audio.ReadInto(buf)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                sumSq += buf[i] * (double)buf[i];
                count++;
            }
        }

        Assert.True(count > 20_000, $"expected a meaningful amount of decoded audio, got {count} samples");
        var rms = Math.Sqrt(sumSq / count);
        var expected = amp / Math.Sqrt(2);
        Assert.InRange(rms, expected * 0.6, expected * 1.4);
    }

    [Fact]
    public async Task AudioLeg_Resampled_To44k_RoundTrips()
    {
        var outPath = TempPath(".mka");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Matroska,
            OutputMode = EncodeOutputMode.AudioOnly,
            AudioLegs = [new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = 2, SampleRate = 44_100 }],
        };
        if (options.Validate().Count > 0)
            return;

        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath), audioInputSampleRate: 48_000))
        {
            PumpAudio(session.AudioSinks[0], MakeSine(48_000, 2, 0.5), channels: 2);
            await FinishAsync(session);
        }

        using var dec = MediaContainerDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        Assert.Equal(44_100, dec.Audio.Format.SampleRate);
    }

    [Fact]
    public void Validate_RejectsFlvWithTwoAudioTracks()
    {
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Flv,
            AudioLegs = [new AudioLegOptions(), new AudioLegOptions()],
        };

        var errors = options.Validate(probeEncoders: false);
        Assert.Contains(errors, e => e.Contains("single audio track"));
    }

    [Fact]
    public void Validate_RejectsBitrateAndCrfTogether()
    {
        var options = new EncodeSessionOptions
        {
            Video = new VideoEncodeOptions { BitrateBps = 4_000_000, Crf = 23 },
        };

        var errors = options.Validate(probeEncoders: false);
        Assert.Contains(errors, e => e.Contains("not both"));
    }
}
