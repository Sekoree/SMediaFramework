using System.Runtime.InteropServices;

namespace PMLib.Types;

/// <summary>
/// A MIDI event consisting of packed message bytes and a timestamp.
/// Short messages (1–3 bytes) are packed into the low-order bytes of <see cref="Message"/>.
/// SysEx messages are split across multiple consecutive <see cref="PmEvent"/> structures (4 payload bytes each).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct PmEvent
{
    /// <summary>
    /// Up to 4 bytes of MIDI data packed as a 32-bit integer.
    /// Status byte occupies the lowest byte; unused bytes are zero.
    /// Use the static helpers to compose and decompose messages.
    /// </summary>
    public uint Message;

    /// <summary>Timestamp in milliseconds at which this event was received or should be sent.</summary>
    public int Timestamp;

    /// <summary>
    /// Encodes a short MIDI message from its constituent bytes.
    /// </summary>
    /// <param name="status">Status byte (e.g. <c>0x90</c> for Note On on channel 1).</param>
    /// <param name="data1">First data byte (e.g. note number).</param>
    /// <param name="data2">Second data byte (e.g. velocity); pass <c>0</c> if not used.</param>
    public static uint CreateMessage(byte status, byte data1, byte data2)
        => (uint)(((data2 << 16) & 0xFF_0000) | ((data1 << 8) & 0xFF00) | (status & 0xFF));

    /// <summary>Extracts the status byte from a packed MIDI message.</summary>
    public static byte GetStatus(uint message) => (byte)(message & 0xFF);

    /// <summary>Extracts the first data byte (e.g. note number or controller number).</summary>
    public static byte GetData1(uint message) => (byte)((message >> 8) & 0xFF);

    /// <summary>Extracts the second data byte (e.g. velocity or controller value).</summary>
    public static byte GetData2(uint message) => (byte)((message >> 16) & 0xFF);
}
