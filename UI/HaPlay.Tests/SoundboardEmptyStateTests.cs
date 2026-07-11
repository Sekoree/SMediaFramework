using Avalonia.Headless;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>UX-10: an empty board shows a call-to-action instead of a blank grid, and it clears the moment
/// the board has content or the operator starts editing.</summary>
public sealed class SoundboardEmptyStateTests
{
    [Fact]
    public async Task ShowEmptyBoardHint_TogglesWithEditModeAndBinding()
    {
        await HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(SoundboardEmptyStateTests).Assembly)
            .DispatchAsync(async () =>
            {
                var vm = new SoundboardWorkspaceViewModel();

                Assert.True(vm.ShowEmptyBoardHint);   // fresh board, nothing bound, not editing
                vm.IsEditMode = true;
                Assert.False(vm.ShowEmptyBoardHint);  // editing shows the "+" drop hints instead
                vm.IsEditMode = false;
                Assert.True(vm.ShowEmptyBoardHint);

                var raised = false;
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(vm.ShowEmptyBoardHint))
                        raised = true;
                };

                await vm.BindFileToTileAsync(vm.SelectedBoard!.Tiles[0], "/tmp/x.wav");

                Assert.False(vm.ShowEmptyBoardHint); // a bound tile means the board is no longer empty
                Assert.True(raised);                 // and the UI was notified to hide the CTA
            }, CancellationToken.None);
    }
}
