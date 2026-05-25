using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.ALSA;

internal static partial class Native
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static readonly ILogger Logger = PALibLogging.GetLogger("PALib.ALSA");

    private static bool IsSupportedPlatform => OperatingSystem.IsLinux();

    [LibraryImport(LibraryName, EntryPoint = "PaAlsa_InitializeStreamInfo")]
    private static partial void PaAlsa_InitializeStreamInfo_Import(ref PaAlsaStreamInfo info);

    public static void PaAlsa_InitializeStreamInfo(ref PaAlsaStreamInfo info)
    {
        if (!IsSupportedPlatform)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("PaAlsa_InitializeStreamInfo ignored on unsupported platform.");
            return;
        }

        PaAlsa_InitializeStreamInfo_Import(ref info);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAlsa_EnableRealtimeScheduling")]
    private static partial void PaAlsa_EnableRealtimeScheduling_Import(nint stream, int enable);

    public static void PaAlsa_EnableRealtimeScheduling(nint stream, int enable)
    {
        if (!IsSupportedPlatform)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("PaAlsa_EnableRealtimeScheduling ignored on unsupported platform.");
            return;
        }

        PaAlsa_EnableRealtimeScheduling_Import(stream, enable);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAlsa_GetStreamInputCard")]
    private static partial PaError PaAlsa_GetStreamInputCard_Import(nint stream, out int card);

    public static PaError PaAlsa_GetStreamInputCard(nint stream, out int card)
    {
        if (!IsSupportedPlatform)
        {
            card = 0;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaAlsa_GetStreamInputCard_Import(stream, out card);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAlsa_GetStreamOutputCard")]
    private static partial PaError PaAlsa_GetStreamOutputCard_Import(nint stream, out int card);

    public static PaError PaAlsa_GetStreamOutputCard(nint stream, out int card)
    {
        if (!IsSupportedPlatform)
        {
            card = 0;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaAlsa_GetStreamOutputCard_Import(stream, out card);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAlsa_SetNumPeriods")]
    private static partial PaError PaAlsa_SetNumPeriods_Import(int numPeriods);

    public static PaError PaAlsa_SetNumPeriods(int numPeriods)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        return PaAlsa_SetNumPeriods_Import(numPeriods);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAlsa_SetRetriesBusy")]
    private static partial PaError PaAlsa_SetRetriesBusy_Import(int retries);

    public static PaError PaAlsa_SetRetriesBusy(int retries)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        return PaAlsa_SetRetriesBusy_Import(retries);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAlsa_SetLibraryPathName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void PaAlsa_SetLibraryPathName_Import(string pathName);

    public static void PaAlsa_SetLibraryPathName(string pathName)
    {
        if (!IsSupportedPlatform)
        {
            if (Logger.IsEnabled(LogLevel.Debug))
                Logger.LogDebug("PaAlsa_SetLibraryPathName ignored on unsupported platform.");
            return;
        }

        PaAlsa_SetLibraryPathName_Import(pathName);
    }
}
