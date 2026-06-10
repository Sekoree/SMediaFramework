using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>UI rewrite P5b: channel-count → preset auto-rules on the player routing matrix.</summary>
public sealed class ChannelPresetRuleTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(ChannelPresetRuleTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static (MediaPlayerViewModel Player, PlayerOutputBinding Binding) CreateStereoOutPlayer()
    {
        var om = new OutputManagementViewModel();
        om.ReplaceDefinitionsForLoad(new OutputDefinition[]
        {
            new PortAudioOutputDefinition(Guid.NewGuid(), "PA", 0, "Alsa", 1, "dev", 2, 48000),
        });
        var player = new MediaPlayerViewModel(om, "P1");
        var binding = Assert.Single(player.Outputs);
        binding.IsSelected = true;
        return (player, binding);
    }

    [Fact]
    public void RuleFires_WhenSourceChannelCountChangesToMatch()
    {
        DispatchUi(static () =>
        {
            var (player, binding) = CreateStereoOutPlayer();
            player.AudioMatrixSourceChannels = 2; // stereo baseline, identity
            player.ChannelPresetRules.Add(new ChannelPresetRule
            {
                SourceChannels = 6,
                Preset = AudioDownmixPreset.Surround51ToStereo,
            });

            player.AudioMatrixSourceChannels = 6; // the occasional 5.1 file arrives

            var matrix = binding.Matrix;
            Assert.Equal(6, matrix.InputChannelCount);
            // ITU fold-down: FL→L unity, FC→L at −3 dB, LFE silent everywhere.
            var flToL = matrix.Cell(0, 0)!;
            var fcToL = matrix.Cell(2, 0)!;
            var fcToR = matrix.Cell(2, 1)!;
            var lfeToL = matrix.Cell(3, 0)!;
            Assert.False(flToL.Muted);
            Assert.False(fcToL.Muted);
            Assert.False(fcToR.Muted);
            Assert.Equal(AudioDownmixPresets.Minus3Db, fcToL.GainDb, 3);
            Assert.True(lfeToL.Muted);
        });
    }

    [Fact]
    public void WithoutRule_ResizeKeepsIdentityDefault()
    {
        DispatchUi(static () =>
        {
            var (player, binding) = CreateStereoOutPlayer();
            player.AudioMatrixSourceChannels = 6;

            var matrix = binding.Matrix;
            // Identity diagonal: FC→L stays muted (the lossy default the rule exists to fix).
            Assert.False(matrix.Cell(0, 0)!.Muted);
            Assert.True(matrix.Cell(2, 0)!.Muted);
        });
    }

    [Fact]
    public void AddRule_ReplacesSameChannelCount_AndAppliesImmediatelyWhenMatching()
    {
        DispatchUi(static () =>
        {
            var (player, binding) = CreateStereoOutPlayer();
            player.AudioMatrixSourceChannels = 6; // already 6ch, no rule yet → identity

            player.NewRuleChannels = 6;
            player.NewRulePreset = AudioDownmixPreset.Surround51ToStereo;
            player.AddChannelPresetRuleCommand.Execute(null);

            Assert.Single(player.ChannelPresetRules);
            Assert.False(binding.Matrix.Cell(2, 0)!.Muted); // applied immediately to the live 6ch matrix

            player.NewRulePreset = AudioDownmixPreset.DropLfe;
            player.AddChannelPresetRuleCommand.Execute(null);
            var rule = Assert.Single(player.ChannelPresetRules); // replaced, not appended
            Assert.Equal(AudioDownmixPreset.DropLfe, rule.Preset);
        });
    }

    [Fact]
    public void Rules_RoundTripThroughPlayerConfig()
    {
        DispatchUi(static () =>
        {
            var (player, _) = CreateStereoOutPlayer();
            player.ChannelPresetRules.Add(new ChannelPresetRule
            {
                SourceChannels = 8,
                Preset = AudioDownmixPreset.DropLfe,
            });

            var config = player.BuildPlayerConfigSnapshot();
            var rule = Assert.Single(config.ChannelPresetRules);
            Assert.Equal(8, rule.SourceChannels);

            var (restored, _) = CreateStereoOutPlayer();
            restored.ApplyPlayerConfigSnapshot(config);
            Assert.Equal(AudioDownmixPreset.DropLfe, Assert.Single(restored.ChannelPresetRules).Preset);
        });
    }
}
