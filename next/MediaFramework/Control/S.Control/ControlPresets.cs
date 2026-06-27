
namespace S.Control;

public static class X32Presets
{
    public const int DefaultPort = 10023;
    public static readonly TimeSpan DefaultRenewInterval = TimeSpan.FromSeconds(8);

    public static X32EndpointPreset CreateEndpointPreset(
        string host = "127.0.0.1",
        int port = DefaultPort,
        int? localPort = null) =>
        new(
            Host: host,
            Port: port,
            LocalPort: localPort,
            XRemoteRenewInterval: DefaultRenewInterval,
            SubscriptionRenewInterval: DefaultRenewInterval);

    public static string ChannelFaderAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, 32):00}/mix/fader";

    public static string ChannelMuteAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, 32):00}/mix/on";

    public static string ChannelPanAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, 32):00}/mix/pan";

    public static string ChannelSoloStatusAddress(int channel) =>
        $"/-stat/solosw/{Math.Clamp(channel, 1, 32):00}";

    public static string BusFaderAddress(int bus) =>
        $"/bus/{Math.Clamp(bus, 1, 16):00}/mix/fader";

    public static string BusMuteAddress(int bus) =>
        $"/bus/{Math.Clamp(bus, 1, 16):00}/mix/on";

    public static string MatrixFaderAddress(int matrix) =>
        $"/mtx/{Math.Clamp(matrix, 1, 6):00}/mix/fader";

    public static string MatrixMuteAddress(int matrix) =>
        $"/mtx/{Math.Clamp(matrix, 1, 6):00}/mix/on";

    public static string DcaFaderAddress(int dca) =>
        $"/dca/{Math.Clamp(dca, 1, 8)}/fader";

    public static string DcaMuteAddress(int dca) =>
        $"/dca/{Math.Clamp(dca, 1, 8)}/on";

    public static string MainStereoFaderAddress() => "/main/st/mix/fader";

    public static string MainStereoMuteAddress() => "/main/st/mix/on";

    public static IReadOnlyList<X32ParameterPreset> ChannelStripPresets(int channel)
    {
        var safe = Math.Clamp(channel, 1, 32);
        return
        [
            new($"Ch {safe:00} Fader", ChannelFaderAddress(safe), X32ParameterValueKind.NormalizedFloat),
            new($"Ch {safe:00} Mute", ChannelMuteAddress(safe), X32ParameterValueKind.BooleanInt),
            new($"Ch {safe:00} Pan", ChannelPanAddress(safe), X32ParameterValueKind.NormalizedFloat),
            new($"Ch {safe:00} Solo", ChannelSoloStatusAddress(safe), X32ParameterValueKind.BooleanInt, IsReadOnly: true),
        ];
    }

    public static IReadOnlyList<X32ParameterPreset> DcaPresets(int dca)
    {
        var safe = Math.Clamp(dca, 1, 8);
        return
        [
            new($"DCA {safe} Fader", DcaFaderAddress(safe), X32ParameterValueKind.NormalizedFloat),
            new($"DCA {safe} Mute", DcaMuteAddress(safe), X32ParameterValueKind.BooleanInt),
        ];
    }

    public static IReadOnlyList<X32ParameterPreset> MainStereoPresets() =>
    [
        new("Main Stereo Fader", MainStereoFaderAddress(), X32ParameterValueKind.NormalizedFloat),
        new("Main Stereo Mute", MainStereoMuteAddress(), X32ParameterValueKind.BooleanInt),
    ];

    public static IReadOnlyList<X32ParameterPreset> BusPresets(int bus)
    {
        var safe = Math.Clamp(bus, 1, 16);
        return
        [
            new($"Bus {safe:00} Fader", BusFaderAddress(safe), X32ParameterValueKind.NormalizedFloat),
            new($"Bus {safe:00} Mute", BusMuteAddress(safe), X32ParameterValueKind.BooleanInt),
        ];
    }

    public static IReadOnlyList<X32ParameterPreset> MatrixPresets(int matrix)
    {
        var safe = Math.Clamp(matrix, 1, 6);
        return
        [
            new($"Matrix {safe:00} Fader", MatrixFaderAddress(safe), X32ParameterValueKind.NormalizedFloat),
            new($"Matrix {safe:00} Mute", MatrixMuteAddress(safe), X32ParameterValueKind.BooleanInt),
        ];
    }
}

/// <summary>
/// OSC address builders for Behringer X-Air / Midas M-Air mixers. Channels share the X32's
/// <c>/ch/NN/mix/...</c> layout, but buses and DCAs are single-digit, and the main is <c>/lr</c>.
/// </summary>
public static class XAirPresets
{
    public const int DefaultPort = 10024;
    public const int ChannelCount = 16; // XR16/XR18 (XR12 uses the first 12)
    public const int BusCount = 6;
    public const int DcaCount = 4;

    public static readonly TimeSpan DefaultRenewInterval = TimeSpan.FromSeconds(8);

    public static string ChannelFaderAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, ChannelCount):00}/mix/fader";

    public static string ChannelMuteAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, ChannelCount):00}/mix/on";

    public static string ChannelPanAddress(int channel) =>
        $"/ch/{Math.Clamp(channel, 1, ChannelCount):00}/mix/pan";

    public static string ChannelSoloStatusAddress(int channel) =>
        $"/-stat/solosw/{Math.Clamp(channel, 1, ChannelCount):00}";

    public static string BusFaderAddress(int bus) =>
        $"/bus/{Math.Clamp(bus, 1, BusCount)}/mix/fader";

    public static string BusMuteAddress(int bus) =>
        $"/bus/{Math.Clamp(bus, 1, BusCount)}/mix/on";

    public static string DcaFaderAddress(int dca) =>
        $"/dca/{Math.Clamp(dca, 1, DcaCount)}/fader";

    public static string DcaMuteAddress(int dca) =>
        $"/dca/{Math.Clamp(dca, 1, DcaCount)}/on";

    public static string MainLrFaderAddress() => "/lr/mix/fader";

    public static string MainLrMuteAddress() => "/lr/mix/on";
}

public sealed record X32EndpointPreset(
    string Host,
    int Port,
    int? LocalPort,
    TimeSpan XRemoteRenewInterval,
    TimeSpan SubscriptionRenewInterval);

public sealed record X32ParameterPreset(
    string DisplayName,
    string Address,
    X32ParameterValueKind ValueKind,
    bool IsReadOnly = false);

public enum X32ParameterValueKind
{
    NormalizedFloat,
    BooleanInt,
    Text,
}

public static class X32Fader
{
    public const double NegativeInfinityDb = double.NegativeInfinity;
    public const double PracticalFloorDb = -90.0;
    public const double UnityDb = 0.0;
    public const double MaxDb = 10.0;

    public static double FromNormalized(double value)
    {
        var f = Math.Clamp(value, 0.0, 1.0);
        if (f <= 0)
            return NegativeInfinityDb;
        if (f >= 0.5)
            return f * 40.0 - 30.0;
        if (f >= 0.25)
            return f * 80.0 - 50.0;
        if (f >= 0.0625)
            return f * 160.0 - 70.0;
        return f * 480.0 - 90.0;
    }

    public static double ToNormalized(double db)
    {
        if (double.IsNegativeInfinity(db) || db <= PracticalFloorDb)
            return 0.0;

        var d = Math.Clamp(db, PracticalFloorDb, MaxDb);
        if (d >= -10.0)
            return (d + 30.0) / 40.0;
        if (d >= -30.0)
            return (d + 50.0) / 80.0;
        if (d >= -60.0)
            return (d + 70.0) / 160.0;
        return (d + 90.0) / 480.0;
    }

    public static double ToKnownStep(double value) =>
        Math.Clamp(Math.Round(Math.Clamp(value, 0.0, 1.0) * 1023.0) / 1023.0, 0.0, 1.0);
}
