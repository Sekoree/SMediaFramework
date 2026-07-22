using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using HaPlay.Models;
using Xunit;

namespace HaPlay.Tests;

/// <summary>UX-03: the shortcut help overlay must list shortcuts and filter by gesture or description.</summary>
public sealed class KeyboardShortcutsTests
{
    [Fact]
    public void ShortcutsDialog_Renders_TheDocumentedGestures()
    {
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(KeyboardShortcutsTests).Assembly)
            .Dispatch(static () =>
            {
                var dialog = new KeyboardShortcutsDialog();
                dialog.Show();
                Dispatcher.UIThread.RunJobs();

                var gestures = dialog.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Select(t => t.Text)
                    .ToList();

                Assert.Contains("Ctrl+N", gestures); // bindings actually resolved to the VM data
                Assert.Contains(gestures, gesture => gesture?.Contains("Esc", StringComparison.Ordinal) == true);
                dialog.Close();
            }, CancellationToken.None);
    }


    [Fact]
    public void Shortcuts_FilterByGestureAndDescription_AndReportNoResults()
    {
        var vm = new KeyboardShortcutsDialogViewModel();

        Assert.True(vm.HasResults);
        Assert.True(vm.FilteredGroups.Sum(g => g.Entries.Count) >= 8);

        // Gesture match: "Ctrl+S" also matches "Ctrl+Shift+S".
        vm.SearchText = "Ctrl+S";
        var byGesture = vm.FilteredGroups.SelectMany(g => g.Entries).ToList();
        Assert.NotEmpty(byGesture);
        Assert.All(byGesture, e => Assert.Contains("ctrl+s", e.Gesture, StringComparison.OrdinalIgnoreCase));

        // Description match.
        vm.SearchText = "panic";
        var byText = vm.FilteredGroups.SelectMany(g => g.Entries).ToList();
        Assert.Single(byText);
        Assert.Contains("panic", byText[0].Description, StringComparison.OrdinalIgnoreCase);

        // No match → empty + HasResults false.
        vm.SearchText = "zznotarealshortcut";
        Assert.Empty(vm.FilteredGroups);
        Assert.False(vm.HasResults);

        // Clearing restores the full list.
        vm.SearchText = "";
        Assert.True(vm.HasResults);
    }

    [Theory]
    [InlineData("Space")]
    [InlineData("Esc")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Shift+F12")]
    public void CueHotkeyGesture_AcceptsEditorSyntax(string gesture) =>
        Assert.True(CueHotkeyGesture.IsValid(gesture));

    [Fact]
    public void CueHotkeyEditor_RejectsConflictsAndCanResetDefaults()
    {
        var vm = new KeyboardShortcutsDialogViewModel(new CueHotkeyProfile
        {
            Go = "G",
            Pause = "G",
        });

        Assert.False(vm.TryBuildCueHotkeys(out _));
        Assert.Contains("conflicts", vm.ValidationMessage, StringComparison.OrdinalIgnoreCase);

        vm.ResetCueHotkeys();
        Assert.True(vm.TryBuildCueHotkeys(out var reset));
        Assert.Equal("Space", reset.Go);
        Assert.Equal("Esc", reset.StopThenPanic);
        Assert.Equal("N", reset.NextVisualizerPreset);
    }
}
