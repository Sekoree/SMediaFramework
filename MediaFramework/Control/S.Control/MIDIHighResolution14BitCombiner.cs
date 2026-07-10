using PMLib.MessageTypes;

namespace S.Control;

/// <summary>
/// Reassembles 14-bit (high-resolution) MIDI Control Change pairs on input. PortMIDI delivers each CC
/// byte individually, so a 14-bit fader arrives as a coarse CC (controller 0–31, MSB) followed by a fine
/// CC (controller +32, LSB). For the coarse controllers a device profile marks as 14-bit, this caches the
/// MSB and, on the matching LSB, emits one combined <see cref="ControlChange"/> (value
/// <c>(MSB &lt;&lt; 7) | LSB</c>, 0–16383, <see cref="ControlChange.IsHighResolution"/> <see langword="true"/>).
/// </summary>
/// <remarks>
/// Opt-in and precise: only the configured coarse controllers are paired. Every other message - including
/// 7-bit CCs on unconfigured controllers, notes, etc. - passes through untouched, so this never disturbs a
/// device whose CC 0–31 carry ordinary 7-bit data (e.g. Bank Select). State is keyed per channel + coarse
/// controller; input from one port is delivered single-threaded, so no locking is needed.
/// </remarks>
internal sealed class MIDIHighResolution14BitCombiner
{
    private readonly HashSet<int> _coarseControllers; // CC numbers 0–31 to treat as 14-bit MSBs
    private readonly Dictionary<(int Channel, int Controller), int> _coarseValues = new();

    public MIDIHighResolution14BitCombiner(IEnumerable<int> coarseControllers)
    {
        ArgumentNullException.ThrowIfNull(coarseControllers);
        _coarseControllers = coarseControllers.Where(c => c is >= 0 and <= 31).ToHashSet();
    }

    /// <summary>True when at least one controller is configured for 14-bit pairing.</summary>
    public bool IsEnabled => _coarseControllers.Count > 0;

    /// <summary>
    /// Returns the message to dispatch, or <see langword="null"/> to swallow it - used for a coarse (MSB)
    /// byte that is being held until its fine (LSB) partner completes the 14-bit value. Non-CC messages and
    /// CCs on unconfigured controllers are returned unchanged.
    /// </summary>
    public IMIDIMessage? Process(IMIDIMessage message)
    {
        if (_coarseControllers.Count == 0 || message is not ControlChange cc || cc.IsHighResolution)
            return message;

        int controller = cc.Controller;

        // Coarse (MSB): remember it; the matching fine CC will emit the combined value. Hold it back so the
        // script/monitor sees one clean 14-bit event instead of a half-resolution coarse update.
        if (controller is >= 0 and <= 31 && _coarseControllers.Contains(controller))
        {
            _coarseValues[(cc.Channel, controller)] = cc.Value & 0x7F;
            return null;
        }

        // Fine (LSB): combine with the latest coarse for this pair (0 until the first coarse arrives).
        if (controller is >= 32 and <= 63 && _coarseControllers.Contains(controller - 32))
        {
            var coarseController = controller - 32;
            var coarse = _coarseValues.TryGetValue((cc.Channel, coarseController), out var stored) ? stored : 0;
            return ControlChange.FromCoarseFine(cc.Channel, (byte)coarseController, (byte)coarse, (byte)(cc.Value & 0x7F));
        }

        return message;
    }
}
