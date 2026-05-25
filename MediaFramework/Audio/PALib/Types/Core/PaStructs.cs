using System.Runtime.InteropServices;

namespace PALib.Types.Core;

[StructLayout(LayoutKind.Sequential)]
internal struct PaVersionInfo
{
    public int versionMajor;
    public int versionMinor;
    public int versionSubMinor;
    public nint versionControlRevision;
    public nint versionText;

    public readonly string? VersionControlRevision => Marshal.PtrToStringUTF8(versionControlRevision);
    public readonly string? VersionText => Marshal.PtrToStringUTF8(versionText);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaHostApiInfo
{
    public int structVersion;
    public PaHostApiTypeId type;
    public nint name;
    public int deviceCount;
    public int defaultInputDevice;
    public int defaultOutputDevice;

    public readonly string? Name => Marshal.PtrToStringUTF8(name);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaHostErrorInfo
{
    public PaHostApiTypeId hostApiType;
    public nint errorCode;
    public nint errorText;

    public readonly string? ErrorText => Marshal.PtrToStringUTF8(errorText);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaDeviceInfo
{
    public int structVersion;
    public nint name;
    public int hostApi;
    public int maxInputChannels;
    public int maxOutputChannels;
    public double defaultLowInputLatency;
    public double defaultLowOutputLatency;
    public double defaultHighInputLatency;
    public double defaultHighOutputLatency;
    public double defaultSampleRate;

    public readonly string? Name => Marshal.PtrToStringUTF8(name);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaStreamParameters
{
    public int device;
    public int channelCount;
    public PaSampleFormat sampleFormat;
    public double suggestedLatency;
    public nint hostApiSpecificStreamInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaStreamCallbackTimeInfo
{
    public double inputBufferAdcTime;
    public double currentTime;
    public double outputBufferDacTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PaStreamInfo
{
    public int structVersion;
    public double inputLatency;
    public double outputLatency;
    public double sampleRate;
}
