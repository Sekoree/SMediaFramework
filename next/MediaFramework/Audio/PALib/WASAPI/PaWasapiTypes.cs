#pragma warning disable IDE1006

using System.Runtime.InteropServices;
using PALib.Types.Core;

namespace PALib.WASAPI;

[Flags]
internal enum PaWasapiFlags : uint
{
    paWinWasapiExclusive = 1 << 0,
    paWinWasapiRedirectHostProcessor = 1 << 1,
    paWinWasapiUseChannelMask = 1 << 2,
    paWinWasapiPolling = 1 << 3,
    paWinWasapiThreadPriority = 1 << 4,
    paWinWasapiExplicitSampleFormat = 1 << 5,
    paWinWasapiAutoConvert = 1 << 6
}

[Flags]
internal enum PaWasapiStreamState : uint
{
    paWasapiStreamStateError = 1 << 0,
    paWasapiStreamStateThreadPrepare = 1 << 1,
    paWasapiStreamStateThreadStart = 1 << 2,
    paWasapiStreamStateThreadStop = 1 << 3
}

internal enum PaWasapiDeviceRole : int
{
    eRoleRemoteNetworkDevice = 0,
    eRoleSpeakers,
    eRoleLineLevel,
    eRoleHeadphones,
    eRoleMicrophone,
    eRoleHeadset,
    eRoleHandset,
    eRoleUnknownDigitalPassthrough,
    eRoleSPDIF,
    eRoleHDMI,
    eRoleUnknownFormFactor
}

internal enum PaWasapiJackConnectionType : int
{
    eJackConnTypeUnknown,
    eJackConnType3Point5mm,
    eJackConnTypeQuarter,
    eJackConnTypeAtapiInternal,
    eJackConnTypeRCA,
    eJackConnTypeOptical,
    eJackConnTypeOtherDigital,
    eJackConnTypeOtherAnalog,
    eJackConnTypeMultichannelAnalogDIN,
    eJackConnTypeXlrProfessional,
    eJackConnTypeRJ11Modem,
    eJackConnTypeCombination
}

internal enum PaWasapiJackGeoLocation : int
{
    eJackGeoLocUnk = 0,
    eJackGeoLocRear = 0x1,
    eJackGeoLocFront,
    eJackGeoLocLeft,
    eJackGeoLocRight,
    eJackGeoLocTop,
    eJackGeoLocBottom,
    eJackGeoLocRearPanel,
    eJackGeoLocRiser,
    eJackGeoLocInsideMobileLid,
    eJackGeoLocDrivebay,
    eJackGeoLocHDMI,
    eJackGeoLocOutsideMobileLid,
    eJackGeoLocATAPI,
    eJackGeoLocReserved5,
    eJackGeoLocReserved6
}

internal enum PaWasapiJackGenLocation : int
{
    eJackGenLocPrimaryBox = 0,
    eJackGenLocInternal,
    eJackGenLocSeparate,
    eJackGenLocOther
}

internal enum PaWasapiJackPortConnection : int
{
    eJackPortConnJack = 0,
    eJackPortConnIntegratedDevice,
    eJackPortConnBothIntegratedAndJack,
    eJackPortConnUnknown
}

internal enum PaWasapiThreadPriority : int
{
    eThreadPriorityNone = 0,
    eThreadPriorityAudio,
    eThreadPriorityCapture,
    eThreadPriorityDistribution,
    eThreadPriorityGames,
    eThreadPriorityPlayback,
    eThreadPriorityProAudio,
    eThreadPriorityWindowManager
}

internal enum PaWasapiStreamCategory : int
{
    eAudioCategoryOther = 0,
    eAudioCategoryCommunications = 3,
    eAudioCategoryAlerts = 4,
    eAudioCategorySoundEffects = 5,
    eAudioCategoryGameEffects = 6,
    eAudioCategoryGameMedia = 7,
    eAudioCategoryGameChat = 8,
    eAudioCategorySpeech = 9,
    eAudioCategoryMovie = 10,
    eAudioCategoryMedia = 11
}

internal enum PaWasapiStreamOption : int
{
    eStreamOptionNone = 0,
    eStreamOptionRaw = 1,
    eStreamOptionMatchFormat = 2
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PaWasapiHostProcessorCallback(nint inputBuffer, nint inputFrames, nint outputBuffer, nint outputFrames, nint userData);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void PaWasapiStreamStateCallback(nint stream, uint stateFlags, uint errorId, nint userData);

[StructLayout(LayoutKind.Sequential)]
internal struct PaWasapiJackDescription
{
    public uint channelMapping;
    public uint color;
    public PaWasapiJackConnectionType connectionType;
    public PaWasapiJackGeoLocation geoLocation;
    public PaWasapiJackGenLocation genLocation;
    public PaWasapiJackPortConnection portConnection;
    public uint isConnected;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaWasapiStreamInfo
{
    public nuint size;
    public PaHostApiTypeId hostApiType;
    public nuint version;
    public nuint flags;
    public PaWinWaveFormatChannelMask channelMask;
    public nint hostProcessorOutput;
    public nint hostProcessorInput;
    public PaWasapiThreadPriority threadPriority;
    public PaWasapiStreamCategory streamCategory;
    public PaWasapiStreamOption streamOption;
}

#pragma warning restore IDE1006
