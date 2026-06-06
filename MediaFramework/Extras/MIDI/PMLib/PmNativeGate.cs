namespace PMLib;

/// <summary>
/// Serializes all PortMidi native calls. PortMidi is not thread-safe; concurrent
/// <c>Pm_*</c> entry points from poll threads, output writers, and shutdown must not overlap.
/// Do not hold this gate while invoking managed event handlers.
/// </summary>
internal static class PmNativeGate
{
    internal static readonly Lock SyncRoot = new();
}
