using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The deck header's multi-output aggregate (01·b): worst-case health dot, routed-output count,
/// and the degraded-suffix summary — plus live re-notification when a routed line's health flips. Drives
/// <see cref="MediaPlayerViewModel.DeckOutputHealth"/>, <see cref="MediaPlayerViewModel.DeckOutputHealthColor"/>
/// and <see cref="MediaPlayerViewModel.DeckOutputSummary"/>.</summary>
public sealed class MediaPlayerDeckOutputAggregateTests
{
    private static Task DispatchUi(Func<Task> action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerDeckOutputAggregateTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    // Audio outputs can be routed on any number of players without a video-conflict prompt, so they make
    // the cleanest fixture for exercising the aggregate over an arbitrary routed set.
    private static MediaPlayerViewModel CreatePlayerWithAudioOutputs(int count)
    {
        var outputs = new OutputManagementViewModel();
        var defs = new List<OutputDefinition>();
        for (var i = 0; i < count; i++)
            defs.Add(new PortAudioOutputDefinition(Guid.NewGuid(), $"PA{i}", 0, "Alsa", i, $"dev{i}", 2, 48_000));
        outputs.ReplaceDefinitionsForLoad(defs);
        return new MediaPlayerViewModel(outputs, "P1");
    }

    [Fact]
    public async Task No_selection_reads_as_no_output()
    {
        await DispatchUi(() =>
        {
            var p = CreatePlayerWithAudioOutputs(3);
            Assert.False(p.HasRoutedOutputs);
            Assert.Empty(p.SelectedOutputs);
            Assert.Equal(OutputLineHealthState.Unknown, p.DeckOutputHealth);
            Assert.Equal("No output", p.DeckOutputSummary);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task All_healthy_shows_plain_count_and_healthy_dot()
    {
        await DispatchUi(() =>
        {
            var p = CreatePlayerWithAudioOutputs(3);
            foreach (var b in p.Outputs)
            {
                b.Line.Health = OutputLineHealthState.Healthy;
                b.IsSelected = true;
            }
            Assert.True(p.HasRoutedOutputs);
            Assert.Equal(3, p.SelectedOutputs.Count());
            Assert.Equal(OutputLineHealthState.Healthy, p.DeckOutputHealth);
            Assert.Equal("#2E9E4B", p.DeckOutputHealthColor);
            Assert.Equal("3 outputs", p.DeckOutputSummary);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task One_warning_downgrades_dot_and_appends_reconnecting_suffix()
    {
        await DispatchUi(() =>
        {
            var p = CreatePlayerWithAudioOutputs(3);
            foreach (var b in p.Outputs)
            {
                b.Line.Health = OutputLineHealthState.Healthy;
                b.IsSelected = true;
            }
            p.Outputs[1].Line.Health = OutputLineHealthState.Warning;
            Assert.Equal(OutputLineHealthState.Warning, p.DeckOutputHealth);
            Assert.Equal("#F9A825", p.DeckOutputHealthColor);
            Assert.Equal("3 outputs · 1 reconnecting", p.DeckOutputSummary);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Error_dominates_warning_and_appends_offline_suffix()
    {
        await DispatchUi(() =>
        {
            var p = CreatePlayerWithAudioOutputs(4);
            foreach (var b in p.Outputs)
            {
                b.Line.Health = OutputLineHealthState.Healthy;
                b.IsSelected = true;
            }
            p.Outputs[0].Line.Health = OutputLineHealthState.Warning;
            p.Outputs[3].Line.Health = OutputLineHealthState.Error;
            Assert.Equal(OutputLineHealthState.Error, p.DeckOutputHealth);
            Assert.Equal("#C0392B", p.DeckOutputHealthColor);
            Assert.Equal("4 outputs · 1 offline", p.DeckOutputSummary);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task One_routed_output_uses_the_singular_label()
    {
        await DispatchUi(() =>
        {
            var p = CreatePlayerWithAudioOutputs(3);
            p.Outputs[0].Line.Health = OutputLineHealthState.Healthy;
            p.Outputs[0].IsSelected = true;
            Assert.Equal("1 output", p.DeckOutputSummary);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Unselected_outputs_do_not_affect_the_aggregate()
    {
        await DispatchUi(() =>
        {
            var p = CreatePlayerWithAudioOutputs(3);
            // Two routed + healthy; the third is offline but NOT routed, so it must not degrade the dot.
            p.Outputs[0].Line.Health = OutputLineHealthState.Healthy;
            p.Outputs[1].Line.Health = OutputLineHealthState.Healthy;
            p.Outputs[2].Line.Health = OutputLineHealthState.Error;
            p.Outputs[0].IsSelected = true;
            p.Outputs[1].IsSelected = true;
            Assert.Equal(OutputLineHealthState.Healthy, p.DeckOutputHealth);
            Assert.Equal("2 outputs", p.DeckOutputSummary);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Aggregate_reraises_when_a_routed_line_health_changes()
    {
        await DispatchUi(() =>
        {
            var p = CreatePlayerWithAudioOutputs(2);
            foreach (var b in p.Outputs)
            {
                b.Line.Health = OutputLineHealthState.Healthy;
                b.IsSelected = true;
            }
            var raised = 0;
            p.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MediaPlayerViewModel.DeckOutputSummary))
                    raised++;
            };
            p.Outputs[0].Line.Health = OutputLineHealthState.Error;
            Assert.True(raised >= 1);
            Assert.Equal("2 outputs · 1 offline", p.DeckOutputSummary);
            return Task.CompletedTask;
        });
    }
}
