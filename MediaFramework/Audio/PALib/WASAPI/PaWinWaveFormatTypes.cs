using System.Runtime.InteropServices;
using PALib.Runtime;
using PALib.Types.Core;

namespace PALib.WASAPI;

[Flags]
internal enum PaWinWaveFormatChannelMask : uint
{
    PAWIN_SPEAKER_FRONT_LEFT = 0x1,
    PAWIN_SPEAKER_FRONT_RIGHT = 0x2,
    PAWIN_SPEAKER_FRONT_CENTER = 0x4,
    PAWIN_SPEAKER_LOW_FREQUENCY = 0x8,
    PAWIN_SPEAKER_BACK_LEFT = 0x10,
    PAWIN_SPEAKER_BACK_RIGHT = 0x20,
    PAWIN_SPEAKER_FRONT_LEFT_OF_CENTER = 0x40,
    PAWIN_SPEAKER_FRONT_RIGHT_OF_CENTER = 0x80,
    PAWIN_SPEAKER_BACK_CENTER = 0x100,
    PAWIN_SPEAKER_SIDE_LEFT = 0x200,
    PAWIN_SPEAKER_SIDE_RIGHT = 0x400,
    PAWIN_SPEAKER_TOP_CENTER = 0x800,
    PAWIN_SPEAKER_TOP_FRONT_LEFT = 0x1000,
    PAWIN_SPEAKER_TOP_FRONT_CENTER = 0x2000,
    PAWIN_SPEAKER_TOP_FRONT_RIGHT = 0x4000,
    PAWIN_SPEAKER_TOP_BACK_LEFT = 0x8000,
    PAWIN_SPEAKER_TOP_BACK_CENTER = 0x10000,
    PAWIN_SPEAKER_TOP_BACK_RIGHT = 0x20000,
    PAWIN_SPEAKER_RESERVED = 0x7FFC0000,
    PAWIN_SPEAKER_ALL = 0x80000000,

    PAWIN_SPEAKER_MONO = PAWIN_SPEAKER_FRONT_CENTER,
    PAWIN_SPEAKER_STEREO = PAWIN_SPEAKER_FRONT_LEFT | PAWIN_SPEAKER_FRONT_RIGHT,
    PAWIN_SPEAKER_5POINT1 = PAWIN_SPEAKER_FRONT_LEFT | PAWIN_SPEAKER_FRONT_RIGHT | PAWIN_SPEAKER_FRONT_CENTER | PAWIN_SPEAKER_LOW_FREQUENCY | PAWIN_SPEAKER_BACK_LEFT | PAWIN_SPEAKER_BACK_RIGHT,
    PAWIN_SPEAKER_7POINT1 = PAWIN_SPEAKER_5POINT1 | PAWIN_SPEAKER_FRONT_LEFT_OF_CENTER | PAWIN_SPEAKER_FRONT_RIGHT_OF_CENTER,
    PAWIN_SPEAKER_5POINT1_SURROUND = PAWIN_SPEAKER_FRONT_LEFT | PAWIN_SPEAKER_FRONT_RIGHT | PAWIN_SPEAKER_FRONT_CENTER | PAWIN_SPEAKER_LOW_FREQUENCY | PAWIN_SPEAKER_SIDE_LEFT | PAWIN_SPEAKER_SIDE_RIGHT,
    PAWIN_SPEAKER_7POINT1_SURROUND = PAWIN_SPEAKER_FRONT_LEFT | PAWIN_SPEAKER_FRONT_RIGHT | PAWIN_SPEAKER_FRONT_CENTER | PAWIN_SPEAKER_LOW_FREQUENCY | PAWIN_SPEAKER_BACK_LEFT | PAWIN_SPEAKER_BACK_RIGHT | PAWIN_SPEAKER_SIDE_LEFT | PAWIN_SPEAKER_SIDE_RIGHT
}

internal static class PaWinWaveFormatConstants
{
    public const int PAWIN_SIZEOF_WAVEFORMATEX = 18;
    public const int PAWIN_SIZEOF_WAVEFORMATEXTENSIBLE = PAWIN_SIZEOF_WAVEFORMATEX + 22;

    public const uint PAWIN_WAVE_FORMAT_PCM = 1;
    public const uint PAWIN_WAVE_FORMAT_IEEE_FLOAT = 3;
    public const uint PAWIN_WAVE_FORMAT_DOLBY_AC3_SPDIF = 0x0092;
    public const uint PAWIN_WAVE_FORMAT_WMA_SPDIF = 0x0164;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PaWinWaveFormat
{
    public fixed byte fields[PaWinWaveFormatConstants.PAWIN_SIZEOF_WAVEFORMATEXTENSIBLE];
    public uint extraLongForAlignment;
}

internal static partial class WaveFormatNative
{
    private const string LibraryName = PortAudioLibraryNames.Default;
    private static bool IsSupportedPlatform => OperatingSystem.IsWindows();

    [LibraryImport(LibraryName, EntryPoint = "PaWin_SampleFormatToLinearWaveFormatTag")]
    private static partial int PaWin_SampleFormatToLinearWaveFormatTag_Import(PaSampleFormat sampleFormat);
    public static int PaWin_SampleFormatToLinearWaveFormatTag(PaSampleFormat sampleFormat)
        => !IsSupportedPlatform ? 0 : PaWin_SampleFormatToLinearWaveFormatTag_Import(sampleFormat);

    [LibraryImport(LibraryName, EntryPoint = "PaWin_InitializeWaveFormatEx")]
    private static unsafe partial void PaWin_InitializeWaveFormatEx_Import(PaWinWaveFormat* waveFormat, int numChannels, PaSampleFormat sampleFormat, int waveFormatTag, double sampleRate);
    public static unsafe void PaWin_InitializeWaveFormatEx(PaWinWaveFormat* waveFormat, int numChannels, PaSampleFormat sampleFormat, int waveFormatTag, double sampleRate)
    {
        if (!IsSupportedPlatform) return;
        PaWin_InitializeWaveFormatEx_Import(waveFormat, numChannels, sampleFormat, waveFormatTag, sampleRate);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWin_InitializeWaveFormatExtensible")]
    private static unsafe partial void PaWin_InitializeWaveFormatExtensible_Import(PaWinWaveFormat* waveFormat, int numChannels, PaSampleFormat sampleFormat, int waveFormatTag, double sampleRate, PaWinWaveFormatChannelMask channelMask);
    public static unsafe void PaWin_InitializeWaveFormatExtensible(PaWinWaveFormat* waveFormat, int numChannels, PaSampleFormat sampleFormat, int waveFormatTag, double sampleRate, PaWinWaveFormatChannelMask channelMask)
    {
        if (!IsSupportedPlatform) return;
        PaWin_InitializeWaveFormatExtensible_Import(waveFormat, numChannels, sampleFormat, waveFormatTag, sampleRate, channelMask);
    }

    [LibraryImport(LibraryName, EntryPoint = "PaWin_DefaultChannelMask")]
    private static partial PaWinWaveFormatChannelMask PaWin_DefaultChannelMask_Import(int numChannels);
    public static PaWinWaveFormatChannelMask PaWin_DefaultChannelMask(int numChannels)
        => !IsSupportedPlatform ? 0 : PaWin_DefaultChannelMask_Import(numChannels);
}
