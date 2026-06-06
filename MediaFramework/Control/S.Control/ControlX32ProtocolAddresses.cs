namespace S.Control;

internal static class ControlX32ProtocolAddresses
{
    public static bool IsX32OscProfile(string? profileId) =>
        string.Equals(profileId, "behringer.x32.osc", StringComparison.OrdinalIgnoreCase)
        || string.Equals(profileId, "behringer.xair.osc", StringComparison.OrdinalIgnoreCase);

    public static bool IsMaintenanceAddress(string? address) =>
        address is "/xremote" or "/subscribe" or "/meters";

    public static bool UsesProtocolMaintenance(ControlDeviceInstanceConfig device, ControlPeriodicOscSendConfig send) =>
        device.Protocol == ControlDeviceProtocol.Osc
        && device.IsEnabled
        && send.IsEnabled
        && IsX32OscProfile(device.ProfileId)
        && IsMaintenanceAddress(send.Address);
}
