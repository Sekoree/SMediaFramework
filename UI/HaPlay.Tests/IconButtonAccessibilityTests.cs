using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>A11Y-01 acceptance smoke: an icon-only interactive control (a Button/ToggleButton whose visible
/// content is a glyph, not text) must expose an <see cref="AutomationProperties"/> name so a screen reader can
/// announce it. This guards the media-player transport strip (Previous/Next/Stop/Play-Pause and the settings
/// overflow), the workspace with the most icon-only controls, against a name silently going missing.</summary>
public sealed class IconButtonAccessibilityTests
{
    [Fact]
    public void MediaPlayer_IconOnlyControls_ExposeAutomationNames()
    {
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(IconButtonAccessibilityTests).Assembly)
            .Dispatch(static () =>
            {
                var main = new MainViewModel();
                var player = main.Players[0];

                var window = new Window { Width = 1000, Height = 700, Content = new MediaPlayerView { DataContext = player } };
                window.Show();
                Dispatcher.UIThread.RunJobs(); // realize the transport template + playlist

                var unnamed = window.GetVisualDescendants()
                    .OfType<Button>() // Button covers ToggleButton/RepeatButton (both derive from it)
                    .Where(IsIconOnly)
                    .Where(b => string.IsNullOrEmpty(AutomationProperties.GetName(b)))
                    .Select(b => b.Name ?? b.GetType().Name)
                    .ToList();

                window.Close();

                Assert.True(
                    unnamed.Count == 0,
                    "icon-only controls without an AutomationProperties.Name: " + string.Join(", ", unnamed));
            }, CancellationToken.None);
    }

    // Icon-only = has a glyph (PathIcon/Image) somewhere in its realized template and no non-whitespace text.
    // A control that carries a visible text label is already announced by that label, so it is exempt.
    private static bool IsIconOnly(Button button)
    {
        var descendants = button.GetVisualDescendants().ToList();
        var hasGlyph = descendants.OfType<PathIcon>().Any() || descendants.OfType<Image>().Any();
        if (!hasGlyph)
            return false;
        return !descendants.OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
    }
}
