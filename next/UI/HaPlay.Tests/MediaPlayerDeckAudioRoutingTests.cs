using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>Covers the deck→ShowSession live-source descriptors and audio matrix conversion used to route a
/// deck's audio to its selected device on the default ShowSession path.</summary>
public sealed class MediaPlayerDeckAudioRoutingTests
{
    [Fact]
    public void LiveInputUris_preservePerItemOptions()
    {
        var ndi = new NDIInputPlaylistItem("Camera A (LAN)")
        {
            VideoOnly = true,
            LowBandwidth = true,
            AudioMinBufferedDurationMs = 25,
        };
        var ndiUri = MediaPlayerViewModel.BuildNdiInputUri(ndi);
        var parsedNdi = S.Media.NDI.NDIDecoderProvider.ParseSourceUri(ndiUri);
        Assert.Equal("Camera A (LAN)", parsedNdi.SourceName);
        Assert.False(parsedNdi.ReceiveAudio);
        Assert.True(parsedNdi.ReceiveVideo);
        Assert.True(parsedNdi.LowBandwidth);
        Assert.Equal(TimeSpan.FromMilliseconds(25), parsedNdi.AudioMinBufferedDuration);

        var pa = new PortAudioInputPlaylistItem("USB In")
        {
            HostApiName = "JACK / PipeWire",
            HostApiIndex = 3,
            GlobalDeviceIndex = 11,
            Channels = 6,
            SampleRate = 96000,
            SuggestedLatency = 0.0075,
        };
        var descriptor = S.Media.Audio.PortAudio.PortAudioCaptureDecoderProvider.ParseDescriptor(
            MediaPlayerViewModel.BuildPortAudioInputUri(pa));
        Assert.Equal("USB In", descriptor.DeviceName);
        Assert.Equal("JACK / PipeWire", descriptor.HostApiName);
        Assert.Equal(3, descriptor.HostApiIndex);
        Assert.Equal(11, descriptor.GlobalDeviceIndex);
        Assert.Equal(6, descriptor.Channels);
        Assert.Equal(96000, descriptor.SampleRate);
        Assert.Equal(0.0075, descriptor.SuggestedLatencySeconds);
    }

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

    [Fact]
    public void GainMatrixCells_PreserveMixesCellGainsAndInputTrim()
    {
        var cells = MediaPlayerViewModel.BuildDeckGainMatrixCells([
            (Input: 0, Output: 0, GainDb: -6.0, Muted: false, InputTrimDb: 3.0, InputMuted: false),
            (Input: 1, Output: 0, GainDb: -12.0, Muted: false, InputTrimDb: 0.0, InputMuted: false),
            (Input: 2, Output: 1, GainDb: 0.0, Muted: false, InputTrimDb: 0.0, InputMuted: true),
        ]);

        Assert.Equal(2, cells.Count); // both inputs still contribute to output 0; muted trim is removed
        Assert.Equal((0, 0), (cells[0].InputChannel, cells[0].OutputChannel));
        Assert.Equal(Math.Pow(10, -3.0 / 20.0), cells[0].Gain, precision: 5);
        Assert.Equal((1, 0), (cells[1].InputChannel, cells[1].OutputChannel));
        Assert.Equal(Math.Pow(10, -12.0 / 20.0), cells[1].Gain, precision: 5);
    }
}
