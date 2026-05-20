using PMLib.Types;

namespace PMLib.MessageTypes;

internal sealed class MIDISysExAccumulator
{
    private List<byte>? _buffer;

    public bool IsActive => _buffer is not null;

    public void Reset() => _buffer = null;

    public IMIDIMessage? Process(PmEvent ev, out SysEx? completed)
    {
        completed = null;
        var status = PmEvent.GetStatus(ev.Message);

        if (_buffer is not null)
        {
            if (MIDIMessageParser.IsRealTime(status))
                return MIDIMessageParser.Decode(ev);

            Accumulate(ev.Message, out completed);
            return null;
        }

        if (status == 0xF0)
        {
            _buffer = [];
            Accumulate(ev.Message, out completed);
            return null;
        }

        return MIDIMessageParser.Decode(ev);
    }

    private void Accumulate(uint message, out SysEx? completed)
    {
        completed = null;
        for (var b = 0; b < 4; b++)
        {
            var value = (byte)((message >> (b * 8)) & 0xFF);

            if (value == 0xF7)
            {
                _buffer!.Add(0xF7);
                completed = new SysEx([.. _buffer]);
                _buffer = null;
                return;
            }

            if (b > 0 && (value & 0x80) != 0)
            {
                _buffer = null;
                return;
            }

            _buffer!.Add(value);
        }
    }
}
