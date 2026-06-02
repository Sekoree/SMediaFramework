using System.Diagnostics;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Encode;
using S.Media.FFmpeg.Encode.Internal;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Encode.Tests;

public sealed class FFmpegMuxRoundTripTests : IDisposable
{
    private readonly string? _sourcePath;
    private readonly List<string> _tempPaths = [];

    public FFmpegMuxRoundTripTests()
    {
        FFmpegRuntime.EnsureInitialized();
        var path = Path.Combine(Path.GetTempPath(), $"mf_enc_src_{Guid.NewGuid():N}.mp4");
        _sourcePath = TryGenerateVideo(path, 160, 120, 10, 1) ? path : null;
        if (_sourcePath is not null)
            _tempPaths.Add(_sourcePath);
    }

    public void Dispose()
    {
        foreach (var p in _tempPaths)
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(p), $"{nameof(FFmpegMuxRoundTripTests)}: delete temp");
    }

    [Fact]
    public void Mux_round_trip_writes_playable_mp4()
    {
        if (_sourcePath is null)
            return;

        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_out_{Guid.NewGuid():N}.mp4");
        _tempPaths.Add(outPath);

        using var dec = MediaContainerDecoder.Open(_sourcePath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        var count = 0;
        {
            using var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
            {
                IncludeAudio = false,
                Video = new FFmpegVideoFileOutputOptions { Codec = FFmpegVideoCodec.H264, GopSize = 6 },
            });

            mux.Video!.Configure(dec.Video.Format);
            while (dec.Video.TryReadNextFrame(out var f))
            {
                mux.Video.Submit(f);
                count++;
            }
        }

        Assert.True(count > 0);
        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 256);

        using var verify = VideoFileDecoder.Open(outPath, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
        Assert.True(verify.TryReadNextFrame(out var frame));
        frame.Dispose();
    }

    [Fact]
    public void Video_submit_before_configure_disposes_input_frame()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_unconfigured_{Guid.NewGuid():N}.mp4");
        _tempPaths.Add(outPath);
        var released = 0;
        using var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
        {
            IncludeAudio = false,
            IncludeVideo = true,
            Video = new FFmpegVideoFileOutputOptions { Codec = FFmpegVideoCodec.H264 },
        });

        Assert.Throws<InvalidOperationException>(() =>
            mux.Video!.Submit(MakeBgraFrame(16, 16, DisposableRelease.Wrap(() => Interlocked.Increment(ref released)))));

        Assert.Equal(1, Volatile.Read(ref released));
    }

    [Fact]
    public void Video_configured_submit_disposes_input_frame_once()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_submit_{Guid.NewGuid():N}.mp4");
        _tempPaths.Add(outPath);
        var released = 0;
        using var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
        {
            IncludeAudio = false,
            IncludeVideo = true,
            Video = new FFmpegVideoFileOutputOptions { Codec = FFmpegVideoCodec.H264 },
        });
        var format = new VideoFormat(16, 16, PixelFormat.I420, new Rational(30, 1));
        mux.Video!.Configure(format);

        mux.Video.Submit(MakeI420Frame(16, 16, DisposableRelease.Wrap(() => Interlocked.Increment(ref released))));

        Assert.Equal(1, Volatile.Read(ref released));
    }

    [Fact]
    public void Video_conversion_path_disposes_converted_frame_once()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_convert_{Guid.NewGuid():N}.mp4");
        _tempPaths.Add(outPath);
        var originalReleased = 0;
        var convertedReleased = 0;
        try
        {
            FfmpegVideoEncoder.ConverterFactoryForTests = () => new TrackingConverter(
                () => Interlocked.Increment(ref convertedReleased));
            using var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
            {
                IncludeAudio = false,
                IncludeVideo = true,
                Video = new FFmpegVideoFileOutputOptions { Codec = FFmpegVideoCodec.H264 },
            });
            mux.Video!.Configure(new VideoFormat(16, 16, PixelFormat.Bgra32, new Rational(30, 1)));

            mux.Video.Submit(MakeBgraFrame(16, 16, DisposableRelease.Wrap(() => Interlocked.Increment(ref originalReleased))));

            Assert.Equal(1, Volatile.Read(ref originalReleased));
            Assert.Equal(1, Volatile.Read(ref convertedReleased));
        }
        finally
        {
            FfmpegVideoEncoder.ConverterFactoryForTests = null;
        }
    }

    [Fact]
    public void Audio_flac_round_trip_preserves_signal()
    {
        // Exercises the audio encoder's swresample path for a NON-float codec (FLAC = s16). The old
        // path copied raw float bytes into the frame, which the codec interpreted as integers — the
        // decoded signal was garbage. FLAC is lossless, so the decoded RMS must track the input sine.
        const int sampleRate = 48_000;
        const int channels = 2;
        const int totalFrames = sampleRate / 2; // 0.5 s
        const float amp = 0.25f;
        var interleaved = new float[totalFrames * channels];
        for (var i = 0; i < totalFrames; i++)
        {
            var s = amp * MathF.Sin(2f * MathF.PI * 440f * i / sampleRate);
            for (var ch = 0; ch < channels; ch++)
                interleaved[i * channels + ch] = s;
        }

        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_aud_{Guid.NewGuid():N}.mka");
        _tempPaths.Add(outPath);

        var format = new AudioFormat(sampleRate, channels);
        const int chunkFrames = 1024;
        using (var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
        {
            Container = FFmpegEncodeContainer.Matroska,
            IncludeVideo = false,
            IncludeAudio = true,
            Audio = new FFmpegAudioFileOutputOptions { Codec = FFmpegAudioCodec.Flac },
        }))
        {
            mux.Audio!.Configure(format);
            for (var off = 0; off < totalFrames; off += chunkFrames)
            {
                var n = Math.Min(chunkFrames, totalFrames - off);
                mux.Audio.Submit(interleaved.AsSpan(off * channels, n * channels));
            }
        }

        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 256);

        // Decode back: lossless FLAC means the decoded RMS must match the input sine's RMS. A broken
        // sample-format path would yield silence or noise far from amp/√2.
        using var dec = AudioFileDecoder.Open(outPath);
        var buf = new float[chunkFrames * channels];
        double sumSq = 0;
        long count = 0;
        int read;
        while ((read = dec.ReadInto(buf)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                sumSq += buf[i] * (double)buf[i];
                count++;
            }
        }

        Assert.True(count > 20_000, $"expected a meaningful amount of decoded audio, got {count} samples");
        var rms = Math.Sqrt(sumSq / count);
        var expected = amp / Math.Sqrt(2); // ≈ 0.1768
        Assert.InRange(rms, expected * 0.6, expected * 1.4);
    }

    [Fact]
    public void Audio_submit_rejects_misaligned_interleaved_samples()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_misaligned_{Guid.NewGuid():N}.mka");
        _tempPaths.Add(outPath);

        using var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
        {
            Container = FFmpegEncodeContainer.Matroska,
            IncludeVideo = false,
            IncludeAudio = true,
            Audio = new FFmpegAudioFileOutputOptions { Codec = FFmpegAudioCodec.Flac },
        });
        mux.Audio!.Configure(new AudioFormat(48_000, 2));

        var ex = Assert.Throws<ArgumentException>(() => mux.Audio.Submit([0f, 0f, 0f]));

        Assert.Contains("multiple of channel count", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Audio_channel_aligned_partial_chunks_buffer_until_flush()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"mf_enc_partial_{Guid.NewGuid():N}.mka");
        _tempPaths.Add(outPath);
        const int sampleRate = 48_000;
        const int channels = 2;
        const int totalFrames = 1500;
        const int chunkFrames = 17;
        var format = new AudioFormat(sampleRate, channels);

        using (var mux = FFmpegMuxFileOutput.Open(outPath, new FFmpegMuxFileOutputOptions
        {
            Container = FFmpegEncodeContainer.Matroska,
            IncludeVideo = false,
            IncludeAudio = true,
            Audio = new FFmpegAudioFileOutputOptions { Codec = FFmpegAudioCodec.Flac },
        }))
        {
            mux.Audio!.Configure(format);
            var chunk = new float[chunkFrames * channels];
            for (var off = 0; off < totalFrames; off += chunkFrames)
            {
                var frames = Math.Min(chunkFrames, totalFrames - off);
                var samples = frames * channels;
                Array.Fill(chunk, 0.125f, 0, samples);
                mux.Audio.Submit(chunk.AsSpan(0, samples));
            }
        }

        Assert.True(File.Exists(outPath));
        Assert.True(new FileInfo(outPath).Length > 256);

        using var dec = AudioFileDecoder.Open(outPath);
        var buffer = new float[chunkFrames * channels];
        var decodedSamples = 0;
        int read;
        while ((read = dec.ReadInto(buffer)) > 0)
            decodedSamples += read;

        Assert.True(decodedSamples >= totalFrames * channels,
            $"expected at least submitted sample count after flush padding, got {decodedSamples}");
    }

    private static bool TryGenerateVideo(string path, int width, int height, int fps, int durationSec)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y", "-f", "lavfi",
                    "-i", $"testsrc=size={width}x{height}:rate={fps}:duration={durationSec}",
                    "-pix_fmt", "yuv420p", "-c:v", "libx264", "-loglevel", "error", path,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static VideoFrame MakeBgraFrame(int width, int height, IDisposable? release = null)
    {
        var format = new VideoFormat(width, height, PixelFormat.Bgra32, new Rational(30, 1));
        return new VideoFrame(TimeSpan.Zero, format, new byte[width * height * 4], width * 4, release: release);
    }

    private static VideoFrame MakeI420Frame(int width, int height, IDisposable? release = null)
    {
        var format = new VideoFormat(width, height, PixelFormat.I420, new Rational(30, 1));
        var chromaWidth = PixelFormatInfo.ChromaWidth420(width);
        var chromaHeight = PixelFormatInfo.ChromaHeight420(height);
        return new VideoFrame(
            TimeSpan.Zero,
            format,
            planes:
            [
                new byte[width * height],
                new byte[chromaWidth * chromaHeight],
                new byte[chromaWidth * chromaHeight],
            ],
            strides: [width, chromaWidth, chromaWidth],
            release: release);
    }

    private static VideoFrame MakeNv12Frame(int width, int height, IDisposable? release = null)
    {
        var format = new VideoFormat(width, height, PixelFormat.Nv12, new Rational(30, 1));
        var chromaWidth = PixelFormatInfo.ChromaWidth420(width);
        var chromaHeight = PixelFormatInfo.ChromaHeight420(height);
        return new VideoFrame(
            TimeSpan.Zero,
            format,
            planes:
            [
                new byte[width * height],
                new byte[chromaWidth * chromaHeight * 2],
            ],
            strides: [width, chromaWidth * 2],
            release: release);
    }

    private sealed class TrackingConverter(Action onConvertedDisposed) : IVideoCpuFrameConverter
    {
        private PixelFormat _dst;
        private int _width;
        private int _height;

        public void Configure(PixelFormat src, PixelFormat dst, int width, int height)
        {
            _dst = dst;
            _width = width;
            _height = height;
        }

        public VideoFrame Convert(VideoFrame source, VideoTransferHint hint)
        {
            return _dst switch
            {
                PixelFormat.I420 => MakeI420Frame(_width, _height, DisposableRelease.Wrap(onConvertedDisposed)),
                PixelFormat.Nv12 => MakeNv12Frame(_width, _height, DisposableRelease.Wrap(onConvertedDisposed)),
                _ => throw new InvalidOperationException($"test converter does not support destination {_dst}."),
            };
        }

        public void Dispose() { }
    }
}
