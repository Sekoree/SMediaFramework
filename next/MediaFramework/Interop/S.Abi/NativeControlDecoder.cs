using System.Runtime.InteropServices;
using System.Text;
using OSCLib;
using S.Control;

namespace S.Abi;

/// <summary>
/// Adapts a native plugin's <c>MfpControlDecoderVTable</c> to the managed <see cref="IControlMeterBlobDecoder"/>,
/// so a plugin-provided feedback decoder (registered via the ABI under a name like "acme.meters") is
/// indistinguishable from the built-in X32 decoder once added to <see cref="ControlMeterBlobDecoderRegistry"/>.
/// Decode forwards the complete OSC argument list through the function pointer; the plugin fills a host-provided
/// readings buffer.
/// </summary>
internal sealed unsafe class NativeControlDecoder : IControlMeterBlobDecoder, IDisposable
{
    private const int MaxReadings = 256;

    private readonly MfpControlDecoderVTable* _vt;
    private readonly void* _self;
    private readonly AbiPluginLease _lease;
    private bool _disposed;

    public NativeControlDecoder(nint vtable, nint self, AbiPluginLease lease)
    {
        _vt = (MfpControlDecoderVTable*)vtable;
        _self = (void*)self;
        _lease = lease;
    }

    public IEnumerable<ControlMeterReading> Decode(
        string oscAddress,
        IReadOnlyList<OSCArgument> arguments,
        int blobArgumentIndex,
        ReadOnlyMemory<byte> blob)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_vt->Decode == null)
            return [];

        var addrLen = Encoding.UTF8.GetByteCount(oscAddress);
        var addr = stackalloc byte[addrLen + 1];
        Encoding.UTF8.GetBytes(oscAddress, new Span<byte>(addr, addrLen));
        addr[addrLen] = 0;

        var outBuf = (MfpControlReading*)NativeMemory.Alloc(MaxReadings, (nuint)sizeof(MfpControlReading));
        var nativeArgs = (MfpControlArg*)NativeMemory.AllocZeroed(
            (nuint)Math.Max(1, arguments.Count), (nuint)sizeof(MfpControlArg));
        var allocations = new List<nint>();
        try
        {
            for (var i = 0; i < arguments.Count; i++)
                nativeArgs[i] = MarshalArgument(arguments[i], i == blobArgumentIndex ? blob : null, allocations);

            var count = 0;
            AbiPluginHost.ClearLastError();
            var rc = _vt->Decode(
                _self, addr, nativeArgs, arguments.Count, blobArgumentIndex, outBuf, MaxReadings, &count);

            if (rc == (int)MfpStatus.ErrAgain || count <= 0)
                return [];
            if (rc != (int)MfpStatus.Ok)
                throw AbiPluginHost.StatusException("plugin control decoder", rc);
            if (count > MaxReadings)
                throw new InvalidOperationException(
                    $"plugin control decoder returned {count} readings into a {MaxReadings}-entry buffer.");

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
            foreach (var allocation in allocations)
                NativeMemory.Free((void*)allocation);
            NativeMemory.Free(nativeArgs);
            NativeMemory.Free(outBuf);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lease.Dispose();
    }

    private static MfpControlArg MarshalArgument(
        OSCArgument argument,
        ReadOnlyMemory<byte>? selectedBlob,
        List<nint> allocations)
    {
        var result = new MfpControlArg();
        switch (argument.Type)
        {
            case OSCArgumentType.Int32:
                result.Kind = MfpControlArgKind.Int32;
                result.IntValue = argument.AsInt32();
                break;
            case OSCArgumentType.Float32:
                result.Kind = MfpControlArgKind.Float32;
                result.FloatValue = argument.AsFloat32();
                break;
            case OSCArgumentType.String:
            case OSCArgumentType.Symbol:
                result.Kind = MfpControlArgKind.String;
                SetData(ref result, Encoding.UTF8.GetBytes(argument.AsString()), allocations);
                break;
            case OSCArgumentType.Blob:
                result.Kind = MfpControlArgKind.Blob;
                SetData(ref result, (selectedBlob ?? argument.AsBlob()).Span, allocations);
                break;
            case OSCArgumentType.Int64:
                result.Kind = MfpControlArgKind.Int64;
                result.IntValue = argument.AsInt64();
                break;
            case OSCArgumentType.TimeTag:
                result.Kind = MfpControlArgKind.Int64;
                result.IntValue = unchecked((long)argument.AsTimeTag().Value);
                break;
            case OSCArgumentType.Double64:
                result.Kind = MfpControlArgKind.Double64;
                result.FloatValue = argument.AsDouble64();
                break;
            case OSCArgumentType.True:
                result.Kind = MfpControlArgKind.True;
                break;
            case OSCArgumentType.False:
                result.Kind = MfpControlArgKind.False;
                break;
            case OSCArgumentType.Nil:
                result.Kind = MfpControlArgKind.Nil;
                break;
            case OSCArgumentType.Impulse:
                result.Kind = MfpControlArgKind.Impulse;
                break;
            default:
                result.Kind = MfpControlArgKind.Unsupported;
                break;
        }
        return result;
    }

    private static void SetData(ref MfpControlArg argument, ReadOnlySpan<byte> data, List<nint> allocations)
    {
        argument.DataLength = data.Length;
        if (data.IsEmpty)
            return;
        var copy = (byte*)NativeMemory.Alloc((nuint)data.Length);
        data.CopyTo(new Span<byte>(copy, data.Length));
        argument.Data = copy;
        allocations.Add((nint)copy);
    }
}
