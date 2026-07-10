using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class AudioDownmixPresetsTests
{
    [Fact]
    public void Surround51ToStereo_FoldsCenterAndSurroundsAtMinus3()
    {
        var c = AudioDownmixPresets.Contributions(AudioDownmixPreset.Surround51ToStereo, 6, 2).ToList();

        Assert.Contains(new DownmixContribution(0, 0, 0.0), c);                       // L → L
        Assert.Contains(new DownmixContribution(1, 1, 0.0), c);                       // R → R
        Assert.Contains(new DownmixContribution(2, 0, AudioDownmixPresets.Minus3Db), c); // C → L
        Assert.Contains(new DownmixContribution(2, 1, AudioDownmixPresets.Minus3Db), c); // C → R
        Assert.Contains(new DownmixContribution(4, 0, AudioDownmixPresets.Minus3Db), c); // Ls → L
        Assert.Contains(new DownmixContribution(5, 1, AudioDownmixPresets.Minus3Db), c); // Rs → R
        // LFE (channel 3) is never routed.
        Assert.DoesNotContain(c, x => x.InputChannel == AudioDownmixPresets.Lfe51Channel);
    }

    [Fact]
    public void PassThrough_MapsDiagonalUpToMinChannels()
    {
        var c = AudioDownmixPresets.Contributions(AudioDownmixPreset.PassThrough, 6, 2).ToList();
        Assert.Equal(2, c.Count);
        Assert.Contains(new DownmixContribution(0, 0, 0.0), c);
        Assert.Contains(new DownmixContribution(1, 1, 0.0), c);
    }

    [Fact]
    public void MonoToStereo_DuplicatesInputZeroToEveryOutput()
    {
        var c = AudioDownmixPresets.Contributions(AudioDownmixPreset.MonoToStereo, 1, 2).ToList();
        Assert.Equal(2, c.Count);
        Assert.All(c, x => Assert.Equal(0, x.InputChannel));
        Assert.Contains(new DownmixContribution(0, 0, 0.0), c);
        Assert.Contains(new DownmixContribution(0, 1, 0.0), c);
    }

    [Fact]
    public void DropLfe_SkipsChannelThree()
    {
        var c = AudioDownmixPresets.Contributions(AudioDownmixPreset.DropLfe, 6, 6).ToList();
        Assert.DoesNotContain(c, x => x.InputChannel == AudioDownmixPresets.Lfe51Channel);
        Assert.Contains(new DownmixContribution(0, 0, 0.0), c);
        Assert.Contains(new DownmixContribution(5, 5, 0.0), c);
        Assert.Equal(5, c.Count); // 0,1,2,4,5 - channel 3 dropped
    }

    [Theory]
    [InlineData(AudioDownmixPreset.Surround51ToStereo, 2, 2, false)] // stereo source can't 5.1-fold
    [InlineData(AudioDownmixPreset.Surround51ToStereo, 6, 2, true)]
    [InlineData(AudioDownmixPreset.MonoToStereo, 1, 1, false)]       // needs >=2 outputs
    [InlineData(AudioDownmixPreset.MonoToStereo, 1, 2, true)]
    [InlineData(AudioDownmixPreset.DropLfe, 2, 2, false)]            // no LFE channel present
    [InlineData(AudioDownmixPreset.DropLfe, 6, 6, true)]
    [InlineData(AudioDownmixPreset.PassThrough, 2, 2, true)]
    public void IsApplicable_RespectsChannelLayout(AudioDownmixPreset preset, int inCh, int outCh, bool expected)
    {
        Assert.Equal(expected, AudioDownmixPresets.IsApplicable(preset, inCh, outCh));
    }

    [Fact]
    public void MatrixApplyDownmix_51ToStereo_SetsExpectedAudibleCells()
    {
        var matrix = new AudioMatrixViewModel();
        matrix.Resize(6, 2);

        matrix.ApplyDownmix(AudioDownmixPreset.Surround51ToStereo);

        Assert.True(matrix.Cell(0, 0)!.IsAudible);  // L → L
        Assert.True(matrix.Cell(1, 1)!.IsAudible);  // R → R
        Assert.True(matrix.Cell(2, 0)!.IsAudible);  // C → L
        Assert.False(matrix.Cell(3, 0)!.IsAudible); // LFE muted
        Assert.False(matrix.Cell(3, 1)!.IsAudible);
        Assert.True(matrix.Cell(4, 0)!.IsAudible);  // Ls → L
        Assert.False(matrix.Cell(4, 1)!.IsAudible); // Ls not on R
    }

    [Fact]
    public void MatrixApplyDownmix_NotApplicable_LeavesMatrixUntouched()
    {
        var matrix = new AudioMatrixViewModel();
        matrix.Resize(2, 2); // stereo

        // Identity default from Resize: (0,0) and (1,1) audible.
        Assert.True(matrix.Cell(0, 0)!.IsAudible);

        matrix.ApplyDownmix(AudioDownmixPreset.Surround51ToStereo); // not applicable to stereo

        Assert.True(matrix.Cell(0, 0)!.IsAudible); // unchanged - not silenced
        Assert.True(matrix.Cell(1, 1)!.IsAudible);
    }
}
