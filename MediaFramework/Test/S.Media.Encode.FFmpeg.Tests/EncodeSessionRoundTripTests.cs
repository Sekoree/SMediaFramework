using S.Media.Decode.FFmpeg;
using S.Media.Decode.FFmpeg.Video;
using S.Media.Encode.FFmpeg.Internal;
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

    private static async Task SubmitVideoWithoutOverflowAsync(FFmpegEncodeSession session, VideoFrame frame)
    {
        // The encode queue is deliberately bounded for live use and drops oldest frames under a burst.
        // Tests that assert an exact encoded count must pace their synthetic producer accordingly.
        while (session.GetMetrics() is { } metrics
               && metrics.VideoQueueDepth >= metrics.VideoQueueCapacity - 1)
            await Task.Delay(1);
        session.VideoSink!.Submit(frame);
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
                await SubmitVideoWithoutOverflowAsync(session, MakeBgraFrame(160, 120, i, fps));
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

    [Theory]
    [InlineData(EncodeVideoCodec.ProRes422)]
    [InlineData(EncodeVideoCodec.ProRes4444)]
    public async Task ProRes_EncoderOpens_AndProducesFrames(EncodeVideoCodec codec)
    {
        // Review H8 regression guard: prores_ks accepts ONLY 10-bit pixel formats. The old mapping
        // handed it 8/12-bit variants and avcodec_open2 failed the moment an operator picked ProRes.
        var outPath = TempPath(".mov");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Mov,
            OutputMode = EncodeOutputMode.VideoOnly,
            Video = new VideoEncodeOptions { Codec = codec, BitrateBps = 20_000_000 },
        };
        if (options.Validate() is { Count: > 0 })
            return; // no ProRes encoder in this FFmpeg build

        var fps = new Rational(30, 1);
        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath)))
        {
            session.VideoSink!.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, fps));
            for (var i = 0; i < 10; i++)
                session.VideoSink.Submit(MakeBgraFrame(160, 120, i, fps));
            await FinishAsync(session);

            var metrics = session.GetMetrics();
            // The guard is "the encoder OPENED and frames flow" (the H8 bug failed avcodec_open2 with
            // zero frames). ProRes is slow enough that the bounded submit queue may drop a frame or two
            // under a burst - that's the drop-oldest contract, not a regression.
            Assert.True(metrics.VideoFramesEncoded >= 5, $"only {metrics.VideoFramesEncoded} frames encoded");
            Assert.All(metrics.Sinks, s => Assert.True(s.Healthy, s.Error));
        }

        Assert.True(new FileInfo(outPath).Length > 256);
    }

    [Theory]
    [InlineData(60, 30, 60, 28, 33)]   // faster input: ~half the frames are DROPPED onto the 30 fps grid
    [InlineData(15, 30, 30, 55, 62)]   // slower input: gaps are FILLED by re-encoding the held frame
    public async Task FixedFps_ReallyConvertsTheFrameRate(
        int inputFps, int targetFps, int submitted, int minEncoded, int maxEncoded)
    {
        // Review H7: a configured FPS used to be metadata only - 60 fps input "configured as 30" still
        // encoded ~60 frames/s. The tick scheduler now drops/duplicates onto the target timebase.
        var outPath = TempPath(".mp4");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Mp4,
            OutputMode = EncodeOutputMode.VideoOnly,
            Video = new VideoEncodeOptions
            {
                Codec = EncodeVideoCodec.H264, Crf = 35, Preset = "ultrafast", GopSize = 10, Fps = targetFps,
            },
        };
        if (options.Validate() is { Count: > 0 })
            return; // no H.264 in this build

        var fps = new Rational(inputFps, 1);
        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath)))
        {
            session.VideoSink!.Configure(new VideoFormat(160, 120, PixelFormat.Bgra32, fps));
            for (var i = 0; i < submitted; i++)
            {
                session.VideoSink.Submit(MakeBgraFrame(160, 120, i, fps));
                await Task.Delay(2); // pace: the submit queue is bounded drop-oldest - a burst would race it
            }

            await FinishAsync(session);

            var metrics = session.GetMetrics();
            Assert.InRange(metrics.VideoFramesEncoded, minEncoded, maxEncoded);
        }

        // The produced stream carries (approximately - container duration rounding) the TARGET rate.
        using var dec = MediaContainerDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        var rate = dec.Video.Format.FrameRate;
        var actualFps = (double)rate.Numerator / Math.Max(1, rate.Denominator);
        Assert.InRange(actualFps, targetFps - 2.0, targetFps + 2.0);
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
    public async Task InputFormatChange_MidRecording_KeepsTheLockedOutputFormat()
    {
        var outPath = TempPath(".mp4");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Mp4,
            OutputMode = EncodeOutputMode.VideoOnly,
            Video = new VideoEncodeOptions { Codec = EncodeVideoCodec.H264, Crf = 30, Preset = "ultrafast" },
        };
        if (options.Validate().Count > 0)
            return;

        var fps = new Rational(30, 1);
        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath)))
        {
            // First format locks the output (128x96); the mid-recording switch to a larger source must
            // reconvert instead of faulting (the auto-sized deck canvas resizes on track changes).
            session.VideoSink!.Configure(new VideoFormat(128, 96, PixelFormat.Bgra32, fps));
            for (var i = 0; i < 10; i++)
                await SubmitVideoWithoutOverflowAsync(session, MakeBgraFrame(128, 96, i, fps));

            session.VideoSink.Configure(new VideoFormat(192, 144, PixelFormat.Bgra32, fps));
            for (var i = 10; i < 20; i++)
                await SubmitVideoWithoutOverflowAsync(session, MakeBgraFrame(192, 144, i, fps));

            await FinishAsync(session);
            var metrics = session.GetMetrics();
            Assert.Equal(20, metrics.VideoFramesEncoded);
            Assert.All(metrics.Sinks, s => Assert.True(s.Healthy, s.Error));
        }

        using var dec = MediaContainerDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        Assert.Equal(128, dec.Video.Format.Width); // locked to the FIRST format
        Assert.Equal(96, dec.Video.Format.Height);
    }

    [Fact]
    public async Task CombinedAudioSink_SplitsConcatenatedChannelsOntoTracks()
    {
        var outPath = TempPath(".mka");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Matroska,
            OutputMode = EncodeOutputMode.AudioOnly,
            AudioLegs =
            [
                new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = 2, Name = "Program" },
                new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = 1, Name = "Commentary" },
            ],
        };
        if (options.Validate().Count > 0)
            return;

        const int sampleRate = 48_000;
        const float amp = 0.25f;
        using (var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath), sampleRate))
        {
            var combined = session.CombinedAudioSink!;
            Assert.Equal(3, combined.Format.Channels); // stereo track + mono track, concatenated

            // Combined ch 0-1 (track 1) carries a sine; ch 2 (track 2) stays silent.
            const int frames = sampleRate / 2;
            var interleaved = new float[frames * 3];
            for (var i = 0; i < frames; i++)
            {
                var s = amp * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate);
                interleaved[i * 3] = s;
                interleaved[i * 3 + 1] = s;
                interleaved[i * 3 + 2] = 0f;
            }

            const int chunkFrames = 1024;
            for (var off = 0; off < frames; off += chunkFrames)
            {
                var n = Math.Min(chunkFrames, frames - off);
                combined.Submit(interleaved.AsSpan(off * 3, n * 3));
            }

            await FinishAsync(session);
        }

        var (audioStreams, _) = ProbeStreamCounts(outPath);
        Assert.Equal(2, audioStreams);

        // The primary decoded track (track 1, stereo) must carry the sine, proving the split kept the
        // signal on the right side of the concatenation.
        using var dec = MediaContainerDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        Assert.Equal(2, dec.Audio.Format.Channels);
        var buf = new float[4096];
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

        var rms = Math.Sqrt(sumSq / Math.Max(1, count));
        Assert.InRange(rms, amp / Math.Sqrt(2) * 0.6, amp / Math.Sqrt(2) * 1.4);
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

    [Fact]
    public async Task FixedFps_KeepAliveAndRestartedMediaTimelines_ContinueWithoutBlackGap()
    {
        var outPath = TempPath(".mp4");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Mp4,
            OutputMode = EncodeOutputMode.VideoOnly,
            Video = new VideoEncodeOptions
            {
                Codec = EncodeVideoCodec.H264,
                Crf = 35,
                Preset = "ultrafast",
                GopSize = 10,
                Fps = 30,
            },
        };
        if (options.Validate().Count > 0)
            return;

        var fps = new Rational(30, 1);
        using var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath));
        session.VideoSink!.Configure(new VideoFormat(128, 96, PixelFormat.Bgra32, fps));

        // Filler deliberately has no source clock. Then two clips each restart at media time zero,
        // followed by filler again. Every boundary must advance one target tick instead of dropping
        // until the restarted clock catches up with the session lifetime.
        for (var i = 0; i < 10; i++)
        {
            session.VideoSink.SubmitTimelineContinuation(MakeBgraFrame(128, 96, 0, fps));
            await Task.Delay(2);
        }
        for (var segment = 0; segment < 2; segment++)
        for (var i = 0; i < 10; i++)
        {
            session.VideoSink.Submit(MakeBgraFrame(128, 96, i, fps));
            await Task.Delay(2);
        }
        for (var i = 0; i < 5; i++)
        {
            session.VideoSink.SubmitTimelineContinuation(MakeBgraFrame(128, 96, 0, fps));
            await Task.Delay(2);
        }

        await FinishAsync(session);
        var metrics = session.GetMetrics();
        Assert.Equal(35, metrics.VideoFramesSubmitted);
        Assert.Equal(35, metrics.VideoFramesEncoded);
        Assert.Equal(0, metrics.VideoFramesDropped);
    }

    [Fact]
    public async Task FixedFps_VideoPacketsCarryDurationAndStreamCadence()
    {
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.MpegTs,
            OutputMode = EncodeOutputMode.VideoOnly,
            Video = new VideoEncodeOptions
            {
                Codec = EncodeVideoCodec.H264,
                Crf = 35,
                Preset = "ultrafast",
                GopSize = 10,
                Fps = 60,
            },
        };
        if (options.Validate().Count > 0)
            return;

        var sink = new RecordingPacketSink();
        using var session = FFmpegEncodeSession.CreateWithSinks(options, [sink]);
        var fps = new Rational(60, 1);
        session.VideoSink!.Configure(new VideoFormat(128, 96, PixelFormat.Bgra32, fps));
        for (var i = 0; i < 12; i++)
        {
            session.VideoSink.Submit(MakeBgraFrame(128, 96, i, fps));
            await Task.Delay(2);
        }

        await FinishAsync(session);

        Assert.Equal(60, sink.VideoFrameRateNumerator);
        Assert.Equal(1, sink.VideoFrameRateDenominator);
        Assert.NotEmpty(sink.VideoPacketDurations);
        Assert.All(sink.VideoPacketDurations, duration => Assert.Equal(1_500, duration));
    }

    [Fact]
    public void FixedFps_UsesFrameClockInsideCodec_NotTransportClock()
    {
        var options = new VideoEncodeOptions
        {
            Codec = EncodeVideoCodec.H264,
            Crf = 35,
            Preset = "ultrafast",
            Fps = 60,
        };
        if (!FfmpegEncodeMaps.VideoEncoderAvailable(options.Codec))
            return;

        using var core = new FfmpegVideoEncoderCore(
            options,
            new VideoFormat(128, 96, PixelFormat.Bgra32, new Rational(60, 1)));

        Assert.Equal(1, core.CodecTimeBase.num);
        Assert.Equal(60, core.CodecTimeBase.den);
        Assert.Equal(1, core.TimeBase.num);
        Assert.Equal(90_000, core.TimeBase.den);
    }

    [Fact]
    public void SourceFollowingFps_UsesInputFrameClockInsideCodec()
    {
        var options = new VideoEncodeOptions
        {
            Codec = EncodeVideoCodec.Mpeg4,
            BitrateBps = 1_000_000,
            Fps = 0,
        };
        if (!FfmpegEncodeMaps.VideoEncoderAvailable(options.Codec))
            return;

        using var core = new FfmpegVideoEncoderCore(
            options,
            new VideoFormat(128, 96, PixelFormat.Bgra32, new Rational(30_000, 1_001)));

        Assert.Equal(1_001, core.CodecTimeBase.num);
        Assert.Equal(30_000, core.CodecTimeBase.den);
        Assert.Equal(1, core.TimeBase.num);
        Assert.Equal(90_000, core.TimeBase.den);
    }

    [Fact]
    public void ConstantBitrateLowLatency_ProgramsVbvAndDisablesBFrames()
    {
        var options = new VideoEncodeOptions
        {
            Codec = EncodeVideoCodec.H264,
            BitrateBps = 2_000_000,
            BitrateMode = EncodeVideoBitrateMode.Constant,
            BufferSizeBits = 1_000_000,
            MaxBFrames = 0,
            LowLatencyTune = true,
            Preset = "ultrafast",
            Fps = 30,
        };
        if (!FfmpegEncodeMaps.VideoEncoderAvailable(options.Codec))
            return;

        // Transport mode also exercises libx264's nal-hrd=cbr option. A typo/unsupported option
        // fails encoder-open here instead of silently degrading the UI's CBR promise.
        using var core = new FfmpegVideoEncoderCore(
            options,
            new VideoFormat(128, 96, PixelFormat.Bgra32, new Rational(30, 1)),
            enableConstantBitrateFiller: true);

        Assert.Equal(2_000_000, core.MinimumBitrate);
        Assert.Equal(2_000_000, core.MaximumBitrate);
        Assert.Equal(1_000_000, core.VbvBufferSize);
        Assert.Equal(0, core.MaximumBFrames);
    }

    [Fact]
    public unsafe void AudioCore_InputGapAdvancesPacketTimeline_InsteadOfCompressingDroppedTime()
    {
        if (!FfmpegEncodeMaps.AudioEncoderAvailable(EncodeAudioCodec.Flac))
            return;

        using var core = new FfmpegAudioEncoderCore(
            new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = 2, SampleRate = 48_000 },
            new AudioFormat(48_000, 2));
        var packetPts = new List<long>();
        void Capture(IntPtr packet) =>
            packetPts.Add(((global::FFmpeg.AutoGen.AVPacket*)packet)->pts);

        core.Submit(new float[4_800 * 2], inputStartFrame: 0, Capture);
        core.Submit(new float[4_800 * 2], inputStartFrame: 48_000, Capture);
        core.Flush(Capture);

        Assert.NotEmpty(packetPts);
        Assert.True(packetPts.SequenceEqual(packetPts.Order()), "audio packet PTS must remain monotonic");
        Assert.Contains(packetPts, pts => pts >= 48_000);
    }

    [Fact]
    public void Validate_RejectsUnsupportedEncoderSampleRate_AndInvalidScalarRanges()
    {
        if (FfmpegEncodeMaps.AudioEncoderAvailable(EncodeAudioCodec.Opus))
        {
            var supported = FfmpegEncodeMaps.AudioEncoderSampleRates(EncodeAudioCodec.Opus);
            var unsupported = new[] { 44_100, 22_050, 11_025, 96_000 }
                .FirstOrDefault(rate => !supported.Contains(rate));
            if (supported.Count > 0 && unsupported > 0)
            {
                var opus = new EncodeSessionOptions
                {
                    Container = EncodeContainer.Matroska,
                    OutputMode = EncodeOutputMode.AudioOnly,
                    AudioLegs = [new AudioLegOptions { Codec = EncodeAudioCodec.Opus, SampleRate = unsupported }],
                };
                Assert.Contains(opus.Validate(), error => error.Contains("does not support"));
            }
        }

        var invalid = new EncodeSessionOptions
        {
            Video = new VideoEncodeOptions
            {
                BitrateBps = -1,
                BitrateMode = EncodeVideoBitrateMode.Constant,
                BufferSizeBits = -1,
                GopSize = -1,
                MaxBFrames = 17,
                Fps = 241,
            },
            AudioLegs = [new AudioLegOptions { BitrateBps = -1, SampleRate = 7_999 }],
        };
        var errors = invalid.Validate(probeEncoders: false);
        Assert.Contains(errors, error => error.Contains("Video bitrate"));
        Assert.Contains(errors, error => error.Contains("Constant video bitrate"));
        Assert.Contains(errors, error => error.Contains("VBV"));
        Assert.Contains(errors, error => error.Contains("GOP"));
        Assert.Contains(errors, error => error.Contains("B-frames"));
        Assert.Contains(errors, error => error.Contains("frame rate"));
        Assert.Contains(errors, error => error.Contains("Audio track 1: bitrate"));
        Assert.Contains(errors, error => error.Contains("sample rate"));
    }

    [Fact]
    public async Task MetricsPolling_IsSafeAcrossFinishAndDispose()
    {
        var outPath = TempPath(".mka");
        var options = new EncodeSessionOptions
        {
            Container = EncodeContainer.Matroska,
            OutputMode = EncodeOutputMode.AudioOnly,
            AudioLegs = [new AudioLegOptions { Codec = EncodeAudioCodec.Flac, Channels = 2 }],
        };
        if (options.Validate().Count > 0)
            return;

        var session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(outPath));
        PumpAudio(session.AudioSinks[0], MakeSine(48_000, 2, 0.25), channels: 2);
        using var stop = new CancellationTokenSource();
        var poll = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
                _ = session.GetMetrics().Sinks.Sum(sink => sink.BytesWritten);
        });

        await FinishAsync(session);
        session.Dispose();
        await Task.Delay(20);
        stop.Cancel();
        await poll;
    }

    [Fact]
    public void CreateWithSinks_OwnsAndRollsBackSinksWhenValidationFails()
    {
        var sink = new RecordingPacketSink();
        var options = new EncodeSessionOptions
        {
            Video = new VideoEncodeOptions { BitrateBps = -1 },
        };

        Assert.Throws<ArgumentException>(() => FFmpegEncodeSession.CreateWithSinks(options, [sink]));
        Assert.True(sink.Disposed);
    }

    [Fact]
    public unsafe void ReconnectingSink_RecoversAfterWriteFailureWithoutDetaching()
    {
        var created = new List<ScriptedPacketSink>();
        var reconnecting = new Sinks.ReconnectingPacketSink(
            "srt://example.test:9000?streamid=…",
            () =>
            {
                var sink = new ScriptedPacketSink(failOnFirstPacket: created.Count == 0);
                created.Add(sink);
                return sink;
            },
            minimumRetryDelay: TimeSpan.Zero,
            maximumRetryDelay: TimeSpan.Zero);

        reconnecting.OnStreamsReady([]); // first connection succeeds
        Assert.True(reconnecting.Healthy);

        reconnecting.OnPacket(null, keyframe: false); // first destination write drops
        Assert.False(reconnecting.Healthy);
        Assert.Contains("simulated", reconnecting.Error);
        Assert.True(created[0].Disposed);

        reconnecting.OnPacket(null, keyframe: false); // audio-only seam may reconnect immediately
        Assert.True(reconnecting.Healthy);
        Assert.Null(reconnecting.Error);
        Assert.Equal(2, created.Count);
        Assert.Equal(1, created[1].Packets);

        reconnecting.Finish();
        reconnecting.Dispose();
        Assert.True(created[1].Finished);
        Assert.True(created[1].Disposed);
    }

    private sealed unsafe class RecordingPacketSink : Sinks.IEncodedPacketSink
    {
        public bool Disposed { get; private set; }
        public int VideoFrameRateNumerator { get; private set; }
        public int VideoFrameRateDenominator { get; private set; }
        public List<long> VideoPacketDurations { get; } = [];
        public string Name => "test";
        public long BytesWritten => 0;
        public void OnStreamsReady(IReadOnlyList<Sinks.EncodedStreamInfo> streams)
        {
            var video = streams.FirstOrDefault(stream => stream.Kind == Sinks.EncodedStreamKind.Video);
            if (video is null)
                return;
            VideoFrameRateNumerator = video.FrameRate.num;
            VideoFrameRateDenominator = video.FrameRate.den;
        }

        public void OnPacket(global::FFmpeg.AutoGen.AVPacket* packet, bool keyframe)
        {
            if (packet is not null && packet->stream_index == 0)
                VideoPacketDurations.Add(packet->duration);
        }
        public void Finish() { }
        public void Dispose() => Disposed = true;
    }

    private sealed unsafe class ScriptedPacketSink(bool failOnFirstPacket) : Sinks.IEncodedPacketSink
    {
        public bool Disposed { get; private set; }
        public bool Finished { get; private set; }
        public int Packets { get; private set; }
        public string Name => "scripted";
        public long BytesWritten => Packets * 188L;
        public void OnStreamsReady(IReadOnlyList<Sinks.EncodedStreamInfo> streams) { }

        public void OnPacket(global::FFmpeg.AutoGen.AVPacket* packet, bool keyframe)
        {
            Packets++;
            if (failOnFirstPacket && Packets == 1)
                throw new IOException("simulated network write failure");
        }

        public void Finish() => Finished = true;
        public void Dispose() => Disposed = true;
    }
}

/// <summary>Review P3-2: repeated generated-frame stepping must not lose fractional 90 kHz ticks.</summary>
public sealed class FixedRateStepAccumulationTests
{
    [Theory]
    [InlineData(30_000, 1001)]  // 29.97: exact 3003-tick steps
    [InlineData(60_000, 1001)]  // 59.94: 1501.5 ticks - the truncation-drift case
    [InlineData(24_000, 1001)]  // 23.976
    [InlineData(48, 1)]         // exact divisor control
    [InlineData(17, 3)]         // no exact 90 kHz divisor at all
    public void StepSum_MatchesExactRationalTimeline(int rateNum, int rateDen)
    {
        long remainder = 0, total = 0;
        const int frames = 600_000; // hours of stepping - integer-truncation drift would be huge here
        for (var i = 0; i < frames; i++)
            total += FFmpegEncodeSession.AccumulateStep90k(rateNum, rateDen, ref remainder);

        var exact = 90_000L * rateDen * frames / rateNum;
        Assert.InRange(total, exact - 1, exact + 1);
    }

    [Fact]
    public void Fps5994_DoesNotDriftHalfATickPerFrame()
    {
        long remainder = 0, total = 0;
        for (var i = 0; i < 2; i++)
            total += FFmpegEncodeSession.AccumulateStep90k(60_000, 1001, ref remainder);
        Assert.Equal(3003, total); // 1501 + 1502, not 1501 + 1501
    }
}
