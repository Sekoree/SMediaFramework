using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.FFmpeg;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests;

/// <summary>
/// Seek behaviour on realistic media with a non-trivial GOP (keyframe every ~2 s), which the
/// frame-accurate <c>-g 1</c> fixtures elsewhere never exercise. Bluray rips have large GOPs, so a
/// keyframe seek that leaves audio at the keyframe while video is advanced to the target shows up as
/// seconds of A/V desync.
/// </summary>
public class SharedDemuxSeekAlignmentTests
{
    public SharedDemuxSeekAlignmentTests() => FFmpegRuntime.EnsureInitialized();

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
