using PMLib;
using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// MIDI Control Change message (status <c>0xBn</c>).
/// Supports both standard 7-bit values (0–127) and high-resolution 14-bit values (0–16383).
/// </summary>
/// <remarks>
/// <b>14-bit (high-resolution) CC:</b> MIDI achieves 14-bit resolution by pairing a coarse
/// controller (CC 0–31) with its fine counterpart (CC 32–63, where fine = coarse + 32).
/// When <see cref="IsHighResolution"/> is <see langword="true"/>, <see cref="WriteTo"/> emits
/// <b>two</b> consecutive short messages: the coarse CC followed by the fine CC.
/// Therefore <see cref="Controller"/> must be in the range 0–31 for 14-bit messages.
///
/// <b>Receiving 14-bit CC:</b> PortMidi delivers each MIDI byte individually, so a 14-bit
/// update arrives as two separate <see cref="ControlChange"/> instances (one coarse, one fine).
/// Use <see cref="FromCoarseFine"/> to combine them into a single high-resolution value.
/// </remarks>
public readonly struct ControlChange : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }

    /// <summary>
    /// Controller number, 0–127.
    /// For high-resolution messages this must be 0–31 (the coarse controller);
    /// the fine controller (<c>Controller + 32</c>) is sent automatically.
    /// </summary>
    public byte Controller { get; }

    /// <summary>
    /// Controller value.
    /// 0–127 for standard 7-bit messages; 0–16383 for 14-bit high-resolution messages.
    /// </summary>
    public ushort Value { get; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="Value"/> is a 14-bit value (0–16383) and
    /// <see cref="WriteTo"/> sends two MIDI messages (coarse + fine).
    /// </summary>
    public bool IsHighResolution { get; }

    public MIDIMessageType MessageType => MIDIMessageType.ControlChange;

    /// <summary>Constructs a standard 7-bit Control Change message.</summary>
    public ControlChange(byte channel, byte controller, byte value)
    {
        Channel = channel;
        Controller = controller;
        Value = value;
        IsHighResolution = false;
    }

    private ControlChange(byte channel, byte controller, ushort value, bool highRes)
    {
        Channel = channel;
        Controller = controller;
        Value = value;
        IsHighResolution = highRes;
    }

    /// <summary>
    /// Creates a 14-bit high-resolution Control Change message from a raw 14-bit value.
    /// </summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <param name="controller">Coarse controller number, 0–31.</param>
    /// <param name="value">14-bit value, 0–16383.</param>
    public static ControlChange HighRes(byte channel, byte controller, ushort value)
        => new(channel, controller, (ushort)(value & 0x3FFF), highRes: true);

    /// <summary>
    /// Creates a 14-bit high-resolution Control Change message by combining a received
    /// coarse byte and fine byte (e.g. from two consecutive received CC messages).
    /// </summary>
    /// <param name="channel">MIDI channel, 0–15.</param>
    /// <param name="controller">Coarse controller number, 0–31.</param>
    /// <param name="coarse">MSB data byte (from CC <paramref name="controller"/>), 0–127.</param>
    /// <param name="fine">LSB data byte (from CC <paramref name="controller"/> + 32), 0–127.</param>
    public static ControlChange FromCoarseFine(byte channel, byte controller, byte coarse, byte fine)
        => HighRes(channel, controller, (ushort)((coarse << 7) | fine));

    public PmError WriteTo(nint stream, int timestamp)
    {
        byte statusByte = (byte)(0xB0 | (Channel & 0x0F));
        if (IsHighResolution)
        {
            byte msb = (byte)((Value >> 7) & 0x7F);
            byte lsb = (byte)(Value & 0x7F);
            var err = Native.Pm_WriteShort(stream, timestamp,
                PmEvent.CreateMessage(statusByte, Controller, msb));
            if (err != PmError.NoError) return err;
            return Native.Pm_WriteShort(stream, timestamp,
                PmEvent.CreateMessage(statusByte, (byte)(Controller + 32), lsb));
        }
        return Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(statusByte, Controller, (byte)(Value & 0x7F)));
    }
}
