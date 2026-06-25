using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.WMME;

internal static partial class Native
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static readonly ILogger Logger = PALibLogging.GetLogger("PALib.WMME");
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    [LibraryImport(LibraryName, EntryPoint = "PaWinMME_GetStreamInputHandleCount")]
    private static partial int PaWinMME_GetStreamInputHandleCount_Import(nint stream);

    public static int PaWinMME_GetStreamInputHandleCount(nint stream)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream})", nameof(PaWinMME_GetStreamInputHandleCount), PALibLogging.PtrMeta(stream));
        if (!IsSupportedPlatform)
            return (int)PaError.paIncompatibleStreamHostApi;

        return PaWinMME_GetStreamInputHandleCount_Import(stream);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWinMME_GetStreamInputHandle")]
    private static partial nint PaWinMME_GetStreamInputHandle_Import(nint stream, int handleIndex);

    public static nint PaWinMME_GetStreamInputHandle(nint stream, int handleIndex)
        => !IsSupportedPlatform ? nint.Zero : PaWinMME_GetStreamInputHandle_Import(stream, handleIndex);

    [LibraryImport(LibraryName, EntryPoint = "PaWinMME_GetStreamOutputHandleCount")]
    private static partial int PaWinMME_GetStreamOutputHandleCount_Import(nint stream);

    public static int PaWinMME_GetStreamOutputHandleCount(nint stream)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream})", nameof(PaWinMME_GetStreamOutputHandleCount), PALibLogging.PtrMeta(stream));
        if (!IsSupportedPlatform)
            return (int)PaError.paIncompatibleStreamHostApi;

        return PaWinMME_GetStreamOutputHandleCount_Import(stream);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWinMME_GetStreamOutputHandle")]
    private static partial nint PaWinMME_GetStreamOutputHandle_Import(nint stream, int handleIndex);

    public static nint PaWinMME_GetStreamOutputHandle(nint stream, int handleIndex)
        => !IsSupportedPlatform ? nint.Zero : PaWinMME_GetStreamOutputHandle_Import(stream, handleIndex);
}
