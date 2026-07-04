using System.Runtime.InteropServices;
using PMLib.Types;

namespace PMLib.Runtime;

/// <summary>
/// Allocates a PortMIDI 2.x <c>PmSysDepInfo</c> block for optional ALSA client/port names.
/// </summary>
internal sealed class PmSysDepInfoHandle : IDisposable
{
    private const int StructVersion = 210;

    [StructLayout(LayoutKind.Sequential)]
    private struct PmSysDepPropertyNative
    {
        public PmSysDepPropertyKey Key;
        public nint Value;
    }

    private nint _pointer;
    private readonly List<nint> _stringPointers = new();

    private PmSysDepInfoHandle(nint pointer) => _pointer = pointer;

    public nint Pointer => _pointer;

    public static PmSysDepInfoHandle? TryCreateAlsa(string? clientName, string? portName)
    {
        var properties = new List<(PmSysDepPropertyKey Key, string Value)>();
        if (!string.IsNullOrWhiteSpace(clientName))
            properties.Add((PmSysDepPropertyKey.AlsaClientName, clientName));
        if (!string.IsNullOrWhiteSpace(portName))
            properties.Add((PmSysDepPropertyKey.AlsaPortName, portName));

        if (properties.Count == 0)
            return null;

        var headerSize = sizeof(int) * 2;
        var propertySize = Marshal.SizeOf<PmSysDepPropertyNative>();
        var totalSize = headerSize + properties.Count * propertySize;
        var block = Marshal.AllocHGlobal(totalSize);
        var handle = new PmSysDepInfoHandle(block);

        Marshal.WriteInt32(block, StructVersion);
        Marshal.WriteInt32(block, sizeof(int), properties.Count);

        var offset = headerSize;
        for (var i = 0; i < properties.Count; i++)
        {
            var valuePtr = Marshal.StringToHGlobalAnsi(properties[i].Value);
            handle._stringPointers.Add(valuePtr);

            var propertyPtr = block + offset;
            Marshal.WriteInt32(propertyPtr, (int)properties[i].Key);
            Marshal.WriteIntPtr(propertyPtr, IntPtr.Size, valuePtr);
            offset += propertySize;
        }

        return handle;
    }

    public void Dispose()
    {
        foreach (var valuePtr in _stringPointers)
        {
            if (valuePtr != 0)
                Marshal.FreeHGlobal(valuePtr);
        }

        _stringPointers.Clear();

        if (_pointer != 0)
        {
            Marshal.FreeHGlobal(_pointer);
            _pointer = 0;
        }
    }
}
