using Microsoft.Extensions.Logging;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.WDMKS;

internal static class Native
{
    private const string Category = "PALib.WDMKS";
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public static PaError TraceInfo(in PaWinWDMKSInfo info)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        var logger = PALibLogging.GetLogger(Category);
        if (logger.IsEnabled(LogLevel.Trace))
            logger.LogTrace("{Method}(size={Size}, hostApiType={HostApiType}, version={Version}, flags={Flags}, noOfPackets={NoOfPackets}, channelMask={ChannelMask})",
                nameof(TraceInfo), info.size, info.hostApiType, info.version, info.flags, info.noOfPackets, info.channelMask);

        return PaError.paNoError;
    }
}
