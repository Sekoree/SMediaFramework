using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PMLib.Runtime;
using PMLib.Types;

namespace PMLib;

/// <summary>
/// P/Invoke bindings for the PortMidi library, generated via <see cref="LibraryImportAttribute"/>.
/// The native library must be loadable as <c>portmidi</c>
/// (i.e. <c>libportmidi.so</c> on Linux, <c>portmidi.dll</c> on Windows,
/// <c>libportmidi.dylib</c> on macOS).
/// </summary>
internal static partial class Native
{
    private const string LibraryName = "portmidi";

    // P4.17: late-bound logger so PMLibLogging.Configure() is always honoured.
    private static ILogger Logger => PMLibLogging.GetLogger("PMLib.Native");

    // ── Initialisation ──────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_Initialize")]
    private static partial PmError Pm_Initialize_Import();
    /// <summary>
    /// Initialises the PortMidi library and scans for available devices.
    /// Must be called before any other <c>Pm_*</c> function.
    /// </summary>
    internal static PmError Pm_Initialize()
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}()", nameof(Pm_Initialize));
        return Pm_Initialize_Import();
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_Terminate")]
    private static partial PmError Pm_Terminate_Import();
    /// <summary>Terminates the PortMidi library. Call when you are finished with PortMidi.</summary>
    internal static PmError Pm_Terminate()
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}()", nameof(Pm_Terminate));
        return Pm_Terminate_Import();
    }

    // ── Error handling ──────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_HasHostError")]
    private static partial int Pm_HasHostError_Import(nint stream);
    /// <summary>
    /// Returns non-zero if <paramref name="stream"/> has a pending asynchronous host error.
    /// </summary>
    internal static int Pm_HasHostError(nint stream)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream})", nameof(Pm_HasHostError), PMLibLogging.PtrMeta(stream));
        return Pm_HasHostError_Import(stream);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_GetErrorText")]
    private static partial nint Pm_GetErrorText_Import(PmError errnum);
    /// <summary>
    /// Returns a pointer to a static, library-owned string describing <paramref name="errnum"/>.
    /// For a managed <see cref="string"/>, marshal the returned pointer with <c>Marshal.PtrToStringUTF8</c>.
    /// </summary>
    internal static nint Pm_GetErrorText(PmError errnum)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Error})", nameof(Pm_GetErrorText), errnum);
        return Pm_GetErrorText_Import(errnum);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_GetHostErrorText")]
    private static partial void Pm_GetHostErrorText_Import(Span<byte> msg, uint len);
    /// <summary>
    /// Writes a human-readable host-error description into <paramref name="msg"/>.
    /// </summary>
    internal static void Pm_GetHostErrorText(Span<byte> msg, uint len)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}(len={Len})", nameof(Pm_GetHostErrorText), len);
        Pm_GetHostErrorText_Import(msg, len);
    }

    // ── Device enumeration ──────────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_CountDevices")]
    private static partial int Pm_CountDevices_Import();
    /// <summary>Returns the total number of MIDI devices.</summary>
    internal static int Pm_CountDevices()
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}()", nameof(Pm_CountDevices));
        return Pm_CountDevices_Import();
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_GetDefaultInputDeviceID")]
    private static partial int Pm_GetDefaultInputDeviceID_Import();
    /// <summary>Returns the default input device ID, or <c>-1</c> if none exists.</summary>
    internal static int Pm_GetDefaultInputDeviceID()
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}()", nameof(Pm_GetDefaultInputDeviceID));
        return Pm_GetDefaultInputDeviceID_Import();
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_GetDefaultOutputDeviceID")]
    private static partial int Pm_GetDefaultOutputDeviceID_Import();
    /// <summary>Returns the default output device ID, or <c>-1</c> if none exists.</summary>
    internal static int Pm_GetDefaultOutputDeviceID()
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}()", nameof(Pm_GetDefaultOutputDeviceID));
        return Pm_GetDefaultOutputDeviceID_Import();
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_FindDevice", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Pm_FindDevice_Import(string pattern, int isInput);
    /// <summary>
    /// Finds the first device whose name contains <paramref name="pattern"/>.
    /// </summary>
    internal static int Pm_FindDevice(string pattern, int isInput)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Pattern}, {IsInput})", nameof(Pm_FindDevice), pattern, isInput);
        return Pm_FindDevice_Import(pattern, isInput);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_GetDeviceInfo")]
    private static partial nint Pm_GetDeviceInfo_Import(int id);
    /// <summary>
    /// Returns a native pointer to a <see cref="PmDeviceInfo"/> structure for the given device.
    /// </summary>
    internal static nint Pm_GetDeviceInfo(int id)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Id})", nameof(Pm_GetDeviceInfo), id);
        return Pm_GetDeviceInfo_Import(id);
    }

    // ── Opening and closing streams ─────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_OpenInput")]
    private static partial PmError Pm_OpenInput_Import(
        out nint stream, int inputDevice, nint inputSysDepInfo,
        int bufferSize, nint timeProc, nint timeInfo);
    /// <summary>Opens a MIDI input stream.</summary>
    internal static PmError Pm_OpenInput(
        out nint stream, int inputDevice, nint inputSysDepInfo,
        int bufferSize, nint timeProc, nint timeInfo)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}(device={Device}, bufferSize={BufferSize})", nameof(Pm_OpenInput), inputDevice, bufferSize);
        return Pm_OpenInput_Import(out stream, inputDevice, inputSysDepInfo, bufferSize, timeProc, timeInfo);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_OpenOutput")]
    private static partial PmError Pm_OpenOutput_Import(
        out nint stream, int outputDevice, nint outputSysDepInfo,
        int bufferSize, nint timeProc, nint timeInfo, int latency);
    /// <summary>Opens a MIDI output stream.</summary>
    internal static PmError Pm_OpenOutput(
        out nint stream, int outputDevice, nint outputSysDepInfo,
        int bufferSize, nint timeProc, nint timeInfo, int latency)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}(device={Device}, bufferSize={BufferSize}, latency={Latency})", nameof(Pm_OpenOutput), outputDevice, bufferSize, latency);
        return Pm_OpenOutput_Import(out stream, outputDevice, outputSysDepInfo, bufferSize, timeProc, timeInfo, latency);
    }

    // ── Virtual devices ─────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_CreateVirtualInput", StringMarshalling = StringMarshalling.Utf8)]
    private static partial PmError Pm_CreateVirtualInput_Import(string name, string? interf, nint sysDepInfo);
    internal static PmError Pm_CreateVirtualInput(string name, string? interf, nint sysDepInfo)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Name}, {Interf})", nameof(Pm_CreateVirtualInput), name, interf);
        return Pm_CreateVirtualInput_Import(name, interf, sysDepInfo);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_CreateVirtualOutput", StringMarshalling = StringMarshalling.Utf8)]
    private static partial PmError Pm_CreateVirtualOutput_Import(string name, string? interf, nint sysDepInfo);
    internal static PmError Pm_CreateVirtualOutput(string name, string? interf, nint sysDepInfo)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Name}, {Interf})", nameof(Pm_CreateVirtualOutput), name, interf);
        return Pm_CreateVirtualOutput_Import(name, interf, sysDepInfo);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_DeleteVirtualDevice")]
    private static partial PmError Pm_DeleteVirtualDevice_Import(int device);
    internal static PmError Pm_DeleteVirtualDevice(int device)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Device})", nameof(Pm_DeleteVirtualDevice), device);
        return Pm_DeleteVirtualDevice_Import(device);
    }

    // ── Stream configuration ────────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_SetFilter")]
    private static partial PmError Pm_SetFilter_Import(nint stream, PmFilter filters);
    internal static PmError Pm_SetFilter(nint stream, PmFilter filters)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream}, {Filters})", nameof(Pm_SetFilter), PMLibLogging.PtrMeta(stream), filters);
        return Pm_SetFilter_Import(stream, filters);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_SetChannelMask")]
    private static partial PmError Pm_SetChannelMask_Import(nint stream, int mask);
    internal static PmError Pm_SetChannelMask(nint stream, int mask)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream}, mask=0x{Mask:X4})", nameof(Pm_SetChannelMask), PMLibLogging.PtrMeta(stream), mask);
        return Pm_SetChannelMask_Import(stream, mask);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_Abort")]
    private static partial PmError Pm_Abort_Import(nint stream);
    internal static PmError Pm_Abort(nint stream)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream})", nameof(Pm_Abort), PMLibLogging.PtrMeta(stream));
        return Pm_Abort_Import(stream);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_Close")]
    private static partial PmError Pm_Close_Import(nint stream);
    internal static PmError Pm_Close(nint stream)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream})", nameof(Pm_Close), PMLibLogging.PtrMeta(stream));
        return Pm_Close_Import(stream);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_Synchronize")]
    private static partial PmError Pm_Synchronize_Import(nint stream);
    internal static PmError Pm_Synchronize(nint stream)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream})", nameof(Pm_Synchronize), PMLibLogging.PtrMeta(stream));
        return Pm_Synchronize_Import(stream);
    }

    // ── Reading ─────────────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_Read")]
    private static partial int Pm_Read_Import(nint stream, Span<PmEvent> buffer, int length);
    /// <summary>Reads up to <paramref name="length"/> MIDI events from an input stream.</summary>
    internal static int Pm_Read(nint stream, Span<PmEvent> buffer, int length)
    {
        // Note: Pm_Read is hot-path — only log at Trace level with guard.
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream}, length={Length})", nameof(Pm_Read), PMLibLogging.PtrMeta(stream), length);
        return Pm_Read_Import(stream, buffer, length);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_Poll")]
    private static partial PmError Pm_Poll_Import(nint stream);
    internal static PmError Pm_Poll(nint stream)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream})", nameof(Pm_Poll), PMLibLogging.PtrMeta(stream));
        return Pm_Poll_Import(stream);
    }

    // ── Writing ─────────────────────────────────────────────────────────────────

    [LibraryImport(LibraryName, EntryPoint = "Pm_Write")]
    private static partial PmError Pm_Write_Import(nint stream, ReadOnlySpan<PmEvent> buffer, int length);
    internal static PmError Pm_Write(nint stream, ReadOnlySpan<PmEvent> buffer, int length)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream}, length={Length})", nameof(Pm_Write), PMLibLogging.PtrMeta(stream), length);
        return Pm_Write_Import(stream, buffer, length);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_WriteShort")]
    private static partial PmError Pm_WriteShort_Import(nint stream, int when, uint msg);
    internal static PmError Pm_WriteShort(nint stream, int when, uint msg)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream}, when={When}, msg=0x{Msg:X8})", nameof(Pm_WriteShort), PMLibLogging.PtrMeta(stream), when, msg);
        return Pm_WriteShort_Import(stream, when, msg);
    }

    [LibraryImport(LibraryName, EntryPoint = "Pm_WriteSysEx")]
    private static partial PmError Pm_WriteSysEx_Import(nint stream, int when, ReadOnlySpan<byte> msg);
    internal static PmError Pm_WriteSysEx(nint stream, int when, ReadOnlySpan<byte> msg)
    {
        if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("{Method}({Stream}, when={When}, msgLen={Len})", nameof(Pm_WriteSysEx), PMLibLogging.PtrMeta(stream), when, msg.Length);
        return Pm_WriteSysEx_Import(stream, when, msg);
    }

    // ── Lock-free queue — pmutil ─────────────────────────────────────────────────

    /// <summary>Creates a lock-free, single-reader / single-writer queue.</summary>
    [LibraryImport(LibraryName)]
    internal static partial nint Pm_QueueCreate(nint numMsgs, int bytesPerMsg);

    /// <summary>Destroys a queue and frees its memory.</summary>
    [LibraryImport(LibraryName)]
    internal static partial PmError Pm_QueueDestroy(nint queue);

    /// <summary>Removes and copies the message at the head of the queue.</summary>
    [LibraryImport(LibraryName)]
    internal static partial PmError Pm_Dequeue(nint queue, nint msg);

    /// <summary>Copies the message and appends it to the queue.</summary>
    [LibraryImport(LibraryName)]
    internal static partial PmError Pm_Enqueue(nint queue, nint msg);

    /// <summary>Returns non-zero if the queue is full.</summary>
    [LibraryImport(LibraryName)]
    internal static partial int Pm_QueueFull(nint queue);

    /// <summary>Returns non-zero if the queue is empty (or null).</summary>
    [LibraryImport(LibraryName)]
    internal static partial int Pm_QueueEmpty(nint queue);

    /// <summary>Returns a pointer to the head message without removing it.</summary>
    [LibraryImport(LibraryName)]
    internal static partial nint Pm_QueuePeek(nint queue);

    /// <summary>Signals an overflow condition to the reader.</summary>
    [LibraryImport(LibraryName)]
    internal static partial PmError Pm_SetOverflow(nint queue);
}
