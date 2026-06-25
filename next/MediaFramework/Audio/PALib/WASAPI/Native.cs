using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.WASAPI;

internal static partial class Native
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static readonly ILogger Logger = PALibLogging.GetLogger("PALib.WASAPI");
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetAudioClient")]
    private static partial PaError PaWasapi_GetAudioClient_Import(nint stream, out nint audioClient, int bOutput);

    public static PaError PaWasapi_GetAudioClient(nint stream, out nint audioClient, int bOutput)
    {
        if (!IsSupportedPlatform)
        {
            audioClient = nint.Zero;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaWasapi_GetAudioClient_Import(stream, out audioClient, bOutput);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_UpdateDeviceList")]
    private static partial PaError PaWasapi_UpdateDeviceList_Import();
    public static PaError PaWasapi_UpdateDeviceList()
        => !IsSupportedPlatform ? PaError.paIncompatibleStreamHostApi : PaWasapi_UpdateDeviceList_Import();

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetDeviceCurrentFormat")]
    private static partial int PaWasapi_GetDeviceCurrentFormat_Import(nint stream, nint format, uint formatSize, int bOutput);
    public static int PaWasapi_GetDeviceCurrentFormat(nint stream, nint format, uint formatSize, int bOutput)
        => !IsSupportedPlatform ? (int)PaError.paIncompatibleStreamHostApi : PaWasapi_GetDeviceCurrentFormat_Import(stream, format, formatSize, bOutput);

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetDeviceDefaultFormat")]
    private static partial int PaWasapi_GetDeviceDefaultFormat_Import(nint format, uint formatSize, int device);
    public static int PaWasapi_GetDeviceDefaultFormat(nint format, uint formatSize, int device)
        => !IsSupportedPlatform ? (int)PaError.paIncompatibleStreamHostApi : PaWasapi_GetDeviceDefaultFormat_Import(format, formatSize, device);

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetDeviceMixFormat")]
    private static partial int PaWasapi_GetDeviceMixFormat_Import(nint format, uint formatSize, int device);
    public static int PaWasapi_GetDeviceMixFormat(nint format, uint formatSize, int device)
        => !IsSupportedPlatform ? (int)PaError.paIncompatibleStreamHostApi : PaWasapi_GetDeviceMixFormat_Import(format, formatSize, device);

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetDeviceRole")]
    private static partial int PaWasapi_GetDeviceRole_Import(int device);
    public static int PaWasapi_GetDeviceRole(int device)
        => !IsSupportedPlatform ? (int)PaError.paIncompatibleStreamHostApi : PaWasapi_GetDeviceRole_Import(device);

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetIMMDevice")]
    private static partial PaError PaWasapi_GetIMMDevice_Import(int device, out nint immDevice);
    public static PaError PaWasapi_GetIMMDevice(int device, out nint immDevice)
    {
        if (!IsSupportedPlatform)
        {
            immDevice = nint.Zero;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaWasapi_GetIMMDevice_Import(device, out immDevice);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_ThreadPriorityBoost")]
    private static partial PaError PaWasapi_ThreadPriorityBoost_Import(out nint task, PaWasapiThreadPriority priorityClass);
    public static PaError PaWasapi_ThreadPriorityBoost(out nint task, PaWasapiThreadPriority priorityClass)
    {
        if (!IsSupportedPlatform)
        {
            task = nint.Zero;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaWasapi_ThreadPriorityBoost_Import(out task, priorityClass);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_ThreadPriorityRevert")]
    private static partial PaError PaWasapi_ThreadPriorityRevert_Import(nint task);
    public static PaError PaWasapi_ThreadPriorityRevert(nint task)
        => !IsSupportedPlatform ? PaError.paIncompatibleStreamHostApi : PaWasapi_ThreadPriorityRevert_Import(task);

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetFramesPerHostBuffer")]
    private static partial PaError PaWasapi_GetFramesPerHostBuffer_Import(nint stream, out uint inputFrames, out uint outputFrames);
    public static PaError PaWasapi_GetFramesPerHostBuffer(nint stream, out uint inputFrames, out uint outputFrames)
    {
        if (!IsSupportedPlatform)
        {
            inputFrames = 0;
            outputFrames = 0;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaWasapi_GetFramesPerHostBuffer_Import(stream, out inputFrames, out outputFrames);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetJackCount")]
    private static partial PaError PaWasapi_GetJackCount_Import(int device, out int jackCount);
    public static PaError PaWasapi_GetJackCount(int device, out int jackCount)
    {
        if (!IsSupportedPlatform)
        {
            jackCount = 0;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaWasapi_GetJackCount_Import(device, out jackCount);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_GetJackDescription")]
    private static partial PaError PaWasapi_GetJackDescription_Import(int device, int jackIndex, out PaWasapiJackDescription jackDescription);
    public static PaError PaWasapi_GetJackDescription(int device, int jackIndex, out PaWasapiJackDescription jackDescription)
    {
        if (!IsSupportedPlatform)
        {
            jackDescription = default;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaWasapi_GetJackDescription_Import(device, jackIndex, out jackDescription);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWasapi_SetStreamStateHandler")]
    private static partial PaError PaWasapi_SetStreamStateHandler_Import(nint stream, nint fnStateHandler, nint userData);

    public static PaError PaWasapi_SetStreamStateHandler(nint stream, nint fnStateHandler, nint userData)
    {
        if (Logger.IsEnabled(LogLevel.Trace))
            Logger.LogTrace("{Method}({Stream}, {FnStateHandler}, {UserData})",
                nameof(PaWasapi_SetStreamStateHandler),
                PALibLogging.PtrMeta(stream), PALibLogging.PtrMeta(fnStateHandler), PALibLogging.PtrMeta(userData));

        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        return PaWasapi_SetStreamStateHandler_Import(stream, fnStateHandler, userData);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWasapiWinrt_SetDefaultDeviceId")]
    private static partial PaError PaWasapiWinrt_SetDefaultDeviceId_Import(nint id, int bOutput);
    public static PaError PaWasapiWinrt_SetDefaultDeviceId(nint id, int bOutput)
        => !IsSupportedPlatform ? PaError.paIncompatibleStreamHostApi : PaWasapiWinrt_SetDefaultDeviceId_Import(id, bOutput);

    [LibraryImport(LibraryName, EntryPoint = "PaWasapiWinrt_PopulateDeviceList")]
    private static partial PaError PaWasapiWinrt_PopulateDeviceList_Import(nint ids, nint names, nint roles, uint count, int bOutput);
    public static PaError PaWasapiWinrt_PopulateDeviceList(nint ids, nint names, nint roles, uint count, int bOutput)
        => !IsSupportedPlatform ? PaError.paIncompatibleStreamHostApi : PaWasapiWinrt_PopulateDeviceList_Import(ids, names, roles, count, bOutput);
}
