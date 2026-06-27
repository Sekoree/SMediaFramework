using PMLib.MessageTypes;

namespace PMLib.Accumulators;

/// <summary>
/// Tracks the multi-CC sequences that form NRPN and RPN messages and emits
/// complete <see cref="NRPN"/> or <see cref="RPN"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>NRPN sequence:</b> CC 99 (param MSB) → CC 98 (param LSB) → CC 6 (data MSB) → CC 38 (data LSB).
/// </para>
/// <para>
/// <b>RPN sequence:</b> CC 101 (param MSB) → CC 100 (param LSB) → CC 6 (data MSB) → CC 38 (data LSB).
/// </para>
/// <para>
/// The accumulator maintains per-channel state. When CC 6 (Data Entry MSB) arrives, it is
/// paired with whichever parameter address (NRPN or RPN) was most recently selected on that
/// channel. An optional CC 38 (Data Entry LSB) refines the value to 14-bit; if the next CC
/// on the channel is <em>not</em> CC 38, the value is emitted as 7-bit (LSB = 0) and the new
/// CC is processed normally.
/// </para>
/// <para>
/// <b>Threading:</b> Same as <see cref="HighResCCAccumulator"/> — call from a single thread.
/// </para>
/// </remarks>
public sealed class NRPNAccumulator
{
    private readonly ChannelState[] _channels = new ChannelState[16];

    /// <summary>Raised when a complete NRPN message has been accumulated.</summary>
    public event Action<NRPN>? NRPNReceived;

    /// <summary>Raised when a complete RPN message has been accumulated.</summary>
    public event Action<RPN>? RPNReceived;

    /// <summary>
    /// Processes an incoming <see cref="ControlChange"/> message.
    /// </summary>
    /// <param name="cc">The CC message to process.</param>
    /// <returns>
    /// <see langword="true"/> if the message was consumed as part of an NRPN/RPN sequence;
    /// <see langword="false"/> if the CC is unrelated to NRPN/RPN tracking.
    /// </returns>
    public bool Process(ControlChange cc)
    {
        byte ch  = cc.Channel;
        byte ctl = cc.Controller;
        byte val = (byte)(cc.Value & 0x7F);

        ref var state = ref _channels[ch];

        switch (ctl)
        {
            // ── NRPN parameter select ─────────────────────────────────────
            case 99: // NRPN MSB
                FlushPendingDataMsb(ref state, ch);
                state.ParamMsb = val;
                state.IsNRPN   = true;
                state.ParamComplete = false;
                return true;

            case 98: // NRPN LSB
                FlushPendingDataMsb(ref state, ch);
                state.ParamLsb = val;
                if (state.IsNRPN && state.ParamMsb.HasValue)
                    state.ParamComplete = true;
                return true;

            // ── RPN parameter select ──────────────────────────────────────
            case 101: // RPN MSB
                FlushPendingDataMsb(ref state, ch);
                state.ParamMsb = val;
                state.IsNRPN   = false;
                state.ParamComplete = false;
                return true;

            case 100: // RPN LSB
                FlushPendingDataMsb(ref state, ch);
                state.ParamLsb = val;
                if (!state.IsNRPN && state.ParamMsb.HasValue)
                    state.ParamComplete = true;
                return true;

            // ── Data entry ────────────────────────────────────────────────
            case 6: // Data Entry MSB
                if (!state.ParamComplete) return false;
                FlushPendingDataMsb(ref state, ch);
                state.DataMsb = val;
                return true;

            case 38: // Data Entry LSB
                if (!state.ParamComplete || !state.DataMsb.HasValue) return false;
                Emit(ref state, ch, val);
                return true;

            default:
                // Any other CC while we have a pending Data MSB → flush as 7-bit value.
                FlushPendingDataMsb(ref state, ch);
                return false;
        }
    }

    /// <summary>
    /// If a Data Entry MSB is pending without a LSB, emit it now with LSB = 0.
    /// </summary>
    private void FlushPendingDataMsb(ref ChannelState state, byte channel)
    {
        if (state.DataMsb.HasValue && state.ParamComplete)
        {
            Emit(ref state, channel, dataLsb: 0);
        }
        state.DataMsb = null;
    }

    private void Emit(ref ChannelState state, byte channel, byte dataLsb)
    {
        byte paramMsb = state.ParamMsb!.Value;
        byte paramLsb = state.ParamLsb ?? 0;
        byte dataMsb  = state.DataMsb!.Value;

        if (state.IsNRPN)
            NRPNReceived?.Invoke(new NRPN(channel, paramMsb, paramLsb, dataMsb, dataLsb));
        else
            RPNReceived?.Invoke(new RPN(channel, paramMsb, paramLsb, dataMsb, dataLsb));

        state.DataMsb = null;
    }

    /// <summary>
    /// Resets all per-channel accumulator state.
    /// </summary>
    public void Reset() => Array.Clear(_channels);

    private struct ChannelState
    {
        /// <summary>Last received parameter MSB (CC 99 for NRPN, CC 101 for RPN).</summary>
        public byte? ParamMsb;
        /// <summary>Last received parameter LSB (CC 98 for NRPN, CC 100 for RPN).</summary>
        public byte? ParamLsb;
        /// <summary>Pending Data Entry MSB (CC 6), awaiting optional LSB (CC 38).</summary>
        public byte? DataMsb;
        /// <summary><see langword="true"/> if the current parameter address is NRPN; <see langword="false"/> for RPN.</summary>
        public bool IsNRPN;
        /// <summary><see langword="true"/> when both ParamMsb and ParamLsb have been received.</summary>
        public bool ParamComplete;
    }
}
