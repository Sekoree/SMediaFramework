using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using Xunit;
using Xunit.Abstractions;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Seek behaviour on realistic media with a non-trivial GOP (keyframe every ~2 s), which the
/// frame-accurate <c>-g 1</c> fixtures elsewhere never exercise. Bluray rips have large GOPs, so a
/// keyframe seek that leaves audio at the keyframe while video is advanced to the target shows up as
/// seconds of A/V desync.
/// </summary>
public class SharedDemuxSeekAlignmentTests
{
    private readonly ITestOutputHelper _output;

    public SharedDemuxSeekAlignmentTests(ITestOutputHelper output)
    {
        _output = output;
        FFmpegRuntime.EnsureInitialized();
    }

    [Fact]
    public void ProvidedMovie_DeepSeekProbe_WhenPresent()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MFPLAYER_RUN_PROVIDED_MOVIE_PROBE"), "1", StringComparison.Ordinal))
            return;

        var path = Environment.GetEnvironmentVariable("MFPLAYER_PROBE_MEDIA")
                   ?? "/home/sekoree/Videos/THE IDOLM@STER MOVIE.mkv";
        if (!File.Exists(path))
            return;

        var target = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(50);
        File.WriteAllText(ProbeLogPath, $"probe start {DateTimeOffset.Now:O}{Environment.NewLine}");
        ProbeDecoderCase(path, target, "native-hw", tryHardware: true, fileReadBufferBytes: 0);
        ProbeDecoderCase(path, target, "bigbuf-hw", tryHardware: true, fileReadBufferBytes: 4 * 1024 * 1024);
        ProbeDecoderCase(path, target, "bigbuf-sw", tryHardware: false, fileReadBufferBytes: 4 * 1024 * 1024);
    }

    private void ProbeDecoderCase(
        string path,
        TimeSpan target,
        string label,
        bool tryHardware,
        int fileReadBufferBytes)
    {
        ProbeLog($"{label}: open begin hw={tryHardware} fileReadBuffer={fileReadBufferBytes}");
        using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions
        {
            TryHardwareAcceleration = tryHardware,
            FileReadBufferBytes = fileReadBufferBytes,
            AudioPacketQueueDepth = 720,
            VideoPacketQueueDepth = 512,
        });

        _output.WriteLine(
            $"{label}: duration={c.Duration} shared={c.UsesSharedDemux} audio={c.Audio.Format} video={c.Video.Format}");
        ProbeLog($"{label}: open done duration={c.Duration} audio={c.Audio.Format} video={c.Video.Format}");

        ProbeLog($"{label}: seek begin target={target}");
        var seek = Stopwatch.StartNew();
        c.SeekPresentation(target);
        seek.Stop();
        _output.WriteLine($"{label}: seekElapsedMs={seek.Elapsed.TotalMilliseconds:F1}");
        ProbeLog($"{label}: seek done ms={seek.Elapsed.TotalMilliseconds:F1}");

        ProbeLog($"{label}: first video read begin");
        var vRead = Stopwatch.StartNew();
        Assert.True(c.Video.TryReadNextFrame(out var vf));
        vRead.Stop();
        var videoPts = vf.PresentationTime;
        vf.Dispose();
        _output.WriteLine(
            $"{label}: firstVideo={videoPts} deltaMs={(videoPts - target).TotalMilliseconds:F1} readMs={vRead.Elapsed.TotalMilliseconds:F1}");
        ProbeLog($"{label}: first video done pts={videoPts} deltaMs={(videoPts - target).TotalMilliseconds:F1} ms={vRead.Elapsed.TotalMilliseconds:F1}");

        ProbeLog($"{label}: first audio read begin");
        var aRead = Stopwatch.StartNew();
        Assert.True(c.Audio.TryReadNextFrame(out var af));
        aRead.Stop();
        var audioPts = af.PresentationTime;
        af.Dispose();
        _output.WriteLine(
            $"{label}: firstAudio={audioPts} deltaMs={(audioPts - target).TotalMilliseconds:F1} readMs={aRead.Elapsed.TotalMilliseconds:F1}");
        _output.WriteLine($"{label}: avDeltaMs={(videoPts - audioPts).TotalMilliseconds:F1}");
        ProbeLog($"{label}: first audio done pts={audioPts} deltaMs={(audioPts - target).TotalMilliseconds:F1} ms={aRead.Elapsed.TotalMilliseconds:F1} avDeltaMs={(videoPts - audioPts).TotalMilliseconds:F1}");

        ProbeLog($"{label}: decode burst begin");
        var decode = Stopwatch.StartNew();
        var frames = 0;
        var audioFrames = 0;
        var lateBackstep = 0;
        var lastPts = videoPts;
        for (; frames < 240; frames++)
        {
            if (!c.Video.TryReadNextFrame(out var frame))
                break;
            if (frame.PresentationTime < lastPts)
                lateBackstep++;
            lastPts = frame.PresentationTime;
            frame.Dispose();

            for (var a = 0; a < 8 && c.Audio.TryReadNextFrame(out var audioFrame); a++)
            {
                audioFrames++;
                var audioEnd = audioFrame.PresentationTime
                               + TimeSpan.FromSeconds(audioFrame.SamplesPerChannel / (double)c.Audio.Format.SampleRate);
                audioFrame.Dispose();
                if (audioEnd >= lastPts)
                    break;
            }
        }

        decode.Stop();
        _output.WriteLine(
            $"{label}: decodedNext={frames} audioFrames={audioFrames} elapsedMs={decode.Elapsed.TotalMilliseconds:F1} effectiveFps={(frames / Math.Max(0.001, decode.Elapsed.TotalSeconds)):F1} lastPts={lastPts} backsteps={lateBackstep}");
        ProbeLog($"{label}: decode burst done frames={frames} audioFrames={audioFrames} ms={decode.Elapsed.TotalMilliseconds:F1} fps={(frames / Math.Max(0.001, decode.Elapsed.TotalSeconds)):F1} lastPts={lastPts} backsteps={lateBackstep}");
    }

    private const string ProbeLogPath = "/tmp/mf_seek_probe.log";

    private static void ProbeLog(string line) =>
        File.AppendAllText(ProbeLogPath, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}");

    [Fact]
    public void SeekPresentation_MidGop_AudioAndVideoLandTogetherAtTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc_biggop_align_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 7, fps: 24, keyint: 48)) return; // 2 s GOP
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            Assert.True(c.UsesSharedDemux);

            // 3.0 s sits inside the GOP that starts at the 2.0 s keyframe.
            var target = TimeSpan.FromSeconds(3.0);
            c.SeekPresentation(target);

            Assert.True(c.Video.TryReadNextFrame(out var vf));
            var videoPts = vf.PresentationTime;
            vf.Dispose();

            Assert.True(c.Audio.TryReadNextFrame(out var af));
            var audioPts = af.PresentationTime;
            af.Dispose();

            // Video is advanced to the target by ConsumeDecoderUntilPts; audio must be advanced too,
            // not left at the 2.0 s keyframe. Before the fix audioPts ~= 2.0 s (a full ~1 s behind).
            Assert.InRange(videoPts.TotalSeconds, 2.95, 3.30);
            Assert.InRange(audioPts.TotalSeconds, 2.85, 3.30);
            Assert.True(Math.Abs((audioPts - videoPts).TotalSeconds) < 0.20,
                $"audio/video desync after seek: audio={audioPts.TotalSeconds:F3}s video={videoPts.TotalSeconds:F3}s");

            // Clock should track the requested target, not a pre-target GOP keyframe left in Video.Position.
            var aligned = c.GetAlignedPresentationPosition(target);
            Assert.InRange(aligned.TotalSeconds, target.TotalSeconds - 0.15, target.TotalSeconds + 0.15);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    [Fact]
    public void DoubleSeek_SameTarget_NoReadsBetween_StaysAligned()
    {
        // A coordinated A/V seek calls SeekPresentation twice on the shared demux (audio track + video
        // track) at the same target with no reads in between. The second call is deduplicated; the primed
        // state from the first must remain correct.
        var path = Path.Combine(Path.GetTempPath(), $"mc_biggop_dedup_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 7, fps: 24, keyint: 48)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            var target = TimeSpan.FromSeconds(3.0);
            c.SeekPresentation(target);
            c.SeekPresentation(target); // dedup: must be a safe no-op

            Assert.True(c.Video.TryReadNextFrame(out var vf));
            var videoPts = vf.PresentationTime;
            vf.Dispose();
            Assert.True(c.Audio.TryReadNextFrame(out var af));
            var audioPts = af.PresentationTime;
            af.Dispose();

            Assert.InRange(videoPts.TotalSeconds, 2.95, 3.30);
            Assert.InRange(audioPts.TotalSeconds, 2.85, 3.30);
            Assert.True(Math.Abs((audioPts - videoPts).TotalSeconds) < 0.20,
                $"audio/video desync after double seek: audio={audioPts.TotalSeconds:F3}s video={videoPts.TotalSeconds:F3}s");
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    [Fact]
    public void ReadInto_AfterSeek_IsBounded_AndCatchesUpToTarget()
    {
        // The post-seek audio catch-up runs inside ReadInto on the AudioRouter run-loop thread. It must stay
        // bounded (the router can only stop a run loop that is between reads or in a quick one) yet still
        // land audio at the requested target. Regression for the terminal "AudioRouter run loop thread did
        // not exit within the join cap" seen when the catch-up blocked waiting on a stalled demux.
        var path = Path.Combine(Path.GetTempPath(), $"mc_biggop_bounded_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 7, fps: 24, keyint: 48)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            c.SeekPresentation(TimeSpan.FromSeconds(3.0));

            // Drain audio in discrete reads (as the router run loop does) without draining video. Each read
            // must stay bounded; while catching up TryReadNextFrame returns false promptly rather than
            // blocking, and once the target is reached it yields the keeper frame at ~3 s.
            var got = false;
            var audioPts = TimeSpan.Zero;
            var overall = Stopwatch.StartNew();
            for (var reads = 0; !got && reads < 500 && overall.Elapsed < TimeSpan.FromSeconds(5); reads++)
            {
                var call = Stopwatch.StartNew();
                got = c.Audio.TryReadNextFrame(out var af);
                Assert.True(call.Elapsed < TimeSpan.FromSeconds(1.5),
                    $"audio read blocked for {call.Elapsed.TotalSeconds:F2}s — would trip the router stop cap");
                if (got)
                {
                    audioPts = af.PresentationTime;
                    af.Dispose();
                }
            }

            Assert.True(got, "audio never caught up to the seek target");
            Assert.InRange(audioPts.TotalSeconds, 2.85, 3.30);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    [Fact]
    public void DeepSeekIntoLargeGop_CompletesWithoutHanging_AndStaysAligned()
    {
        // Seeking deep into a long GOP requires draining more than a queue's worth of the OTHER stream while
        // catching up. The old video-only catch-up let the audio queue fill, blocked the single demux thread,
        // starved the video catch-up, and hung SeekPresentation while it held the read/seek write gate (the
        // exact "locked for 5 seconds" the player showed). The interleaved catch-up drains both, so the seek
        // completes; a deadline bounds it regardless.
        var path = Path.Combine(Path.GetTempPath(), $"mc_deepgop_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 14, fps: 24, keyint: 240)) return; // 10 s GOP
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });

            // 9 s sits 9 s into the GOP that starts at the 0 s keyframe — far past a single audio queue.
            var sw = Stopwatch.StartNew();
            c.SeekPresentation(TimeSpan.FromSeconds(9.0));
            sw.Stop();
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"SeekPresentation hung for {sw.Elapsed.TotalSeconds:F1}s");

            Assert.True(c.Video.TryReadNextFrame(out var vf));
            var videoPts = vf.PresentationTime;
            vf.Dispose();
            Assert.True(c.Audio.TryReadNextFrame(out var af));
            var audioPts = af.PresentationTime;
            af.Dispose();

            Assert.InRange(videoPts.TotalSeconds, 8.9, 9.4);
            Assert.InRange(audioPts.TotalSeconds, 8.8, 9.4);
            Assert.True(Math.Abs((audioPts - videoPts).TotalSeconds) < 0.20,
                $"audio/video desync after deep seek: audio={audioPts.TotalSeconds:F3}s video={videoPts.TotalSeconds:F3}s");
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    [Fact]
    public void ConcurrentSeeks_OnLargeGop_DoNotTripIterationGuard()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mc_biggop_race_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 7, fps: 24, keyint: 48)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });

            // Two threads hammering SeekPresentation. A second seek's read-yield request used to make the
            // first seek's ConsumeDecoderUntilPts spin to its 2M iteration guard and throw.
            Exception? failure = null;
            var stop = Stopwatch.StartNew();
            void Seeker(int salt)
            {
                try
                {
                    var rng = new Random(salt);
                    while (stop.Elapsed < TimeSpan.FromSeconds(4) && failure is null)
                        c.SeekPresentation(TimeSpan.FromSeconds(1.0 + rng.NextDouble() * 5.0));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            var t1 = new Thread(() => Seeker(1)) { IsBackground = true };
            var t2 = new Thread(() => Seeker(2)) { IsBackground = true };
            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();

            Assert.Null(failure);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    [Fact]
    public void ReseekSameTarget_AfterReadsConsumePrimedState_RerunsAndStaysAligned()
    {
        // The coordinated-seek dedup is keyed on a "primed, not yet consumed" flag. Once a read pulls past
        // the primed state, a later seek to the SAME target must run in full (not dedup to the now-advanced
        // demux) and land back on the target.
        var path = Path.Combine(Path.GetTempPath(), $"mc_biggop_reseek_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 7, fps: 24, keyint: 48)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });
            var target = TimeSpan.FromSeconds(3.0);

            c.SeekPresentation(target);
            Assert.True(c.Video.TryReadNextFrame(out var vf0));
            vf0.Dispose();
            Assert.True(c.Audio.TryReadNextFrame(out var af0));
            af0.Dispose();

            // Reads above advanced the demux past the target and cleared the primed flag, so this must reseek.
            c.SeekPresentation(target);
            Assert.True(c.Video.TryReadNextFrame(out var vf));
            var videoPts = vf.PresentationTime;
            vf.Dispose();
            Assert.True(c.Audio.TryReadNextFrame(out var af));
            var audioPts = af.PresentationTime;
            af.Dispose();

            Assert.InRange(videoPts.TotalSeconds, 2.95, 3.30);
            Assert.InRange(audioPts.TotalSeconds, 2.85, 3.30);
            Assert.True(Math.Abs((audioPts - videoPts).TotalSeconds) < 0.20,
                $"audio/video desync after reseek: audio={audioPts.TotalSeconds:F3}s video={videoPts.TotalSeconds:F3}s");
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    [Fact]
    public void LargeBufferFileOpen_DecodesAndSeeks_LikeNativeOpen()
    {
        // FileReadBufferBytes routes the open through a large-buffer custom AVIO over a FileStream instead
        // of FFmpeg's native file protocol. It must behave identically: decode frames and seek accurately.
        var path = Path.Combine(Path.GetTempPath(), $"mc_bigbuf_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 7, fps: 24, keyint: 48)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions
            {
                TryHardwareAcceleration = false,
                FileReadBufferBytes = 4 * 1024 * 1024,
            });
            Assert.True(c.UsesSharedDemux);
            Assert.True(c.Duration > TimeSpan.FromSeconds(6));

            // Decode from the start.
            Assert.True(c.Video.TryReadNextFrame(out var first));
            first.Dispose();

            // Seek mid-GOP and confirm both tracks land on target through the big-buffer I/O path.
            var target = TimeSpan.FromSeconds(3.0);
            c.SeekPresentation(target);
            Assert.True(c.Video.TryReadNextFrame(out var vf));
            var videoPts = vf.PresentationTime;
            vf.Dispose();
            Assert.True(c.Audio.TryReadNextFrame(out var af));
            var audioPts = af.PresentationTime;
            af.Dispose();

            Assert.InRange(videoPts.TotalSeconds, 2.95, 3.30);
            Assert.InRange(audioPts.TotalSeconds, 2.85, 3.30);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    [Fact]
    public void CancelInFlightSeek_WhenIdle_IsNoOp_AndNextSeekStillLandsAtTarget()
    {
        // A cancel that arrives with no seek running (or after one finishes) must not wedge the demux: the
        // cancel flag is reset at the start of the next real seek and is never read by the decode path.
        var path = Path.Combine(Path.GetTempPath(), $"mc_biggop_cancel_{Guid.NewGuid():N}.mkv");
        if (!TryGenerateLargeGopAudioVideo(path, durationSec: 7, fps: 24, keyint: 48)) return;
        try
        {
            using var c = MediaContainerDecoder.Open(path, new VideoDecoderOpenOptions { TryHardwareAcceleration = false });

            c.CancelInFlightSeek(); // stray cancel, nothing running

            var target = TimeSpan.FromSeconds(3.0);
            c.SeekPresentation(target);

            Assert.True(c.Video.TryReadNextFrame(out var vf));
            var videoPts = vf.PresentationTime;
            vf.Dispose();
            Assert.True(c.Audio.TryReadNextFrame(out var af));
            var audioPts = af.PresentationTime;
            af.Dispose();

            Assert.InRange(videoPts.TotalSeconds, 2.95, 3.30);
            Assert.InRange(audioPts.TotalSeconds, 2.85, 3.30);
        }
        finally
        {
            MediaDiagnostics.SwallowDisposeErrors(() => File.Delete(path), "SharedDemuxSeekAlignmentTests: temp media delete");
        }
    }

    private static bool TryGenerateLargeGopAudioVideo(string path, int durationSec, int fps, int keyint)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-y",
                    "-f", "lavfi", "-i", $"testsrc=size=320x240:rate={fps}:duration={durationSec}",
                    "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={durationSec}",
                    "-shortest",
                    "-c:a", "aac",
                    "-c:v", "libx264",
                    "-g", keyint.ToString(),
                    "-keyint_min", keyint.ToString(),
                    "-sc_threshold", "0",
                    "-pix_fmt", "yuv420p",
                    "-loglevel", "error",
                    path,
                },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit(30000);
            return p.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
