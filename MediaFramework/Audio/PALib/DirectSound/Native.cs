using Microsoft.Extensions.Logging;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.DirectSound;

internal static class Native
{
    private const string Category = "PALib.DirectSound";
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    public static PaError TraceStreamInfo(in PaWinDirectSoundStreamInfo info)
    {
        if (!IsSupportedPlatform)
            return PaError.paIncompatibleStreamHostApi;

        var logger = PALibLogging.GetLogger(Category);
        if (logger.IsEnabled(LogLevel.Trace))
            logger.LogTrace("{Method}(size={Size}, hostApiType={HostApiType}, version={Version}, flags={Flags}, framesPerBuffer={FramesPerBuffer}, channelMask={ChannelMask})",
                nameof(TraceStreamInfo), info.size, info.hostApiType, info.version, info.flags, info.framesPerBuffer, info.channelMask);

        return PaError.paNoError;
    }
}
