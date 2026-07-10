using System.Diagnostics;
using Xunit;

namespace S.Media.Decode.FFmpeg.Tests;

/// <summary>
/// The incremental subtitle reader behind streaming playback overlays: header/fonts at open, events in
/// caller-pulled batches, non-subtitle streams discarded at the demux level. Batched pulls must yield the
/// exact event set the whole-file <see cref="FFmpegSubtitleDecoder.Decode"/> produces (which itself now
/// runs on the reader - so these also cover that refactor).
/// </summary>
public sealed class FFmpegSubtitleStreamReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("subreader-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>An MKV with a video stream plus a 3-cue ASS subtitle stream (SRT-authored, ASS-muxed) -
    /// the shape of a movie file whose subtitle track the deck plays.</summary>
    private string SubtitledMkv()
    {
        var srt = Path.Combine(_dir, "cues.srt");
        File.WriteAllText(srt,
            "1\n00:00:00,500 --> 00:00:01,500\nfirst line\n\n" +
            "2\n00:00:01,600 --> 00:00:02,400\nsecond line\n\n" +
            "3\n00:00:02,500 --> 00:00:03,000\nthird line\n");

        var path = Path.Combine(_dir, "subtitled.mkv");
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
                "-nostdin -loglevel error -y " +
                "-f lavfi -i testsrc2=duration=3:size=128x72:rate=10 " +
                $"-i \"{srt}\" -map 0:v -map 1 -c:v libx264 -pix_fmt yuv420p -c:s ass " +
                $"\"{path}\"",
            RedirectStandardError = true,
        })!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        Assert.True(p.ExitCode == 0, $"ffmpeg fixture generation failed: {stderr}");
        return path;
    }

    [RemuxFact]
    public void BatchedReads_YieldTheSameEventsAsAFullDecode()
    {
        var path = SubtitledMkv();

        var full = FFmpegSubtitleDecoder.Decode(path);
        Assert.Equal(3, full.Events.Count);

        using var reader = FFmpegSubtitleStreamReader.Open(path);
        Assert.NotEmpty(reader.Header);
        Assert.Equal(full.Header, reader.Header);

        // Pull one event at a time - the streaming pump's shape, worst case.
        var streamed = new List<DecodedSubtitleEvent>();
        var batches = 0;
        while (reader.ReadBatch(streamed, maxEvents: 1))
        {
            batches++;
            Assert.True(batches < 100, "ReadBatch did not reach end of stream");
        }

        Assert.Equal(full.Events.Count, streamed.Count);
        for (var i = 0; i < full.Events.Count; i++)
        {
            Assert.Equal(full.Events[i].StartMs, streamed[i].StartMs);
            Assert.Equal(full.Events[i].DurationMs, streamed[i].DurationMs);
            Assert.Equal(full.Events[i].Body, streamed[i].Body);
        }
    }

    /// <summary>A 60 s MKV with frequent keyframes/clusters and cues at 1 s / 30 s / 55 s - enough
    /// timeline for a demux seek to land measurably past the first cue.</summary>
    private string LongSubtitledMkv()
    {
        var srt = Path.Combine(_dir, "long.srt");
        File.WriteAllText(srt,
            "1\n00:00:01,000 --> 00:00:02,000\nfirst line\n\n" +
            "2\n00:00:30,000 --> 00:00:31,000\nsecond line\n\n" +
            "3\n00:00:55,000 --> 00:00:56,000\nthird line\n");

        var path = Path.Combine(_dir, "long-subtitled.mkv");
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
                "-nostdin -loglevel error -y " +
                "-f lavfi -i testsrc2=duration=60:size=128x72:rate=5 " +
                $"-i \"{srt}\" -map 0:v -map 1 -c:v libx264 -pix_fmt yuv420p -g 10 -c:s ass " +
                $"-cluster_time_limit 2000 \"{path}\"",
            RedirectStandardError = true,
        })!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        Assert.True(p.ExitCode == 0, $"ffmpeg fixture generation failed: {stderr}");
        return path;
    }

    [RemuxFact]
    public void SeekTo_JumpsTheDemuxForward_AndResetsEndOfStream()
    {
        var path = LongSubtitledMkv();
        using var reader = FFmpegSubtitleStreamReader.Open(path);

        // Seek past the first cue: only the later cues demux from here.
        Assert.True(reader.SeekTo(TimeSpan.FromSeconds(25)));
        var events = new List<DecodedSubtitleEvent>();
        while (reader.ReadBatch(events, maxEvents: int.MaxValue))
        {
        }

        var starts = events.Select(e => e.StartMs).ToList();
        Assert.Contains(30_000, starts);
        Assert.Contains(55_000, starts);
        Assert.DoesNotContain(1_000, starts);
        Assert.True(reader.PositionMs >= 30_000, $"frontier did not advance (PositionMs={reader.PositionMs})");

        // Seek back to the head: end-of-stream resets and the early cue is reachable again.
        Assert.True(reader.SeekTo(TimeSpan.Zero));
        events.Clear();
        while (reader.ReadBatch(events, maxEvents: int.MaxValue))
        {
        }

        Assert.Contains(1_000, events.Select(e => e.StartMs));
    }

    [RemuxFact]
    public void ReadBatch_AfterEndOfStream_StaysFalseAndAppendsNothing()
    {
        var path = SubtitledMkv();

        using var reader = FFmpegSubtitleStreamReader.Open(path);
        var events = new List<DecodedSubtitleEvent>();
        while (reader.ReadBatch(events, maxEvents: int.MaxValue))
        {
        }

        var count = events.Count;
        Assert.False(reader.ReadBatch(events, maxEvents: int.MaxValue));
        Assert.Equal(count, events.Count);
    }
}
