using System.Diagnostics;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Core.Video;
using S.Media.Decode.FFmpeg;
using S.Media.FFmpeg.Common;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

/// <summary>
/// End-to-end gate for <see cref="FFmpegStreamCopyRemuxer"/> - the YouTube prepare path's
/// separate-streams → one-asset step. Inputs are generated with the host's <c>ffmpeg</c> CLI
/// (lavfi test sources), so the tests skip where the CLI or usable natives are absent (CI runners).
/// </summary>
public sealed class StreamCopyRemuxerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("remux-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private string Generate(string args, string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-nostdin -loglevel error -y {args} \"{path}\"",
            RedirectStandardError = true,
        });
        var stderr = p!.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, $"ffmpeg input generation failed: {stderr}");
        return path;
    }

    /// <summary>The "YouTube seek" regression: a remuxed local asset opened through the registry's
    /// OpenVideo path (exactly how the YouTube provider plays its cache) must surface
    /// <see cref="ISeekableSource"/> - <see cref="S.Media.Players.VideoPlayer"/> gates its seek on that
    /// interface, and a wrapper hiding it makes coordinated seeks move audio and the clock while video
    /// keeps decoding from the old position (backward = frozen frame, forward = fast-forward).</summary>
    [RemuxFact]
    public void RegistryVideoSource_IsSeekable_AndSeeksTheDecodedStream()
    {
        var av = Generate(
            "-f lavfi -i testsrc2=duration=10:size=192x108:rate=30 -f lavfi -i sine=frequency=440:duration=10 " +
            "-c:v libx264 -g 30 -pix_fmt yuv420p -c:a aac", "seek.mkv");

        var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));
        Assert.True(registry.TryOpenVideo(av, options: null, out var video));
        try
        {
            var seekable = Assert.IsAssignableFrom<ISeekableSource>(video);

            Assert.True(video.TryReadNextFrame(out var first));
            Assert.True(first.PresentationTime < TimeSpan.FromSeconds(1), $"unexpected first pts {first.PresentationTime}");
            first.Dispose();

            seekable.Seek(TimeSpan.FromSeconds(6));
            Assert.True(video.TryReadNextFrame(out var forward));
            Assert.True(forward.PresentationTime >= TimeSpan.FromSeconds(5.5) && forward.PresentationTime <= TimeSpan.FromSeconds(6.6),
                $"forward seek did not move the video stream (pts={forward.PresentationTime})");
            forward.Dispose();

            seekable.Seek(TimeSpan.FromSeconds(2));
            Assert.True(video.TryReadNextFrame(out var backward));
            Assert.True(backward.PresentationTime >= TimeSpan.FromSeconds(1.5) && backward.PresentationTime <= TimeSpan.FromSeconds(2.6),
                $"backward seek did not move the video stream (pts={backward.PresentationTime})");
            backward.Dispose();
        }
        finally
        {
            (video as IDisposable)?.Dispose();
        }
    }

    [RemuxFact]
    public void Remux_SeparateVideoAndAudio_ProducesOneAssetWithBothTracks()
    {
        // The YouTube shape: a video-only stream and an audio-only stream of the same content length.
        var video = Generate("-f lavfi -i testsrc2=duration=2:size=192x108:rate=30 -c:v libx264 -pix_fmt yuv420p -an", "v.mp4");
        var audio = Generate("-f lavfi -i sine=frequency=440:duration=2 -c:a aac -vn", "a.m4a");
        var output = Path.Combine(_dir, "muxed.partial.mkv");

        var lastProgress = 0d;
        FFmpegStreamCopyRemuxer.Remux(
            video, audio, output,
            progress: new Progress<double>(p => lastProgress = p));

        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 10_000, "remuxed asset suspiciously small");

        // The result must open through the NORMAL playback path with both tracks intact.
        var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));
        Assert.True(registry.TryOpenVideo(output, options: null, out var v), "video track lost in remux");
        try
        {
            Assert.True(v.Format.Width == 192 && v.Format.Height == 108, "video geometry lost in remux");
        }
        finally
        {
            (v as IDisposable)?.Dispose();
        }

        Assert.True(registry.TryOpenAudio(output, options: null, out var a), "audio track lost in remux");
        try
        {
            Assert.True(a.Format.SampleRate > 0);
        }
        finally
        {
            (a as IDisposable)?.Dispose();
        }
    }

    [RemuxFact]
    public void Remux_AudioOnly_PassesThrough()
    {
        var audio = Generate("-f lavfi -i sine=frequency=220:duration=1 -c:a aac -vn", "a.m4a");
        var output = Path.Combine(_dir, "audio.mkv");

        FFmpegStreamCopyRemuxer.Remux(videoPath: null, audioPath: audio, output);

        var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));
        Assert.True(registry.TryOpenAudio(output, options: null, out var a));
        try
        {
            Assert.True(a.Format.SampleRate > 0);
        }
        finally
        {
            (a as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Remux_NoInputs_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            FFmpegStreamCopyRemuxer.Remux(null, null, Path.Combine(_dir, "x.mkv")));

    // ---- embedded still image / thumbnail (Ideas.txt) --------------------------------------------

    [RemuxFact]
    public void Remux_WithThumbnail_AutomaticElectionStillPlaysTheRealVideo()
    {
        var video = Generate("-f lavfi -i testsrc2=duration=2:size=192x108:rate=30 -c:v libx264 -pix_fmt yuv420p -an", "v.mp4");
        var audio = Generate("-f lavfi -i sine=frequency=440:duration=2 -c:a aac -vn", "a.m4a");
        var thumb = Generate("-f lavfi -i color=c=red:size=64x36 -frames:v 1", "thumb.jpg");
        var output = Path.Combine(_dir, "with-thumb.mkv");

        FFmpegStreamCopyRemuxer.Remux(video, audio, output, stillImagePath: thumb);

        // The container carries BOTH video streams; the thumbnail is the attached-picture one.
        var streams = MediaContainerDecoder.ProbeStreams(output);
        var videoStreams = streams.Where(s => s.Kind == MediaStreamKind.Video).ToList();
        Assert.Equal(2, videoStreams.Count);
        Assert.Single(videoStreams, s => s.IsAttachedPicture);

        // Automatic election must keep playing the REAL video, exactly like MP3 cover art is skipped.
        var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));
        Assert.True(registry.TryOpenVideo(output, options: null, out var v));
        try
        {
            Assert.True(v.Format.Width == 192 && v.Format.Height == 108,
                $"automatic election picked the thumbnail ({v.Format.Width}x{v.Format.Height})");
        }
        finally
        {
            (v as IDisposable)?.Dispose();
        }

        // Explicit selection of the attached-picture stream shows the thumbnail (cue video-tab picker).
        var thumbIndex = videoStreams.Single(s => s.IsAttachedPicture).Index;
        Assert.True(registry.TryOpenVideo(
            output, new VideoSourceOpenOptions { VideoStreamIndex = thumbIndex }, out var cover));
        try
        {
            Assert.True(cover.Format.Width == 64 && cover.Format.Height == 36,
                $"explicit stream selection did not open the thumbnail ({cover.Format.Width}x{cover.Format.Height})");
        }
        finally
        {
            (cover as IDisposable)?.Dispose();
        }
    }

    [RemuxFact]
    public void Remux_AudioOnlyWithThumbnail_ShowsTheCoverByDefault()
    {
        // The audio-only YouTube item with an embedded thumbnail: the cover is the ONLY video stream,
        // so automatic election falls through to it - the cue can place it like MP3 cover art.
        var audio = Generate("-f lavfi -i sine=frequency=220:duration=1 -c:a aac -vn", "a2.m4a");
        var thumb = Generate("-f lavfi -i color=c=blue:size=64x36 -frames:v 1", "thumb2.jpg");
        var output = Path.Combine(_dir, "audio-thumb.mkv");

        FFmpegStreamCopyRemuxer.Remux(videoPath: null, audioPath: audio, output, stillImagePath: thumb);

        var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()));
        Assert.True(registry.TryOpenAudio(output, options: null, out var a), "audio track lost");
        (a as IDisposable)?.Dispose();

        Assert.True(registry.TryOpenVideo(output, options: null, out var cover), "cover stream not openable");
        try
        {
            Assert.True(cover.Format.Width == 64 && cover.Format.Height == 36);
        }
        finally
        {
            (cover as IDisposable)?.Dispose();
        }
    }
}
