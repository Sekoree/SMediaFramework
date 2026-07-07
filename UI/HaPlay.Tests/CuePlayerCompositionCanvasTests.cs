using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The Cue player's placement canvas mirrors a resizable local output window: when a composition
/// feeds a windowed local output, the canvas aspect follows that window's live size and updates the moment
/// the operator resizes it (the output line's Definition is replaced with the new window size). Without a
/// bound windowed output the canvas falls back to the composition's own resolution.</summary>
public sealed class CuePlayerCompositionCanvasTests
{
    private static void RunUi(Action body) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(CuePlayerCompositionCanvasTests).Assembly)
            .Dispatch(body, System.Threading.CancellationToken.None)
            .GetAwaiter().GetResult();

    [Fact]
    public void PlacementCanvasAspect_FollowsBoundLocalWindowOutput_AndUpdatesOnResize()
    {
        RunUi(() =>
        {
            var vm = new CuePlayerViewModel();
            var lineId = Guid.NewGuid();
            var def = new LocalVideoOutputDefinition(
                lineId, "Screen", VideoOutputEngine.AvaloniaOpenGl, VideoSurfaceMode.Windowed,
                ScreenIndex: 0, WindowWidth: 800, WindowHeight: 600);
            var line = new OutputLineViewModel(def, _ => { });
            vm.SetAvailableOutputs(new ObservableCollection<OutputLineViewModel> { line });

            vm.AddCueListCommand.Execute(null);     // creates + selects a cue list
            vm.AddCompositionCommand.Execute(null); // adds + selects a composition (default 1920x1080)
            vm.AddVideoOutputCommand.Execute(null); // binds the composition to the local output line

            // Canvas mirrors the window (800x600), not the composition's 1920x1080.
            Assert.Equal(800.0 / 600.0, vm.PlacementCanvasAspect, 3);

            // Resizing the output window re-raises PlacementCanvasAspect with the new aspect.
            var raised = false;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CuePlayerViewModel.PlacementCanvasAspect))
                    raised = true;
            };
            line.ReplaceDefinition(def with { WindowWidth = 1000, WindowHeight = 500 });
            Assert.True(raised, "resizing the bound local window should re-raise PlacementCanvasAspect");
            Assert.Equal(1000.0 / 500.0, vm.PlacementCanvasAspect, 3);

            // Dropping the binding falls back to the composition's own resolution.
            vm.RemoveVideoOutputCommand.Execute(null);
            Assert.Equal(1920.0 / 1080.0, vm.PlacementCanvasAspect, 3);
        });
    }
}
