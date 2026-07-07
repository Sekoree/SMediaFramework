using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>A11Y-02 acceptance smoke: soundboard tiles must be real, keyboard-focusable Buttons that a
/// screen reader can name — not pointer-only Borders.</summary>
public sealed class SoundboardAccessibilityTests
{
    [Fact]
    public void SoundboardTiles_AreFocusableButtons_WithAutomationNames()
    {
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(SoundboardAccessibilityTests).Assembly)
            .Dispatch(static () =>
            {
                var vm = new SoundboardWorkspaceViewModel();
                var board = vm.Boards[0];
                vm.SelectedBoard = board;
                board.BindTile(board.Tiles[0], "/tmp/sting.wav");

                var window = new Window { Width = 900, Height = 640, Content = new SoundboardView { DataContext = vm } };
                window.Show();
                Dispatcher.UIThread.RunJobs(); // flush the initial layout so ItemsControl realizes its tiles

                var tiles = window.GetVisualDescendants()
                    .OfType<Button>()
                    .Where(b => b.Classes.Contains("tile"))
                    .ToList();

                Assert.NotEmpty(tiles); // tiles realized as Buttons, not Borders
                Assert.All(tiles, t => Assert.True(t.Focusable, "a soundboard tile is not keyboard-focusable"));

                var bound = tiles.FirstOrDefault(t => t.DataContext is SoundboardTileViewModel { IsBound: true });
                Assert.NotNull(bound);
                Assert.False(
                    string.IsNullOrEmpty(AutomationProperties.GetName(bound!)),
                    "a bound tile exposes no automation name to screen readers");

                window.Close();
            }, CancellationToken.None);
    }
}
