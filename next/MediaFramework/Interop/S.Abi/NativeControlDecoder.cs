using System.Runtime.InteropServices;
using System.Text;
using OSCLib;
using S.Control;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpControlDecoderVTable</c> to the managed <see cref="IControlMeterBlobDecoder"/>,
/// so a plugin-provided feedback decoder (registered via the ABI under a name like "acme.meters") is
/// indistinguishable from the built-in X32 decoder once added to <see cref="ControlMeterBlobDecoderRegistry"/>.
/// Decode forwards the OSC address + the blob bytes through the function pointer; the plugin fills a host-provided
/// readings buffer. (The v1 ABI passes the address + blob, not the surrounding OSC arguments.)
/// </summary>
internal sealed unsafe class NativeControlDecoder : IControlMeterBlobDecoder
{
    private const int MaxReadings = 256;

    private readonly MfpControlDecoderVTable* _vt;
    private readonly void* _self;

    public NativeControlDecoder(nint vtable, nint self)
    {
        _vt = (MfpControlDecoderVTable*)vtable;
        _self = (void*)self;
    }

    public IEnumerable<ControlMeterReading> Decode(
        string oscAddress,
        IReadOnlyList<OSCArgument> arguments,
        int blobArgumentIndex,
        ReadOnlyMemory<byte> blob)
    {
        if (_vt->Decode == null)
            return [];

        var addrLen = Encoding.UTF8.GetByteCount(oscAddress);
        var addr = stackalloc byte[addrLen + 1];
        Encoding.UTF8.GetBytes(oscAddress, new Span<byte>(addr, addrLen));
        addr[addrLen] = 0;

        var outBuf = (MfpControlReading*)NativeMemory.Alloc(MaxReadings, (nuint)sizeof(MfpControlReading));
        try
        {
            var count = 0;
            int rc;
            var span = blob.Span;
            fixed (byte* blobPtr = span)
            {
                rc = _vt->Decode(_self, addr, blobPtr, span.Length, outBuf, MaxReadings, &count);
            }

            if (rc != (int)MfpStatus.Ok || count <= 0)
                return [];

            var readings = new List<ControlMeterReading>(Math.Min(count, MaxReadings));
            for (var i = 0; i < count && i < MaxReadings; i++)
            {
                // Address is the first field of MfpControlReading (offset 0), so the struct pointer is the string pointer.
                var a = Marshal.PtrToStringUTF8((nint)(outBuf + i)) ?? string.Empty;
                readings.Add(new ControlMeterReading(a, (float)outBuf[i].Value));
            }

            return readings;
        }
        finally
        {
            NativeMemory.Free(outBuf);
        }
    }
}
