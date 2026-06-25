using System.Runtime.InteropServices;
using PALib.Types.Core;

namespace PALib.ALSA;

[StructLayout(LayoutKind.Sequential)]
internal struct PaAlsaStreamInfo
{
    public nuint size;
    public PaHostApiTypeId hostApiType;
    public nuint version;
    public nint deviceString;

    public readonly string? DeviceString => Marshal.PtrToStringUTF8(deviceString);
}
