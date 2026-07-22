namespace HaPlay.Playback;

/// <summary>
/// Stable ShowSession device identifiers for app-owned output-line runtimes. Backend device ids
/// identify hardware, not the configured runtime that already owns it, so they must not be used for
/// borrowed carrier routes.
/// </summary>
internal static class OutputAudioRouteDeviceIds
{
    private const string PortAudioPrefix = "portaudio-line:";
    private const string NdiPrefix = "ndi-audio:";
    private const string EncodePrefix = "file-audio:";

    public static string PortAudio(Guid lineId) => $"{PortAudioPrefix}{lineId}";
    public static string Ndi(Guid lineId) => $"{NdiPrefix}{lineId}";
    public static string Encode(Guid lineId) => $"{EncodePrefix}{lineId}";

    public static bool TryParsePortAudio(string deviceId, out Guid lineId) =>
        TryParse(deviceId, PortAudioPrefix, out lineId);

    public static bool TryParseEncode(string deviceId, out Guid lineId) =>
        TryParse(deviceId, EncodePrefix, out lineId);

    public static bool TryParseLineId(string deviceId, out Guid lineId) =>
        TryParse(deviceId, PortAudioPrefix, out lineId)
        || TryParse(deviceId, NdiPrefix, out lineId)
        || TryParse(deviceId, EncodePrefix, out lineId);

    private static bool TryParse(string value, string prefix, out Guid lineId)
    {
        lineId = default;
        return value.StartsWith(prefix, StringComparison.Ordinal)
               && Guid.TryParse(value[prefix.Length..], out lineId);
    }
}
