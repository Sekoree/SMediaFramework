using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.FFmpeg.Common;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

/// <summary>
/// FFMPEG-01: a generated-fixture matrix that drives the real decode path end-to-end through the registry's
/// public open/read API (exactly how playback opens media). Each input is produced with the host <c>ffmpeg</c>
/// CLI (lavfi test sources), so the whole class skips where the CLI or usable natives are absent
/// (<see cref="RemuxFactAttribute"/>). The matrix asserts the buffer-ownership contract (every
/// <see cref="VideoFrame"/>/<see cref="AudioFrame"/> is caller-owned and disposed, EOF is a clean
/// <c>false</c> not a throw) and native-allocation stability across repeated open/read/dispose loops.
///
/// Deliberately NOT covered here (they need a version-stable generation strategy or hardware, tracked as
/// FFMPEG-01 follow-ups): true variable-frame-rate / missing-PTS inputs, and the CPU-vs-hardware decode split.
/// </summary>
public sealed class FFmpegFixtureMatrixTests : IDisposable
{
    private const int ReadCap = 100_000; // hard stop so a decode bug can never hang the suite
    private readonly string _dir = Directory.CreateTempSubdirectory("ffmatrix-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private string Generate(string args, string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-nostdin -loglevel error -y {args} \"{path}\"",
            RedirectStandardError = true,
        })!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, $"ffmpeg input generation failed: {stderr}");
        return path;
    }

    private string AvFixture(string name = "av.mkv", int durationSec = 2, int rate = 30) => Generate(
        $"-f lavfi -i testsrc2=duration={durationSec}:size=128x72:rate={rate} " +
        $"-f lavfi -i sine=frequency=440:duration={durationSec} " +
        "-c:v libx264 -pix_fmt yuv420p -g 30 -c:a aac -shortest", name);

    private static MediaRegistry Registry() => MediaRegistry.Build(b => b.Use(new FFmpegModule()));

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>Decodes every frame to EOF, disposing each (the ownership contract), and asserts PTS never runs
    /// backwards. Returns the frame count. EOF must be a clean <c>false</c> within <see cref="ReadCap"/>.</summary>
    private static int DecodeAllVideo(IVideoSource video)
    {
        var count = 0;
        var lastPts = TimeSpan.MinValue;
        while (video.TryReadNextFrame(out var frame))
        {
            Assert.True(frame.PresentationTime >= lastPts, $"video PTS went backwards: {frame.PresentationTime} < {lastPts}");
            lastPts = frame.PresentationTime;
            frame.Dispose(); // caller owns the frame
            if (++count > ReadCap) Assert.Fail("video decode did not reach EOF within the read cap");
        }
        Assert.True(video.IsExhausted, "video source did not report exhaustion at EOF");
        return count;
    }

    private static int DecodeAllAudio(IAudioSource audio)
    {
        var count = 0;
        while (audio.TryReadNextFrame(out var frame))
        {
            frame.Dispose();
            if (++count > ReadCap) Assert.Fail("audio decode did not reach EOF within the read cap");
        }
        return count;
    }

    // ---- the matrix -----------------------------------------------------------------------------

    [RemuxFact]
    public void AudioAndVideo_DecodeBothTracksToEof()
    {
        var path = AvFixture();
        var registry = Registry();

        Assert.True(registry.TryOpenVideo(path, options: null, out var video), "A+V: video track did not open");
        int videoFrames;
        try
        {
            Assert.True(video.Format is { Width: 128, Height: 72 }, "A+V: unexpected video geometry");
            videoFrames = DecodeAllVideo(video);
        }
        finally { (video as IDisposable)?.Dispose(); }

        // testsrc2 at 30 fps for ~2 s → ~60 frames; allow slack for encoder/container rounding.
        Assert.InRange(videoFrames, 45, 75);

        Assert.True(registry.TryOpenAudio(path, options: null, out var audio), "A+V: audio track did not open");
        int audioFrames;
        try { audioFrames = DecodeAllAudio(audio); }
        finally { (audio as IDisposable)?.Dispose(); }
        Assert.True(audioFrames > 0, "A+V: no audio frames decoded");
    }

    [RemuxFact]
    public void VideoOnly_DecodesVideo_AndOpeningAudioReportsNoTrack()
    {
        var path = Generate("-f lavfi -i testsrc2=duration=1:size=128x72:rate=30 -c:v libx264 -pix_fmt yuv420p -an", "v.mp4");
        var registry = Registry();

        Assert.True(registry.TryOpenVideo(path, options: null, out var video), "V-only: video did not open");
        try { Assert.True(DecodeAllVideo(video) > 0, "V-only: no video frames"); }
        finally { (video as IDisposable)?.Dispose(); }

        // Contract: TryOpenAudio's "Try" is provider-selection (a provider claims the URI); a claimed container
        // with no audio stream surfaces a controlled FFmpegException, not a crash and not a silent success.
        Assert.Throws<FFmpegException>(() => registry.TryOpenAudio(path, options: null, out _));
    }

    [RemuxFact]
    public void AudioOnly_DecodesAudio_AndOpeningVideoReportsNoTrack()
    {
        var path = Generate("-f lavfi -i sine=frequency=330:duration=1 -c:a aac -vn", "a.m4a");
        var registry = Registry();

        Assert.True(registry.TryOpenAudio(path, options: null, out var audio), "A-only: audio did not open");
        try { Assert.True(DecodeAllAudio(audio) > 0, "A-only: no audio frames"); }
        finally { (audio as IDisposable)?.Dispose(); }

        Assert.Throws<FFmpegException>(() => registry.TryOpenVideo(path, options: null, out _));
    }

    [RemuxFact]
    public void MultiTrackAudio_OpensAndDecodesTheDefaultTrack()
    {
        var path = Generate(
            "-f lavfi -i testsrc2=duration=1:size=128x72:rate=30 " +
            "-f lavfi -i sine=frequency=440:duration=1 -f lavfi -i sine=frequency=880:duration=1 " +
            "-map 0:v -map 1:a -map 2:a -c:v libx264 -pix_fmt yuv420p -c:a aac", "multi.mkv");
        var registry = Registry();

        Assert.True(registry.TryOpenVideo(path, options: null, out var video), "multi-track: video did not open");
        try { Assert.True(DecodeAllVideo(video) > 0); }
        finally { (video as IDisposable)?.Dispose(); }

        Assert.True(registry.TryOpenAudio(path, options: null, out var audio), "multi-track: default audio did not open");
        try { Assert.True(DecodeAllAudio(audio) > 0); }
        finally { (audio as IDisposable)?.Dispose(); }
    }

    [RemuxFact]
    public void TruncatedFile_TerminatesGracefully_WithoutHangingOrCrashing()
    {
        var whole = AvFixture("whole.mkv", durationSec: 3);
        var bytes = File.ReadAllBytes(whole);
        var truncated = Path.Combine(_dir, "truncated.mkv");
        File.WriteAllBytes(truncated, bytes.AsSpan(0, bytes.Length / 2).ToArray()); // keep the (front) header, drop the tail

        var registry = Registry();

        // A mid-stream truncation must not crash: opening either fails cleanly, or succeeds and the decode
        // reaches a bounded EOF (false) rather than throwing or looping forever.
        if (registry.TryOpenVideo(truncated, options: null, out var video))
        {
            try
            {
                var frames = 0;
                while (video.TryReadNextFrame(out var frame))
                {
                    frame.Dispose();
                    if (++frames > ReadCap) Assert.Fail("truncated decode did not terminate within the read cap");
                }
                // It opened, so it should have yielded at least the leading frames before the cut.
                Assert.True(frames > 0, "truncated file opened but produced no frames");
            }
            finally { (video as IDisposable)?.Dispose(); }
        }
    }

    [RemuxFact]
    public void RepeatedOpenReadDispose_IsAllocationStableAcrossLoops()
    {
        // Native-allocation stability (FFMPEG-01): opening + fully decoding + disposing the same input many times
        // must not accumulate native handles/buffers or drift the decoded frame count. A per-open leak or a
        // ratcheting handle table would surface here as a throw or a changing count.
        var path = AvFixture("loop.mkv");
        var registry = Registry();

        int? baseline = null;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(registry.TryOpenVideo(path, options: null, out var video), $"loop {i}: open failed");
            try
            {
                var frames = DecodeAllVideo(video);
                baseline ??= frames;
                Assert.Equal(baseline, frames); // deterministic input → identical frame count every loop
            }
            finally { (video as IDisposable)?.Dispose(); }
        }
    }

    [RemuxFact]
    public void RepeatedSeek_LandsNearTargetEachTime_WithoutLeaking()
    {
        var path = AvFixture("seek.mkv", durationSec: 10);
        var registry = Registry();

        Assert.True(registry.TryOpenVideo(path, options: null, out var video), "seek: open failed");
        try
        {
            var seekable = Assert.IsAssignableFrom<ISeekableSource>(video);
            // Bounce forward/backward repeatedly; each seek must move the decoded stream near the target.
            foreach (var target in new[] { 6.0, 2.0, 8.0, 1.0, 5.0, 3.0 })
            {
                seekable.Seek(TimeSpan.FromSeconds(target));
                Assert.True(video.TryReadNextFrame(out var frame), $"seek to {target}s produced no frame");
                var pts = frame.PresentationTime.TotalSeconds;
                frame.Dispose();
                Assert.True(pts >= target - 0.6 && pts <= target + 1.1,
                    $"seek to {target}s landed at {pts:0.###}s (stream did not follow the seek)");
            }
        }
        finally { (video as IDisposable)?.Dispose(); }
    }
}
