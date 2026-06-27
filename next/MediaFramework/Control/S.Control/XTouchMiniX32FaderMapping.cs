namespace S.Control;

public sealed record XTouchMiniX32FaderUpdate(
    int Channel,
    int MidiController,
    int MidiValue,
    int DeltaSteps,
    string OscAddress,
    double FaderValue);

public static class XTouchMiniX32FaderMapping
{
    public const int FirstEncoderController = 16;
    public const int LastEncoderController = 23;
    public const double DefaultFaderValue = 0.75;
    public const double FaderStep = 1.0 / 1023.0;

    public static bool TryApplyEncoder(
        int midiController,
        int midiValue,
        double? currentFaderValue,
        out XTouchMiniX32FaderUpdate update)
    {
        update = default!;

        if (!TryGetChannel(midiController, out var channel))
            return false;
        if (!TryGetDeltaSteps(midiValue, out var deltaSteps))
            return false;

        var current = currentFaderValue ?? DefaultFaderValue;
        var next = ApplyDelta(current, deltaSteps);
        update = new XTouchMiniX32FaderUpdate(
            channel,
            midiController,
            midiValue,
            deltaSteps,
            X32Presets.ChannelFaderAddress(channel),
            next);
        return true;
    }

    public static bool TryGetChannel(int midiController, out int channel)
    {
        if (midiController is < FirstEncoderController or > LastEncoderController)
        {
            channel = 0;
            return false;
        }

        channel = midiController - FirstEncoderController + 1;
        return true;
    }

    public static bool TryGetDeltaSteps(int midiValue, out int deltaSteps)
    {
        if (midiValue is >= 1 and <= 10)
        {
            deltaSteps = midiValue;
            return true;
        }

        if (midiValue is >= 65 and <= 72)
        {
            deltaSteps = -(midiValue - 64);
            return true;
        }

        deltaSteps = 0;
        return false;
    }

    public static double ApplyDelta(double currentFaderValue, int deltaSteps) =>
        Math.Clamp(currentFaderValue + deltaSteps * FaderStep, 0.0, 1.0);
}
