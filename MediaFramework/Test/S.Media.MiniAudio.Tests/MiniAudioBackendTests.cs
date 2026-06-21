using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.MiniAudio;
using Xunit;

namespace S.Media.MiniAudio.Tests;

public sealed class MiniAudioBackendTests
{
    [Fact]
    public void Name_IsMiniaudio()
    {
        Assert.Equal("miniaudio", new MiniAudioBackend().Name);
    }

    [Fact]
    public void UseMiniAudio_RegistersBackendWithoutOpeningNativeDevice()
    {
        MediaFrameworkRuntime.Init().UseMiniAudio();

        Assert.True(AudioBackends.TryGet("MINIAUDIO", out var backend));
        Assert.IsType<MiniAudioBackend>(backend);
    }

    [Fact]
    public void CreateOutput_InvalidFormat_FailsBeforeNativeOpen()
    {
        var backend = new MiniAudioBackend();

        var ex = Assert.Throws<ArgumentException>(() =>
            backend.CreateOutput(null, new AudioFormat(0, 2)));

        Assert.Contains("SampleRate", ex.Message);
    }

    [Fact]
    public void Output_SubmitAndDrain_UsesManagedRingWithoutDevice()
    {
        using var output = new MiniAudioOutput(new AudioFormat(48_000, 2), ringCapacityFrames: 64);

        var samples = Enumerable.Range(0, 32).Select(i => (float)i).ToArray();
        output.Submit(samples);

        var drained = new float[32];
        Assert.Equal(32, output.TryDrainForTest(drained));
        Assert.Equal(samples, drained);
    }

    [Fact]
    public void Input_ReadInto_EmptyRingReturnsZeroWithoutDevice()
    {
        using var input = new MiniAudioInput(new AudioFormat(48_000, 2), ringCapacityFrames: 64);

        Assert.Equal(0, input.ReadInto(new float[32]));
    }
}
