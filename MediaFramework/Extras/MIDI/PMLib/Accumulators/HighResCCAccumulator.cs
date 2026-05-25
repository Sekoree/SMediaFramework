using PMLib.MessageTypes;

namespace PMLib.Accumulators;

/// <summary>
/// Tracks pairs of coarse (CC 0–31) and fine (CC 32–63) Control Change messages
/// and emits combined 14-bit <see cref="ControlChange"/> values via <see cref="HighResChanged"/>.
/// </summary>
/// <remarks>
/// <para>
/// The MIDI specification achieves 14-bit CC resolution by pairing each coarse controller
/// (CC 0–31, carrying the MSB) with its fine counterpart (CC 32–63, carrying the LSB).
/// This accumulator stores the last received coarse value per channel/controller and fires
/// <see cref="HighResChanged"/> when the matching fine CC completes the pair.
/// </para>
/// <para>
/// <b>Threading:</b> Safe to call <see cref="Process"/> from any single thread (e.g. the
/// input device's poll thread). The event fires synchronously on the caller's thread.
/// </para>
/// <para>
/// <b>Usage:</b> Subscribe to <see cref="HighResChanged"/>, then feed every
/// <see cref="ControlChange"/> from <see cref="PMLib.Devices.MIDIInputDevice.MessageReceived"/>
/// into <see cref="Process"/>. Messages with controller ≥ 64 are ignored and return
/// <see langword="false"/>.
/// </para>
/// </remarks>
public sealed class HighResCCAccumulator
{
    // Flat array: [channel * 32 + controller] = last coarse MSB value, or null.
    private readonly byte?[] _coarse = new byte?[16 * 32];

    /// <summary>
    /// Raised when a complete 14-bit CC pair (coarse + fine) has been accumulated.
    /// The <see cref="ControlChange"/> will have <see cref="ControlChange.IsHighResolution"/>
    /// set to <see langword="true"/> and <see cref="ControlChange.Controller"/> set to the
    /// coarse controller number (0–31).
    /// </summary>
    public event Action<ControlChange>? HighResChanged;

    /// <summary>
    /// Processes an incoming <see cref="ControlChange"/> message.
    /// </summary>
    /// <param name="cc">The CC message to process.</param>
    /// <returns>
    /// <see langword="true"/> if the message was consumed as part of a 14-bit pair:
    /// either a coarse value (CC 0–31) stored for later, or a fine value (CC 32–63) that
    /// completed a pair and fired <see cref="HighResChanged"/>.
    /// <see langword="false"/> if the controller is outside the 14-bit range (≥ 64)
    /// or if a fine CC arrived without a stored coarse counterpart.
    /// </returns>
    public bool Process(ControlChange cc)
    {
        byte controller = cc.Controller;
        byte channel    = cc.Channel;

        if (controller < 32)
        {
            // Coarse CC (MSB) — store and wait for the fine counterpart.
            _coarse[channel * 32 + controller] = (byte)(cc.Value & 0x7F);
            return true;
        }

        if (controller < 64)
        {
            // Fine CC (LSB) — pair with stored coarse value.
            byte coarseController = (byte)(controller - 32);
            int  idx              = channel * 32 + coarseController;
            byte? coarse          = _coarse[idx];

            if (coarse.HasValue)
            {
                _coarse[idx] = null;
                HighResChanged?.Invoke(
                    ControlChange.FromCoarseFine(channel, coarseController, coarse.Value, (byte)(cc.Value & 0x7F)));
                return true;
            }

            // No coarse stored — not consumed as 14-bit.
            return false;
        }

        // CC 64+ is not part of the 14-bit range.
        return false;
    }

    /// <summary>
    /// Clears all stored coarse values across all channels.
    /// </summary>
    public void Reset() => Array.Clear(_coarse);
}
