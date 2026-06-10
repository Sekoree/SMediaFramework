using S.Media.Core.Audio;
using Xunit;

namespace S.Media.Core.Tests.Audio;

/// <summary>P5c: the shareable mix-preset file format consumed by HaPlay's matrix dialog.</summary>
public sealed class AudioMixPresetTests
{
    [Fact]
    public void FromMatrix_ToMatrix_RoundTrips()
    {
        var gains = new float[3, 2];
        gains[0, 0] = 1f;
        gains[2, 1] = 0.7071f;

        var preset = AudioMixPreset.FromMatrix("test", gains);
        Assert.Equal(3, preset.SourceChannels);
        Assert.Equal(2, preset.OutputChannels);

        var back = preset.ToMatrix();
        Assert.Equal(1f, back[0, 0]);
        Assert.Equal(0.7071f, back[2, 1]);
        Assert.Equal(0f, back[1, 0]);
    }

    [Fact]
    public void SaveLoad_RoundTripsThroughFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfmix_{Guid.NewGuid():N}.{AudioMixPreset.FileExtension}");
        try
        {
            AudioMixPreset.FromMatrix("fold", AudioChannelLayoutPresets.Downmix(6, 2)).Save(path);
            var loaded = AudioMixPreset.Load(path);

            Assert.Equal("fold", loaded.Name);
            Assert.Equal(AudioMixPreset.CurrentSchema, loaded.Schema);
            var m = loaded.ToMatrix();
            Assert.Equal(6, m.GetLength(0));
            Assert.Equal(2, m.GetLength(1));
            Assert.Equal(0f, m[3, 0]); // LFE dropped survives the file round-trip
            Assert.True(m[2, 0] > 0f); // center fold present
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RejectsForeignSchemaAndEmptyGains()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mfmix_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{"schema":"something/else","name":"x","gains":[[1]]}""");
            Assert.Throws<InvalidDataException>(() => AudioMixPreset.Load(path));

            File.WriteAllText(path, $$"""{"schema":"{{AudioMixPreset.CurrentSchema}}","name":"x","gains":[]}""");
            Assert.Throws<InvalidDataException>(() => AudioMixPreset.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ToMatrix_ZeroPadsRaggedHandEditedRows()
    {
        var preset = new AudioMixPreset { Name = "ragged", Gains = [[1f], [0.5f, 0.25f]] };
        var m = preset.ToMatrix();
        Assert.Equal(2, m.GetLength(0));
        Assert.Equal(2, m.GetLength(1));
        Assert.Equal(0f, m[0, 1]);
        Assert.Equal(0.25f, m[1, 1]);
    }
}
