using System.Runtime.InteropServices;
using PALib.Types.Core;
using PALib.WASAPI;

namespace PALib.DirectSound;

internal static class PaWinDirectSoundConstants
{
    public const uint paWinDirectSoundUseLowLevelLatencyParameters = 0x01;
    public const uint paWinDirectSoundUseChannelMask = 0x04;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaWinDirectSoundStreamInfo
{
    public nuint size;
    public PaHostApiTypeId hostApiType;
    public nuint version;
    public nuint flags;
    public nuint framesPerBuffer;
    public PaWinWaveFormatChannelMask channelMask;
}
