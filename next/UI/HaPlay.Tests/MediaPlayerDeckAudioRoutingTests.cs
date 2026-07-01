using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Covers <c>MediaPlayerViewModel.BuildDeckChannelMatrix</c> — the deck→ShowSession audio channel map
/// (out←src) used to route a deck's audio to its SELECTED device on the ShowSession path (NXT-06 cutover,
/// stage 2). The actual audio flow reuses the hardware-proven <c>ShowClipAudioRoute</c> mechanism; this pins the
/// mapping (the correctness-critical part — an off-by-one misroutes channels).</summary>
public sealed class MediaPlayerDeckAudioRoutingTests
{
    [Fact]
    public void EmptyGrid_DefaultsToStereoIdentity()
    {
        // The matrix grid is sized lazily once the source channel count is known (on open); until then a deck
        // routes with a plain stereo identity so audio still lands on the selected device.
        Assert.Equal(new[] { 0, 1 }, MediaPlayerViewModel.BuildDeckChannelMatrix([]));
    }

    [Fact]
    public void StereoIdentityCells_MapLeftToLeftRightToRight()
    {
        var map = MediaPlayerViewModel.BuildDeckChannelMatrix([(0, 0, false), (1, 1, false)]);
        Assert.Equal(new[] { 0, 1 }, map);
    }

    [Fact]
    public void SwappedCells_ProduceASwappedMap()
    {
        // out0 ← src1, out1 ← src0
        var map = MediaPlayerViewModel.BuildDeckChannelMatrix([(1, 0, false), (0, 1, false)]);
        Assert.Equal(new[] { 1, 0 }, map);
    }

    [Fact]
    public void MutedCells_AreExcluded_AllMutedIsSilentLine()
    {
        // A single muted route among audible ones is dropped from the map.
        Assert.Equal(new[] { 0, -1 }, MediaPlayerViewModel.BuildDeckChannelMatrix([(0, 0, false), (1, 1, true)]));
        // Every declared cell muted → the line is silent → null (no route emitted).
        Assert.Null(MediaPlayerViewModel.BuildDeckChannelMatrix([(0, 0, true), (1, 1, true)]));
    }

    [Fact]
    public void SparseHigherOutput_FillsGapsWithSilence()
    {
        // Only out2 ← src0; out0/out1 are silence (-1).
        Assert.Equal(new[] { -1, -1, 0 }, MediaPlayerViewModel.BuildDeckChannelMatrix([(0, 2, false)]));
    }

    [Fact]
    public void MultipleInputsToSameOutput_LastCellWins()
    {
        // The int channel map is 1:1 (out←one src); summing multiple inputs into one output is the deferred
        // full-matrix path, so the map keeps the last declared source for a shared output.
        var map = MediaPlayerViewModel.BuildDeckChannelMatrix([(0, 0, false), (1, 0, false)]);
        Assert.Equal(new[] { 1 }, map);
    }
}
