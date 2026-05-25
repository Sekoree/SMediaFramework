using System.Runtime.InteropServices;
using PALib.Types.Core;
using PALib.WASAPI;

namespace PALib.WMME;

internal static class PaWinMmeConstants
{
    public const uint paWinMmeUseLowLevelLatencyParameters = 0x01;
    public const uint paWinMmeUseMultipleDevices = 0x02;
    public const uint paWinMmeUseChannelMask = 0x04;
    public const uint paWinMmeDontThrottleOverloadedProcessingThread = 0x08;
    public const uint paWinMmeWaveFormatDolbyAc3Spdif = 0x10;
    public const uint paWinMmeWaveFormatWmaSpdif = 0x20;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaWinMmeDeviceAndChannelCount
{
    public int device;
    public int channelCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaWinMmeStreamInfo
{
    public nuint size;
    public PaHostApiTypeId hostApiType;
    public nuint version;
    public nuint flags;
    public nuint framesPerBuffer;
    public nuint bufferCount;
    public nint devices;
    public nuint deviceCount;
    public PaWinWaveFormatChannelMask channelMask;
}
