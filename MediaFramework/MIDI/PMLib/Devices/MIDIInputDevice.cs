using Microsoft.Extensions.Logging;
using PMLib.MessageTypes;
using PMLib.Runtime;
using PMLib.Types;

namespace PMLib.Devices;

/// <summary>
/// A PortMidi input device that polls for incoming MIDI messages on a background thread
/// and raises <see cref="MessageReceived"/> and <see cref="SysExReceived"/> events.
/// </summary>
public class MIDIInputDevice : MIDIDevice
{
    private Thread?   _pollThread;
    private volatile bool _polling;
    private readonly ManualResetEventSlim _pollWakeSignal = new(false);

    // Partial SysEx accumulator
    private List<byte>? _sysExBuffer;

    // ── Configuration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Number of input events buffered by PortMidi before overflow.
    /// Must be set before <see cref="Open"/>. Default: 256.
    /// </summary>
    public int BufferSize { get; set; } = 256;

    /// <summary>
    /// How long the poll thread sleeps between each <c>Pm_Read</c> call, in milliseconds.
    /// Lower values reduce latency at the cost of CPU. Default: 1 ms.
    /// </summary>
    public int PollIntervalMs { get; set; } = 1;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised on the polling thread whenever one or more decoded MIDI messages are received.
    /// <para>
    /// Handlers run on a background thread — marshal to the UI thread if needed.
    /// </para>
    /// </summary>
    public event EventHandler<IMIDIMessage>? MessageReceived;

    /// <summary>
    /// Raised on the polling thread when a complete SysEx message (0xF0 … 0xF7) has been
    /// assembled from its PortMidi fragments.
    /// </summary>
    public event EventHandler<SysEx>? SysExReceived;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public MIDIInputDevice(int deviceId) : base(deviceId) { }

    /// <summary>
    /// Opens the input stream and starts the background polling thread.
    /// </summary>
    public override PmError Open()
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("MIDIInputDevice.Open() (deviceId={DeviceId}, name={Name}, bufferSize={BufferSize})",
                DeviceId, Name, BufferSize);

        var err = Native.Pm_OpenInput(
            out Stream, DeviceId,
            inputSysDepInfo: nint.Zero,
            bufferSize: BufferSize,
            timeProc: nint.Zero,
            timeInfo: nint.Zero);

        if (err != PmError.NoError)
        {
            Logger.LogWarning("MIDIInputDevice.Open() failed: {Error} (deviceId={DeviceId}, name={Name})",
                err, DeviceId, Name);
            return err;
        }

        _polling    = true;
        _pollThread = new Thread(PollLoop)
        {
            Name         = $"MIDIInput[{DeviceId}]",
            IsBackground = true
        };
        _pollThread.Start();
        return PmError.NoError;
    }

    /// <summary>
    /// Stops the polling thread and closes the stream.
    /// </summary>
    public override PmError Close()
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("MIDIInputDevice.Close() (deviceId={DeviceId}, name={Name})", DeviceId, Name);

        _polling = false;
        _pollWakeSignal.Set();

        if (_pollThread is { IsAlive: true } t)
        {
            var deadlineTicks = Environment.TickCount64 + 2000;
            while (t.IsAlive)
            {
                var remainMs = deadlineTicks - Environment.TickCount64;
                if (remainMs <= 0)
                    break;

                var slice = remainMs > 32 ? 32 : (int)remainMs;
                if (slice < 1) slice = 1;
                _ = t.Join(TimeSpan.FromMilliseconds(slice));
            }
        }

        _pollThread = null;
        _sysExBuffer = null;
        return base.Close();
    }

    // ── Stream configuration ──────────────────────────────────────────────────

    /// <summary>
    /// Sets message-type filters on this open input stream.
    /// Filtered message types are silently discarded.
    /// </summary>
    public PmError SetFilter(PmFilter filters)
    {
        if (!IsOpen) return PmError.BadPtr;
        return Native.Pm_SetFilter(Stream, filters);
    }

    /// <summary>
    /// Sets a 16-bit channel mask. Use <see cref="PMUtil.ChannelMask"/> to build the mask.
    /// Only messages on channels whose bit is set are received.
    /// </summary>
    public PmError SetChannelMask(int mask)
    {
        if (!IsOpen) return PmError.BadPtr;
        return Native.Pm_SetChannelMask(Stream, mask);
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    private void PollLoop()
    {
        var buffer = new PmEvent[64];

        while (_polling && Stream != nint.Zero)
        {
            var count = Native.Pm_Read(Stream, buffer, buffer.Length);

            if (count > 0)
            {
                for (int i = 0; i < count; i++)
                    ProcessEvent(buffer[i]);
            }
            else if (count == (int)PmError.BufferOverflow)
            {
                Logger.LogWarning("MIDIInputDevice buffer overflow (deviceId={DeviceId}, name={Name})", DeviceId, Name);
            }

            // Use ManualResetEventSlim instead of Thread.Sleep(1) to avoid
            // 10–15 ms sleep resolution on Windows (P4.6).
            _pollWakeSignal.Wait(TimeSpan.FromMilliseconds(PollIntervalMs));
            _pollWakeSignal.Reset();
        }
    }

    /// <summary>
    /// Routes a single PortMidi event through a two-state machine:
    /// <list type="bullet">
    ///   <item><b>Normal mode</b> (<c>_sysExBuffer == null</c>): decodes the event and
    ///     fires <see cref="MessageReceived"/>, or transitions to SysEx mode on 0xF0.</item>
    ///   <item><b>SysEx accumulation mode</b> (<c>_sysExBuffer != null</c>): appends bytes
    ///     to the accumulator until EOX (0xF7) is found, then fires
    ///     <see cref="SysExReceived"/> and returns to normal mode.  Real-time messages
    ///     (0xF8–0xFF) embedded inside SysEx are dispatched immediately without
    ///     disrupting accumulation (per MIDI spec).</item>
    /// </list>
    /// Handler exceptions are caught and logged rather than propagated (§8.2).
    /// </summary>
    private void ProcessEvent(PmEvent ev)
    {
        byte status = PmEvent.GetStatus(ev.Message);

        if (_sysExBuffer != null)
        {
            // Real-time messages can be embedded inside SysEx — fire them independently.
            if (MIDIMessageParser.IsRealTime(status))
            {
                var rt = MIDIMessageParser.Decode(ev);
                if (rt != null) try { MessageReceived?.Invoke(this, rt); }
                    catch (Exception ex) { Logger.LogWarning(ex, "MessageReceived handler threw (deviceId={DeviceId})", DeviceId); }
                return;
            }
            AccumulateSysEx(ev.Message, startByte: 0);
        }
        else if (status == 0xF0)
        {
            _sysExBuffer = new List<byte>();
            AccumulateSysEx(ev.Message, startByte: 0);
        }
        else
        {
            var msg = MIDIMessageParser.Decode(ev);
            if (msg != null) try { MessageReceived?.Invoke(this, msg); }
                catch (Exception ex) { Logger.LogWarning(ex, "MessageReceived handler threw (deviceId={DeviceId})", DeviceId); }
        }
    }

    /// <summary>
    /// Appends bytes from a raw PmEvent message word into the SysEx accumulator.
    /// Fires <see cref="SysExReceived"/> when EOX (0xF7) is found.
    /// </summary>
    private void AccumulateSysEx(uint message, int startByte)
    {
        for (int b = startByte; b < 4; b++)
        {
            byte by = (byte)((message >> (b * 8)) & 0xFF);

            if (by == 0xF7)
            {
                _sysExBuffer!.Add(0xF7);
                try { SysExReceived?.Invoke(this, new SysEx([.. _sysExBuffer])); }
                catch (Exception ex) { Logger.LogWarning(ex, "SysExReceived handler threw (deviceId={DeviceId})", DeviceId); }
                _sysExBuffer = null;
                return;
            }

            // A non-real-time status byte inside SysEx means truncated/corrupt message.
            if (b > 0 && (by & 0x80) != 0)
            {
                _sysExBuffer = null;
                return;
            }

            _sysExBuffer!.Add(by);
        }
    }
}