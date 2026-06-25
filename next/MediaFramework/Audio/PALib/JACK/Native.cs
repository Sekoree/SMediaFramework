using System.Runtime.InteropServices;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.JACK;

internal static partial class Native
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static bool IsSupportedPlatform => OperatingSystem.IsLinux();

    [LibraryImport(LibraryName, EntryPoint = "PaJack_SetClientName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial PaError PaJack_SetClientName_Import(string name);

    public static PaError PaJack_SetClientName(string name)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        return PaJack_SetClientName_Import(name);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaJack_GetClientName")]
    private static partial PaError PaJack_GetClientName_Import(out nint clientName);

    public static PaError PaJack_GetClientName(out string? clientName)
    {
        if (!IsSupportedPlatform)
        {
            clientName = null;
            return PaError.paIncompatibleStreamHostApi;
        }

        var err = PaJack_GetClientName_Import(out var ptr);
        clientName = Marshal.PtrToStringUTF8(ptr);
        return err;
    }
}
