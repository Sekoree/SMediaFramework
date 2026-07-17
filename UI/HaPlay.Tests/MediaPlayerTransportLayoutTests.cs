using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HaPlay.ViewModels;
using HaPlay.Views;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// Review P2-10: the transport row previously clipped at the DEFAULT 960×640 window (compact mode
/// only engaged below 500px while the row needs ~830px), hiding the panic-path Next/VIZ controls
/// under the Mute/volume group. These lay the deck out at the default window, the 720px MinWidth
/// window, and a comfortable width, asserting the two transport groups neither overlap each other
/// nor overflow the view.
/// </summary>
public sealed class MediaPlayerTransportLayoutTests
{
    // View widths ≈ window width minus the expanded 180px sidebar and chrome paddings.
    [Theory]
    [InlineData(1100)] // comfortable: everything visible
    [InlineData(760)]  // the 960×640 DEFAULT window's content area - the reported clipping case
    [InlineData(510)]  // the 720px MinWidth window's content area - worst supported case
    public void TransportGroups_NeverOverlapOrClip(double viewWidth)
    {
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerTransportLayoutTests).Assembly)
            .Dispatch(() =>
            {
                var main = new MainViewModel();
                var player = main.Players[0];

                var view = new MediaPlayerView { DataContext = player };
                var window = new Window { Width = viewWidth, Height = 700, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs(); // realize + arrange the transport row

                var primary = view.GetVisualDescendants().OfType<StackPanel>()
                    .First(p => p.Name == "TransportPrimaryGroup");
                var secondary = view.GetVisualDescendants().OfType<StackPanel>()
                    .First(p => p.Name == "TransportSecondaryGroup");

                var primaryRect = primary.TranslatePoint(new Avalonia.Point(0, 0), view)!.Value;
                var secondaryRect = secondary.TranslatePoint(new Avalonia.Point(0, 0), view)!.Value;
                var primaryRight = primaryRect.X + primary.Bounds.Width;
                var secondaryLeft = secondaryRect.X;
                var secondaryRight = secondaryRect.X + secondary.Bounds.Width;

                window.Close();

                Assert.True(
                    primaryRight <= secondaryLeft + 0.5,
                    $"transport groups overlap at {viewWidth}px: primary ends at {primaryRight:0.#}, secondary starts at {secondaryLeft:0.#}");
                Assert.True(
                    primaryRect.X >= -0.5,
                    $"primary transport group clips off the left edge at {viewWidth}px (x={primaryRect.X:0.#})");
                Assert.True(
                    secondaryRight <= view.Bounds.Width + 0.5,
                    $"secondary transport group clips off the right edge at {viewWidth}px (ends at {secondaryRight:0.#}, view is {view.Bounds.Width:0.#})");
            }, CancellationToken.None);
    }
}
