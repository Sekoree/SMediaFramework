using PMLib.Types;

namespace PMLib.MessageTypes;

/// <summary>
/// Composite MIDI message representing a Registered Parameter Number change.
/// Encodes a 14-bit parameter number (CC 101/100) and a 14-bit value (CC 6/38).
/// </summary>
/// <remarks>
/// <b>Wire format:</b> <see cref="WriteTo"/> sends four consecutive CC messages:
/// <list type="number">
///   <item><description>CC 101 — RPN parameter MSB</description></item>
///   <item><description>CC 100 — RPN parameter LSB</description></item>
///   <item><description>CC 6   — Data Entry MSB</description></item>
///   <item><description>CC 38  — Data Entry LSB</description></item>
/// </list>
///
/// <b>Receiving:</b> Use <see cref="PMLib.Accumulators.NRPNAccumulator"/> to automatically
/// assemble individual CC messages into complete <see cref="RPN"/> instances.
/// </remarks>
public readonly struct RPN : IMIDIMessage
{
    /// <summary>MIDI channel, 0–15.</summary>
    public byte Channel { get; }

    /// <summary>
    /// 14-bit parameter number (0–16383), assembled from CC 101 (MSB) and CC 100 (LSB).
    /// </summary>
    public ushort Parameter { get; }

    /// <summary>
    /// 14-bit value (0–16383), assembled from CC 6 (MSB) and CC 38 (LSB).
    /// </summary>
    public ushort Value { get; }

    public MIDIMessageType MessageType => MIDIMessageType.RPN;

    /// <summary>
    /// Constructs an RPN message from a 14-bit parameter number and 14-bit value.
    /// </summary>
    public RPN(byte channel, ushort parameter, ushort value)
    {
        Channel   = channel;
        Parameter = (ushort)(parameter & 0x3FFF);
        Value     = (ushort)(value & 0x3FFF);
    }

    /// <summary>
    /// Constructs an RPN message from individual MSB/LSB bytes.
    /// </summary>
    public RPN(byte channel, byte paramMsb, byte paramLsb, byte dataMsb, byte dataLsb)
    {
        Channel   = channel;
        Parameter = (ushort)(((paramMsb & 0x7F) << 7) | (paramLsb & 0x7F));
        Value     = (ushort)(((dataMsb & 0x7F) << 7) | (dataLsb & 0x7F));
    }

    /// <summary>Parameter MSB (bits 13–7 of <see cref="Parameter"/>).</summary>
    public byte ParameterMsb => (byte)((Parameter >> 7) & 0x7F);

    /// <summary>Parameter LSB (bits 6–0 of <see cref="Parameter"/>).</summary>
    public byte ParameterLsb => (byte)(Parameter & 0x7F);

    /// <summary>Data Entry MSB (bits 13–7 of <see cref="Value"/>).</summary>
    public byte ValueMsb => (byte)((Value >> 7) & 0x7F);

    /// <summary>Data Entry LSB (bits 6–0 of <see cref="Value"/>).</summary>
    public byte ValueLsb => (byte)(Value & 0x7F);

    public PmError WriteTo(nint stream, int timestamp)
    {
        byte status = (byte)(0xB0 | (Channel & 0x0F));

        var err = Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(status, 101, ParameterMsb));
        if (err != PmError.NoError) return err;

        err = Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(status, 100, ParameterLsb));
        if (err != PmError.NoError) return err;

        err = Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(status, 6, ValueMsb));
        if (err != PmError.NoError) return err;

        return Native.Pm_WriteShort(stream, timestamp,
            PmEvent.CreateMessage(status, 38, ValueLsb));
    }
}
