using HaPlay.ViewModels;
using PMLib.MessageTypes;
using Xunit;

namespace HaPlay.Tests;

public sealed class CueMidiActionMessageTests
{
    [Fact]
    public void ParseEditorState_ReadsHighResolutionCc()
    {
        var state = CueMidiActionMessage.ParseEditorState("ch2 cc14 12 4096");

        Assert.Equal(CueMidiCommandType.HighResolutionControlChange, state.CommandType);
        Assert.Equal(2, state.Channel);
        Assert.Equal(12, state.Data1);
        Assert.Equal(4096, state.Data2);
        Assert.Equal("ch2 cc14 12 4096", CueMidiActionMessage.BuildCommandText(state.CommandType, state.Channel, state.Data1, state.Data2, state.DataText));
    }

    [Fact]
    public void CreateMessage_KeepsLegacyNoteOnFormat()
    {
        var (message, description) = CueMidiActionMessage.CreateMessage("ch1 noteon 60 100", defaultZeroBasedChannel: 4);

        var note = Assert.IsType<NoteOn>(message);
        Assert.Equal(0, note.Channel);
        Assert.Equal(60, note.Note);
        Assert.Equal(100, note.Velocity);
        Assert.Equal("noteon ch1", description);
    }

    [Fact]
    public void CreateMessage_CreatesPitchBend()
    {
        var (message, description) = CueMidiActionMessage.CreateMessage("ch3 pitchbend -1200", defaultZeroBasedChannel: 0);

        var bend = Assert.IsType<PitchBend>(message);
        Assert.Equal(2, bend.Channel);
        Assert.Equal(-1200, bend.Value);
        Assert.Equal("pitchbend ch3", description);
    }

    [Fact]
    public void CreateMessage_NormalizesSysExBytes()
    {
        var (message, description) = CueMidiActionMessage.CreateMessage("sysex 7D 01", defaultZeroBasedChannel: 0);

        var sysEx = Assert.IsType<SysEx>(message);
        Assert.Equal(new byte[] { 0xF0, 0x7D, 0x01, 0xF7 }, sysEx.Data);
        Assert.Equal("sysex", description);
    }

    [Fact]
    public void CreateMessage_CreatesNrpn()
    {
        var (message, description) = CueMidiActionMessage.CreateMessage("ch4 nrpn 100 200", defaultZeroBasedChannel: 0);

        var nrpn = Assert.IsType<NRPN>(message);
        Assert.Equal(3, nrpn.Channel);
        Assert.Equal(100, nrpn.Parameter);
        Assert.Equal(200, nrpn.Value);
        Assert.Equal("nrpn ch4", description);
    }
}
