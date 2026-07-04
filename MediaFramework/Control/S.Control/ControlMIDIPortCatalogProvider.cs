namespace S.Control;

public static class ControlMIDIPortCatalogProvider
{
    public static ControlMIDIPortCatalog? TryEnumerate()
    {
        try
        {
            using var lease = ControlMIDILibraryLease.Acquire();
            var provider = RealControlMIDIDeviceProvider.Instance;
            provider.EnsureInitialized();
            return new ControlMIDIPortCatalog(provider.GetInputDevices(), provider.GetOutputDevices());
        }
        catch
        {
            return null;
        }
    }
}
