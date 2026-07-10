namespace HaPlay.Tests;

// Loads built-in device profiles from their shipped JSON (embedded) - the single source of truth now that the
// hardcoded BuiltInControlDeviceProfileFactory is retired. Tests assert on the same data the runtime loads.
internal static class TestProfiles
{
    public const string X32 = "behringer.x32.osc";
    public const string XAir = "behringer.xair.osc";
    public const string XTouchMini = "behringer.xtouch-mini.mc";
    public const string Bcf2000 = "behringer.bcf2000";

    public static ControlDeviceProfile ById(string id) =>
        BuiltInControlDeviceProfileRepository.Instance.FindById(id)
            ?? throw new InvalidOperationException($"Built-in profile not found: {id}");
}
