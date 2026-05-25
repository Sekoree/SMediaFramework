using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.ASIO;

internal static partial class Native
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static readonly ILogger Logger = PALibLogging.GetLogger("PALib.ASIO");
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    [LibraryImport(LibraryName, EntryPoint = "PaAsio_GetAvailableBufferSizes")]
    private static partial PaError PaAsio_GetAvailableBufferSizes_Import(
        int device,
        out nint minBufferSizeFrames,
        out nint maxBufferSizeFrames,
        out nint preferredBufferSizeFrames,
        out nint granularity);

    public static PaError PaAsio_GetAvailableBufferSizes(
        int device,
        out nint minBufferSizeFrames,
        out nint maxBufferSizeFrames,
        out nint preferredBufferSizeFrames,
        out nint granularity)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Device})", nameof(PaAsio_GetAvailableBufferSizes), device);
        if (!IsSupportedPlatform)
        {
            minBufferSizeFrames = 0;
            maxBufferSizeFrames = 0;
            preferredBufferSizeFrames = 0;
            granularity = 0;
            return PaError.paIncompatibleStreamHostApi;
        }

        return PaAsio_GetAvailableBufferSizes_Import(device, out minBufferSizeFrames, out maxBufferSizeFrames, out preferredBufferSizeFrames, out granularity);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAsio_ShowControlPanel")]
    private static partial PaError PaAsio_ShowControlPanel_Import(int device, nint systemSpecific);
    public static PaError PaAsio_ShowControlPanel(int device, nint systemSpecific)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Device}, {SystemSpecific})", nameof(PaAsio_ShowControlPanel), device, PALibLogging.PtrMeta(systemSpecific));
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        return PaAsio_ShowControlPanel_Import(device, systemSpecific);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAsio_GetInputChannelName")]
    private static partial PaError PaAsio_GetInputChannelName_Import(int device, int channelIndex, out nint channelName);
    public static PaError PaAsio_GetInputChannelName(int device, int channelIndex, out string? channelName)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Device}, {ChannelIndex})", nameof(PaAsio_GetInputChannelName), device, channelIndex);
        if (!IsSupportedPlatform)
        {
            channelName = null;
            return PaError.paIncompatibleStreamHostApi;
        }

        var err = PaAsio_GetInputChannelName_Import(device, channelIndex, out var channelPtr);
        channelName = Marshal.PtrToStringUTF8(channelPtr);
        return err;
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAsio_GetOutputChannelName")]
    private static partial PaError PaAsio_GetOutputChannelName_Import(int device, int channelIndex, out nint channelName);
    public static PaError PaAsio_GetOutputChannelName(int device, int channelIndex, out string? channelName)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Device}, {ChannelIndex})", nameof(PaAsio_GetOutputChannelName), device, channelIndex);
        if (!IsSupportedPlatform)
        {
            channelName = null;
            return PaError.paIncompatibleStreamHostApi;
        }

        var err = PaAsio_GetOutputChannelName_Import(device, channelIndex, out var channelPtr);
        channelName = Marshal.PtrToStringUTF8(channelPtr);
        return err;
    }

    [LibraryImport(LibraryName, EntryPoint = "PaAsio_SetStreamSampleRate")]
    private static partial PaError PaAsio_SetStreamSampleRate_Import(nint stream, double sampleRate);
    public static PaError PaAsio_SetStreamSampleRate(nint stream, double sampleRate)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        return PaAsio_SetStreamSampleRate_Import(stream, sampleRate);
    }
}
