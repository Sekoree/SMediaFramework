using PMLib.MessageTypes;
using PMLib.Types;
using Xunit;

namespace PMLib.Tests;

public sealed class MIDIMessageParserTests
{
    [Fact]
    public void Decode_NoteOnWithVelocity_DecodesNoteOn()
    {
        var msg = MIDIMessageParser.Decode(Event(0x92, 60, 100));
        var note = Assert.IsType<NoteOn>(msg);

        Assert.Equal(2, note.Channel);
        Assert.Equal(60, note.Note);
        Assert.Equal(100, note.Velocity);
    }

    [Fact]
    public void Decode_NoteOnWithZeroVelocity_DecodesNoteOff()
    {
        var msg = MIDIMessageParser.Decode(Event(0x91, 64, 0));
        var note = Assert.IsType<NoteOff>(msg);

        Assert.Equal(1, note.Channel);
        Assert.Equal(64, note.Note);
        Assert.Equal(0, note.Velocity);
    }

    [Fact]
    public void Decode_PitchBend_DecodesSignedCenterRelativeValue()
    {
        var msg = MIDIMessageParser.Decode(Event(0xE3, 0x00, 0x40));
        var bend = Assert.IsType<PitchBend>(msg);

        Assert.Equal(3, bend.Channel);
        Assert.Equal(0, bend.Value);
        Assert.Equal(8192, bend.RawValue);
    }

    [Fact]
    public void Decode_SystemCommon_DecodesSongPosition()
    {
        var msg = MIDIMessageParser.Decode(Event(0xF2, 0x01, 0x02));
        var position = Assert.IsType<SongPosition>(msg);

        Assert.Equal(257, position.Beats);
    }

    [Fact]
    public void Decode_RealTime_DecodesTimingClock()
    {
        var msg = MIDIMessageParser.Decode(Event(0xF8, 0, 0));

        Assert.IsType<TimingClock>(msg);
    }

    private static PmEvent Event(byte status, byte data1, byte data2) =>
        new() { Message = PmEvent.CreateMessage(status, data1, data2) };
}
