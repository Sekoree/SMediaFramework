using Avalonia.Headless;
using Avalonia.Media;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>The per-deck tint (color-code) on the media player: set/clear, derived brushes, and config round-trip.</summary>
public sealed class MediaPlayerTintTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(MediaPlayerTintTests).Assembly)
            .Dispatch(action, CancellationToken.None);

    private static MediaPlayerViewModel CreateDeck() => new(new OutputManagementViewModel(), "P1");

    [Fact]
    public void SetTint_SetsAndClearsTheTint()
    {
        DispatchUi(() =>
        {
            var deck = CreateDeck();
            Assert.False(deck.HasTint);
            Assert.Same(Brushes.Transparent, deck.TintBrush);

            deck.SetTintCommand.Execute(Colors.SlateBlue);
            Assert.True(deck.HasTint);
            Assert.Equal(Colors.SlateBlue, deck.TintColor);
            Assert.Equal(Colors.SlateBlue, deck.TintColorValue);
            Assert.IsType<SolidColorBrush>(deck.TintAccentBrush);

            deck.SetTintCommand.Execute(null); // the "None" swatch
            Assert.False(deck.HasTint);
            Assert.Same(Brushes.Transparent, deck.TintBrush);
        });
    }

    [Fact]
    public void Tint_RoundTripsThroughThePlayerConfig()
    {
        DispatchUi(() =>
        {
            var deck = CreateDeck();
            deck.TintColor = Colors.Orange;

            var snapshot = deck.BuildPlayerConfigSnapshot();
            Assert.Equal(Colors.Orange.ToUInt32(), snapshot.TintArgb);

            var restored = CreateDeck();
            restored.ApplyPlayerConfigSnapshot(snapshot);
            Assert.Equal(Colors.Orange, restored.TintColor);

            Assert.Null(CreateDeck().BuildPlayerConfigSnapshot().TintArgb); // untinted persists null, not 0
        });
    }
}
