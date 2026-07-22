using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// The visualizer cue's placement spec must carry the Effects-tab color stage (chroma key,
/// brightness/contrast) like a media cue's does — 2026-07-21 report: the coordinator's
/// visualizer mapping dropped both, so the controls silently did nothing on visualizer layers.
/// </summary>
public sealed class VisualizerPlacementEffectsTests
{
    private static CueVideoPlacement EffectPlacement() => new()
    {
        CompositionId = Guid.NewGuid(),
        LayerIndex = 1,
        ChromaKeyEnabled = true,
        ChromaKey = new CueChromaKey
        {
            KeyR = 0, KeyG = 0, KeyB = 0, // key out black (visualizer background)
            Similarity = 0.8,
            Smoothness = 0.08,
            SpillSuppression = 0.1,
        },
        ColorAdjustEnabled = true,
        ColorAdjust = new CueColorAdjust { Brightness = 0.2, Contrast = 1.5 },
    };

    [Fact]
    public void ToVisualizerPlacement_ForwardsChromaKeyAndColorAdjust()
    {
        var placement = EffectPlacement();

        var spec = CueShowSessionCoordinator.ToVisualizerPlacement("comp", placement);

        Assert.NotNull(spec.ChromaKey);
        Assert.Equal(0.8f, spec.ChromaKey!.Value.Similarity);
        Assert.NotNull(spec.ColorAdjust);
        Assert.Equal(0.2f, spec.ColorAdjust!.Value.Brightness);
        Assert.Equal(1.5f, spec.ColorAdjust!.Value.Contrast);
    }

    [Fact]
    public void ToVisualizerPlacement_DisabledEffects_StayNull()
    {
        var placement = EffectPlacement() with { ChromaKeyEnabled = false, ColorAdjustEnabled = false };

        var spec = CueShowSessionCoordinator.ToVisualizerPlacement("comp", placement);

        Assert.Null(spec.ChromaKey);
        Assert.Null(spec.ColorAdjust);
    }

    [Fact]
    public void ToVisualizerPlacement_LegacyFullCanvas_HasNoEffects()
    {
        var spec = CueShowSessionCoordinator.ToVisualizerPlacement("comp", null);

        Assert.Null(spec.ChromaKey);
        Assert.Null(spec.ColorAdjust);
    }
}
