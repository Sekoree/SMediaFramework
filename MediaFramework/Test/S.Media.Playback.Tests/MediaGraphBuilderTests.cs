using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class MediaGraphBuilderTests
{
    [Fact]
    public void File_TryBuild_ReturnsOwningGraphWithHealthSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mf_graph_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            Assert.True(
                MediaGraphBuilder.File(path)
                    .WithOptions(o => o with { AudioChunkSamples = 960 })
                    .TryBuild(out var graph, out var error),
                error);

            using var built = graph!;
            Assert.Equal(MediaGraphTopology.FilePlayback, built.Topology);
            Assert.Equal("file playback", built.Description);
            Assert.Same(built.Player, built.Session.Player);
            Assert.Equal(960, built.Player.AudioRouter!.ChunkSamples);

            var health = built.GetHealthSnapshot();
            Assert.NotNull(health.Video);
            Assert.NotNull(health.AudioRouter);
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void File_TryBuild_MissingFile_ReturnsFalse()
    {
        Assert.False(
            MediaGraphBuilder.File("/missing/media-" + Guid.NewGuid())
                .TryBuild(out var graph, out var error));

        Assert.Null(graph);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void File_DryRunValidate_ChecksPathAndOptionsBeforeNativeOpen()
    {
        var missing = MediaGraphBuilder.File("/missing/media-" + Guid.NewGuid()).DryRunValidate();
        Assert.False(missing.IsValid);
        Assert.NotEmpty(missing.Errors);

        var path = Path.Combine(Path.GetTempPath(), $"mf_graph_dry_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, CreateWavBytes());
        try
        {
            var valid = MediaGraphBuilder.File(path).DryRunValidate();
            Assert.True(valid.IsValid);
            Assert.NotEmpty(valid.Warnings);
            Assert.Contains(MediaGraphTopology.NdiInputToPreviewAndProgram, MediaGraphBuilder.CommonPresets.Select(p => p.Topology));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static byte[] CreateWavBytes()
    {
        const int sampleRate = 48_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int samples = 4800;
        var dataBytes = samples * channels * bitsPerSample / 8;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * bitsPerSample / 8);
        bw.Write((short)(channels * bitsPerSample / 8));
        bw.Write(bitsPerSample);
        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);
        for (var i = 0; i < samples; i++)
            bw.Write((short)0);
        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
