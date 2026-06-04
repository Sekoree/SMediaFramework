using HaPlay.ControlGraph;
using Xunit;

namespace HaPlay.Tests;

public sealed class XTouchMiniX32FaderMappingTests
{
    [Fact]
    public void TryApplyEncoder_RightTurn_MapsController16ToX32Channel1FromDefault()
    {
        var ok = XTouchMiniX32FaderMapping.TryApplyEncoder(
            midiController: 16,
            midiValue: 1,
            currentFaderValue: null,
            out var update);

        Assert.True(ok);
        Assert.Equal(1, update.Channel);
        Assert.Equal(16, update.MidiController);
        Assert.Equal(1, update.DeltaSteps);
        Assert.Equal("/ch/01/mix/fader", update.OscAddress);
        Assert.Equal(0.75 + 1.0 / 1023.0, update.FaderValue, precision: 12);
    }

    [Fact]
    public void TryApplyEncoder_FasterRightTurn_IncreasesByMoreSteps()
    {
        var ok = XTouchMiniX32FaderMapping.TryApplyEncoder(
            midiController: 16,
            midiValue: 10,
            currentFaderValue: 0.75,
            out var update);

        Assert.True(ok);
        Assert.Equal(10, update.DeltaSteps);
        Assert.Equal(0.75 + 10.0 / 1023.0, update.FaderValue, precision: 12);
    }

    [Fact]
    public void TryApplyEncoder_LeftTurn_MapsController23ToX32Channel8AndDecreases()
    {
        var ok = XTouchMiniX32FaderMapping.TryApplyEncoder(
            midiController: 23,
            midiValue: 72,
            currentFaderValue: 0.75,
            out var update);

        Assert.True(ok);
        Assert.Equal(8, update.Channel);
        Assert.Equal(23, update.MidiController);
        Assert.Equal(-8, update.DeltaSteps);
        Assert.Equal("/ch/08/mix/fader", update.OscAddress);
        Assert.Equal(0.75 - 8.0 / 1023.0, update.FaderValue, precision: 12);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(65, -1)]
    [InlineData(72, -8)]
    public void TryGetDeltaSteps_DecodesXTouchMiniRelativeEncoderValues(int midiValue, int expectedDelta)
    {
        var ok = XTouchMiniX32FaderMapping.TryGetDeltaSteps(midiValue, out var delta);

        Assert.True(ok);
        Assert.Equal(expectedDelta, delta);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(64)]
    [InlineData(73)]
    public void TryGetDeltaSteps_IgnoresValuesOutsideReferenceMapping(int midiValue)
    {
        var ok = XTouchMiniX32FaderMapping.TryGetDeltaSteps(midiValue, out var delta);

        Assert.False(ok);
        Assert.Equal(0, delta);
    }

    [Fact]
    public void ApplyDelta_ClampsToOscFaderRange()
    {
        Assert.Equal(1.0, XTouchMiniX32FaderMapping.ApplyDelta(0.999, 10));
        Assert.Equal(0.0, XTouchMiniX32FaderMapping.ApplyDelta(0.0, -8));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(24)]
    public void TryApplyEncoder_IgnoresControllersOutsideEncoderStrip(int controller)
    {
        var ok = XTouchMiniX32FaderMapping.TryApplyEncoder(controller, 1, 0.75, out _);

        Assert.False(ok);
    }

    [Fact]
    public void BuiltInTemplate_ProvidesExportedXTouchMiniX32FaderHandler()
    {
        var repository = BuiltInControlScriptTemplateRepository.Instance;
        var template = repository.FindById(BuiltInControlScriptTemplateRepository.XTouchMiniX32FadersTemplateId);

        Assert.NotNull(template);
        Assert.Equal("Scripts/xtouch-mini-x32-faders.mnd", template.SuggestedPath);
        Assert.Contains("export fun onXTouchFaderEncoder", template.Source);
        Assert.Contains("const defaultFaderValue = 0.75;", template.Source);
        Assert.Contains("controller < 16 || controller > 23", template.Source);
        Assert.Contains("x32.channelFaderAddress(channel)", template.Source);
        Assert.Contains("osc.send(\"x32\", address, osc.float32(next));", template.Source);
    }

    [Fact]
    public void BuiltInTemplate_ProvidesExportedXTouchMiniX32MuteHandler()
    {
        var repository = BuiltInControlScriptTemplateRepository.Instance;
        var template = repository.FindById(BuiltInControlScriptTemplateRepository.XTouchMiniX32MutesTemplateId);

        Assert.NotNull(template);
        Assert.Equal("Scripts/xtouch-mini-x32-mutes.mnd", template.SuggestedPath);
        Assert.Contains("export fun onXTouchMuteButton", template.Source);
        Assert.Contains("if (note == 89) return 1;", template.Source);
        Assert.Contains("x32.channelMuteAddress(channel)", template.Source);
        Assert.Contains("osc.send(\"x32\", address, osc.int32(nextOn ? 1 : 0));", template.Source);
    }
}
