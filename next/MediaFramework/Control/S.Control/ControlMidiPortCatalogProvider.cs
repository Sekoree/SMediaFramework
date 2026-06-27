namespace S.Control;

public static class ControlMidiPortCatalogProvider
{
    public static ControlMidiPortCatalog? TryEnumerate()
    {
        try
        {
            using var lease = ControlMidiLibraryLease.Acquire();
            var provider = RealControlMidiDeviceProvider.Instance;
            provider.EnsureInitialized();
            return new ControlMidiPortCatalog(provider.GetInputDevices(), provider.GetOutputDevices());
        }
        catch
        {
            return null;
        }
    }
}
