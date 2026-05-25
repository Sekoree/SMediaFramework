using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using JackLib.Types;

namespace JackLib;

/// <summary>
/// Managed RAII wrapper around a JACK client handle.
/// Handles client lifecycle, callback delegate GC-rooting, port registration
/// and port connection management (including autoconnect to physical outputs).
/// </summary>
/// <remarks>
/// Instantiation will throw <see cref="JackException"/> if libjack is not
/// installed or the JACK server is not reachable and
/// <see cref="JackOptions.NoStartServer"/> was passed.
/// </remarks>
public sealed class JackClient : IDisposable
{
    private static readonly ILogger Log = JackLogging.GetLogger(nameof(JackClient));

    private nint _client;
    private bool _activated;
    private bool _disposed;

    // Keep managed delegates alive while JACK holds function pointers to them.
    private JackProcessDelegate?    _processDelegate;
    private JackShutdownDelegate?   _shutdownDelegate;
    private JackBufferSizeDelegate? _bufferSizeDelegate;
    private JackSampleRateDelegate? _sampleRateDelegate;
    private JackXRunDelegate?       _xRunDelegate;

    // GCHandles for the above delegates when passed to unmanaged code.
    private GCHandle _processHandle;
    private GCHandle _shutdownHandle;
    private GCHandle _bufferSizeHandle;
    private GCHandle _sampleRateHandle;
    private GCHandle _xRunHandle;

    // ── Properties ────────────────────────────────────────────────────────

    /// <summary>The actual client name as assigned by the JACK server (may differ from the requested name).</summary>
    public string?  ClientName   { get; private set; }
    /// <summary>Whether <see cref="Activate"/> has been called successfully.</summary>
    public bool     IsActivated  => _activated;
    /// <summary>The JACK server sample rate (frames per second).</summary>
    public uint     SampleRate   => EnsureOpen(Native.jack_get_sample_rate(_client));
    /// <summary>The current JACK buffer size (frames per process cycle).</summary>
    public uint     BufferSize   => EnsureOpen(Native.jack_get_buffer_size(_client));
    /// <summary>Estimated CPU load as a percentage (0–100).</summary>
    public float    CpuLoad      => EnsureOpen(Native.jack_cpu_load(_client));
    /// <summary>Whether the JACK server is running with real-time scheduling.</summary>
    public bool     IsRealtime   => EnsureOpen(Native.jack_is_realtime(_client)) != 0;

    // ── Constructor ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens a JACK client session.
    /// </summary>
    /// <param name="clientName">Requested client name (max <c>jack_client_name_size()</c> chars).</param>
    /// <param name="options">Open options. Use <see cref="JackOptions.NoStartServer"/> to avoid auto-starting jackd.</param>
    /// <exception cref="JackException">The server is unavailable or the open operation failed.</exception>
    public JackClient(string clientName, JackOptions options = JackOptions.NullOption)
    {
        _client = Native.jack_client_open(clientName, options, out var status);
        if (_client == nint.Zero)
            throw new JackException($"jack_client_open failed. Status: {status}");

        ClientName = Native.jack_get_client_name(_client);
        Log.LogInformation("Created JackClient '{ClientName}' (requested='{RequestedName}', options={Options})",
            ClientName, clientName, options);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>Tells the JACK server to start calling the process callback.</summary>
    public void Activate()
    {
        ThrowIfDisposed();
        if (_activated) return;
        var err = Native.jack_activate(_client);
        if (err != 0) throw new JackException($"jack_activate failed ({err}).");
        _activated = true;
        Log.LogInformation("JackClient '{ClientName}' activated", ClientName);
    }

    /// <summary>Removes this client from the JACK processing graph.</summary>
    public void Deactivate()
    {
        if (!_activated) return;
        Native.jack_deactivate(_client);
        _activated = false;
        Log.LogInformation("JackClient '{ClientName}' deactivated", ClientName);
    }

    // ── Callbacks ─────────────────────────────────────────────────────────

    /// <summary>
    /// Registers the process callback.
    /// The delegate is kept alive by this <see cref="JackClient"/> instance.
    /// Must be called before <see cref="Activate"/>.
    /// </summary>
    public void SetProcessCallback(JackProcessDelegate callback, nint arg = default)
    {
        ThrowIfDisposed();
        ReleaseHandle(ref _processHandle);
        _processDelegate = callback;
        _processHandle   = GCHandle.Alloc(_processDelegate);
        var fp = Marshal.GetFunctionPointerForDelegate(_processDelegate);
        Native.jack_set_process_callback(_client, fp, arg);
    }

    /// <summary>Registers the server-shutdown callback.</summary>
    public void SetShutdownCallback(JackShutdownDelegate callback, nint arg = default)
    {
        ThrowIfDisposed();
        ReleaseHandle(ref _shutdownHandle);
        _shutdownDelegate = callback;
        _shutdownHandle   = GCHandle.Alloc(_shutdownDelegate);
        var fp = Marshal.GetFunctionPointerForDelegate(_shutdownDelegate);
        Native.jack_on_shutdown(_client, fp, arg);
    }

    /// <summary>Registers the buffer-size change callback.</summary>
    public void SetBufferSizeCallback(JackBufferSizeDelegate callback, nint arg = default)
    {
        ThrowIfDisposed();
        ReleaseHandle(ref _bufferSizeHandle);
        _bufferSizeDelegate = callback;
        _bufferSizeHandle   = GCHandle.Alloc(_bufferSizeDelegate);
        var fp = Marshal.GetFunctionPointerForDelegate(_bufferSizeDelegate);
        Native.jack_set_buffer_size_callback(_client, fp, arg);
    }

    /// <summary>Registers the sample-rate change callback.</summary>
    public void SetSampleRateCallback(JackSampleRateDelegate callback, nint arg = default)
    {
        ThrowIfDisposed();
        ReleaseHandle(ref _sampleRateHandle);
        _sampleRateDelegate = callback;
        _sampleRateHandle   = GCHandle.Alloc(_sampleRateDelegate);
        var fp = Marshal.GetFunctionPointerForDelegate(_sampleRateDelegate);
        Native.jack_set_sample_rate_callback(_client, fp, arg);
    }

    /// <summary>Registers the XRun callback.</summary>
    public void SetXRunCallback(JackXRunDelegate callback, nint arg = default)
    {
        ThrowIfDisposed();
        ReleaseHandle(ref _xRunHandle);
        _xRunDelegate = callback;
        _xRunHandle   = GCHandle.Alloc(_xRunDelegate);
        var fp = Marshal.GetFunctionPointerForDelegate(_xRunDelegate);
        Native.jack_set_xrun_callback(_client, fp, arg);
    }

    // ── Port registration ─────────────────────────────────────────────────

    /// <summary>Registers a new output port on this client.</summary>
    /// <param name="shortName">Short port name (unique within this client).</param>
    /// <param name="portType">Port type string — use <see cref="JackPortType.DefaultAudio"/> for audio.</param>
    /// <returns>Opaque port handle (non-zero on success).</returns>
    public nint RegisterOutputPort(string shortName, string portType = JackPortType.DefaultAudio)
    {
        ThrowIfDisposed();
        var port = Native.jack_port_register(_client, shortName, portType,
            (ulong)JackPortFlags.IsOutput, 0);
        if (port == nint.Zero)
            throw new JackException($"jack_port_register (output '{shortName}') failed.");
        Log.LogDebug("Registered output port '{PortName}' on client '{ClientName}'", shortName, ClientName);
        return port;
    }

    /// <summary>Registers a new input port on this client.</summary>
    public nint RegisterInputPort(string shortName, string portType = JackPortType.DefaultAudio)
    {
        ThrowIfDisposed();
        var port = Native.jack_port_register(_client, shortName, portType,
            (ulong)JackPortFlags.IsInput, 0);
        if (port == nint.Zero)
            throw new JackException($"jack_port_register (input '{shortName}') failed.");
        Log.LogDebug("Registered input port '{PortName}' on client '{ClientName}'", shortName, ClientName);
        return port;
    }

    /// <summary>Unregisters and disconnects a port.</summary>
    public void UnregisterPort(nint port)
    {
        ThrowIfDisposed();
        Native.jack_port_unregister(_client, port);
    }

    // ── Port info ─────────────────────────────────────────────────────────

    /// <summary>Returns the full name (client:short) of the given port handle.</summary>
    public string? GetPortName(nint port)      => Native.jack_port_name(port);

    /// <summary>Returns the short name (without client prefix) of the given port handle.</summary>
    public string? GetPortShortName(nint port) => Native.jack_port_short_name(port);

    /// <summary>Returns the flags of the given port handle.</summary>
    public JackPortFlags GetPortFlags(nint port)
        => (JackPortFlags)(ulong)Native.jack_port_flags(port);

    /// <summary>Returns the type string of the given port handle.</summary>
    public string? GetPortType(nint port) => Native.jack_port_type(port);

    /// <summary>Returns the number of connections to/from the given port (must be owned by this client).</summary>
    public int GetPortConnectionCount(nint port) => Native.jack_port_connected(port);

    /// <summary>Returns the port handle for the given full port name, or <see cref="nint.Zero"/> if not found.</summary>
    public nint GetPortByName(string portName) => Native.jack_port_by_name(_client, portName);

    // ── Connections ───────────────────────────────────────────────────────

    /// <summary>
    /// Connects <paramref name="sourcePort"/> to <paramref name="destinationPort"/>.
    /// Returns <c>true</c> on success, <c>false</c> if the connection already exists.
    /// </summary>
    public bool Connect(string sourcePort, string destinationPort)
    {
        ThrowIfDisposed();
        var err = Native.jack_connect(_client, sourcePort, destinationPort);
        // EEXIST (17) = already connected; treat as success
        return err is 0 or 17;
    }

    /// <summary>Removes the connection between two named ports.</summary>
    public void Disconnect(string sourcePort, string destinationPort)
    {
        ThrowIfDisposed();
        Native.jack_disconnect(_client, sourcePort, destinationPort);
    }

    /// <summary>Disconnects all connections of the given port.</summary>
    public void DisconnectPort(nint port)
    {
        ThrowIfDisposed();
        Native.jack_port_disconnect(_client, port);
    }

    // ── Port search ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full names of all ports matching the given criteria.
    /// Pass <see langword="null"/> patterns to skip pattern filtering.
    /// </summary>
    public string[] GetPorts(
        string?       namePattern = null,
        string?       typePattern = null,
        JackPortFlags flags       = JackPortFlags.None)
    {
        ThrowIfDisposed();
        return Native.jack_get_ports(_client, namePattern, typePattern, flags);
    }

    // ── Autoconnect ───────────────────────────────────────────────────────

    /// <summary>
    /// Connects each of <paramref name="ourOutputPorts"/> (by full name) to the
    /// system physical playback ports, in order (port 1 → playback_1, etc.).
    /// Extra ports on either side are silently ignored.
    /// </summary>
    /// <param name="ourOutputPorts">
    /// Full port names owned by this client (e.g. <c>"my_app:out_0"</c>).
    /// Use <see cref="GetPorts"/> with <see cref="JackPortFlags.IsOutput"/> to discover them.
    /// </param>
    /// <returns>Number of connections successfully made.</returns>
    public int AutoConnectToPhysicalOutputs(IReadOnlyList<string> ourOutputPorts)
    {
        ThrowIfDisposed();

        var physicalInputs = GetPorts(
            flags: JackPortFlags.IsPhysical | JackPortFlags.IsInput);

        int count  = Math.Min(ourOutputPorts.Count, physicalInputs.Length);
        int wired  = 0;
        for (int i = 0; i < count; i++)
        {
            if (Connect(ourOutputPorts[i], physicalInputs[i]))
                wired++;
        }
        return wired;
    }

    /// <summary>
    /// Overload that accepts port handles. Resolves full names via <see cref="GetPortName"/>
    /// before connecting.
    /// </summary>
    public int AutoConnectToPhysicalOutputs(IReadOnlyList<nint> ourOutputPorts)
    {
        var names = new List<string>(ourOutputPorts.Count);
        foreach (var p in ourOutputPorts)
        {
            var name = GetPortName(p);
            if (name is not null) names.Add(name);
        }
        return AutoConnectToPhysicalOutputs(names);
    }

    // ── Time ──────────────────────────────────────────────────────────────

    /// <summary>Estimated current frame time (use from non-process threads).</summary>
    public uint GetFrameTime()       => Native.jack_frame_time(_client);

    /// <summary>Exact start-of-cycle frame time (use only from process callback).</summary>
    public uint GetLastFrameTime()   => Native.jack_last_frame_time(_client);

    /// <summary>Converts frame count to microseconds.</summary>
    public ulong FramesToTime(uint frames) => Native.jack_frames_to_time(_client, frames);

    /// <summary>Converts microseconds to frame count.</summary>
    public uint TimeToFrames(ulong usecs) => Native.jack_time_to_frames(_client, usecs);

    /// <summary>JACK monotonic clock in microseconds (independent of client state).</summary>
    public static ulong GetTime() => Native.jack_get_time();

    /// <summary>JACK version string (e.g. "1.9.21").</summary>
    public static string? GetVersionString() => Native.jack_get_version_string();

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Log.LogInformation("Disposing JackClient '{ClientName}'", ClientName);

        if (_activated)
        {
            Native.jack_deactivate(_client);
            _activated = false;
        }

        if (_client != nint.Zero)
        {
            Native.jack_client_close(_client);
            _client = nint.Zero;
        }

        ReleaseHandle(ref _processHandle);
        ReleaseHandle(ref _shutdownHandle);
        ReleaseHandle(ref _bufferSizeHandle);
        ReleaseHandle(ref _sampleRateHandle);
        ReleaseHandle(ref _xRunHandle);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private T EnsureOpen<T>(T value)
    {
        ThrowIfDisposed();
        return value;
    }

    private static void ReleaseHandle(ref GCHandle handle)
    {
        if (handle.IsAllocated) handle.Free();
        handle = default;
    }
}

