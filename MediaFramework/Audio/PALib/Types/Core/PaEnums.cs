#pragma warning disable IDE1006

namespace PALib.Types.Core;

internal enum PaError : int
{
    paNoError = 0,
    paNotInitialized = -10000,
    paUnanticipatedHostError,
    paInvalidChannelCount,
    paInvalidSampleRate,
    paInvalidDevice,
    paInvalidFlag,
    paSampleFormatNotSupported,
    paBadIODeviceCombination,
    paInsufficientMemory,
    paBufferTooBig,
    paBufferTooSmall,
    paNullCallback,
    paBadStreamPtr,
    paTimedOut,
    paInternalError,
    paDeviceUnavailable,
    paIncompatibleHostApiSpecificStreamInfo,
    paStreamIsStopped,
    paStreamIsNotStopped,
    paInputOverflowed,
    paOutputUnderflowed,
    paHostApiNotFound,
    paInvalidHostApi,
    paCanNotReadFromACallbackStream,
    paCanNotWriteToACallbackStream,
    paCanNotReadFromAnOutputOnlyStream,
    paCanNotWriteToAnInputOnlyStream,
    paIncompatibleStreamHostApi,
    paBadBufferPtr
}

internal enum PaHostApiTypeId : int
{
    paInDevelopment = 0,
    paDirectSound = 1,
    paMME = 2,
    paASIO = 3,
    paSoundManager = 4,
    paCoreAudio = 5,
    paOSS = 7,
    paALSA = 8,
    paAL = 9,
    paBeOS = 10,
    paWDMKS = 11,
    paJACK = 12,
    paWASAPI = 13,
    paAudioScienceHPI = 14
}

[Flags]
internal enum PaSampleFormat : ulong
{
    paFloat32 = 0x00000001,
    paInt32 = 0x00000002,
    paInt24 = 0x00000004,
    paInt16 = 0x00000008,
    paInt8 = 0x00000010,
    paUInt8 = 0x00000020,
    paCustomFormat = 0x00010000,
    paNonInterleaved = 0x80000000
}

[Flags]
internal enum PaStreamFlags : ulong
{
    paNoFlag = 0,
    paClipOff = 0x00000001,
    paDitherOff = 0x00000002,
    paNeverDropInput = 0x00000004,
    paPrimeOutputBuffersUsingStreamCallback = 0x00000008,
    paPlatformSpecificFlags = 0xFFFF0000
}

[Flags]
internal enum PaStreamCallbackFlags : ulong
{
    paInputUnderflow = 0x00000001,
    paInputOverflow = 0x00000002,
    paOutputUnderflow = 0x00000004,
    paOutputOverflow = 0x00000008,
    paPrimingOutput = 0x00000010
}

internal enum PaStreamCallbackResult : int
{
    paContinue = 0,
    paComplete = 1,
    paAbort = 2
}

#pragma warning restore IDE1006
