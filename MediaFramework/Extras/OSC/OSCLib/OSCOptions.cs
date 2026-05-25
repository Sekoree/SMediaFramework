using System.Net;

namespace OSCLib;

/// <summary>
/// Controls how inbound OSC packets are parsed.
/// </summary>
public sealed class OSCDecodeOptions
{
    /// <summary>
    /// When <see langword="true"/>, unknown type tags and malformed payloads are rejected.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool StrictMode { get; init; } = true;

    /// <summary>
    /// Allows decoding messages that omit the OSC type tag string.
    /// Default: <see langword="true"/> for compatibility with older senders.
    /// <para>
    /// <b>Note:</b> This setting takes effect regardless of <see cref="StrictMode"/>.
    /// A message without a type tag string is accepted even when <see cref="StrictMode"/> is
    /// <see langword="true"/> unless this property is explicitly set to <see langword="false"/>.
    /// </para>
    /// </summary>
    public bool AllowMissingTypeTagString { get; init; } = true;

    /// <summary>
    /// Preserves unknown arguments as <see cref="OSCUnknownArgument"/> when strict mode is disabled.
    /// </summary>
    public bool PreserveUnknownArguments { get; init; }

    /// <summary>
    /// Optional callback that returns the payload byte-length for an unknown type tag.
    /// Required when <see cref="StrictMode"/> is disabled and unknown payload bytes should be consumed.
    /// </summary>
    public Func<char, ReadOnlySpan<char>, int>? UnknownTagByteLengthResolver { get; init; }

    /// <summary>
    /// Maximum nesting depth for OSC array tags.
    /// Default: <c>16</c>.
    /// </summary>
    public int MaxArrayDepth { get; init; } = 16;

    /// <summary>
    /// Maximum nesting depth for OSC bundles. A crafted packet exceeding this depth
    /// would cause unbounded recursion and is rejected as malformed.
    /// Default: <c>8</c>.
    /// </summary>
    public int MaxBundleDepth { get; init; } = 8;
}

/// <summary>
/// Runtime settings for <see cref="OSCServer"/>.
/// </summary>
public sealed class OSCServerOptions
{
    /// <summary>
    /// Local UDP port to bind.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// Maximum accepted datagram size in bytes.
    /// Default: <c>8192</c>.
    /// </summary>
    public int MaxPacketBytes { get; init; } = 8192;

    /// <summary>
    /// Action for packets larger than <see cref="MaxPacketBytes"/>.
    /// Default: <see cref="OSCOversizePolicy.DropAndLog"/>.
    /// </summary>
    public OSCOversizePolicy OversizePolicy { get; init; } = OSCOversizePolicy.DropAndLog;

    /// <summary>
    /// Minimum interval between repeated oversize-drop warning logs.
    /// Default: 5 seconds.
    /// </summary>
    public TimeSpan OversizeLogInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Decode behavior applied to inbound datagrams.
    /// </summary>
    public OSCDecodeOptions DecodeOptions { get; init; } = new();

    /// <summary>
    /// Enables per-datagram hex dumps at <c>Trace</c> log level.
    /// </summary>
    public bool EnableTraceHexDump { get; init; }

    /// <summary>
    /// Controls whether OSC bundle timetags delay server-side dispatch.
    /// <para>
    /// Setting this to <see langword="true"/> (the default) dispatches bundles immediately regardless
    /// of their timetag. Setting it to <see langword="false"/> delays future-dated bundles until their
    /// OSC/NTP timetag; immediate and past timetags dispatch without delay.
    /// </para>
    /// <para>
    /// The <see cref="OSCMessageContext.BundleTimeTag"/> property is always populated and
    /// available to handlers.
    /// </para>
    /// </summary>
    public bool IgnoreTimeTagScheduling { get; init; } = true;

    /// <summary>
    /// If set, the server joins this multicast group immediately after binding.
    /// </summary>
    public IPAddress? MulticastGroup { get; init; }

    /// <summary>
    /// Local network interface to use when joining <see cref="MulticastGroup"/>.
    /// <see langword="null"/> selects the default interface (<see cref="IPAddress.Any"/>).
    /// </summary>
    public IPAddress? MulticastLocalAddress { get; init; }
}

/// <summary>
/// Runtime settings for <see cref="OSCClient"/>.
/// </summary>
public sealed class OSCClientOptions
{
    /// <summary>
    /// Maximum packet size the client is allowed to send.
    /// Default: <c>8192</c>.
    /// </summary>
    public int MaxPacketBytes { get; init; } = 8192;


    /// <summary>
    /// Enables sending to broadcast addresses (255.255.255.255 or subnet-directed broadcast).
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool EnableBroadcast { get; init; }
}

/// <summary>
/// Message dispatch context passed to route handlers.
/// </summary>
public readonly record struct OSCMessageContext(
    OSCMessage Message,
    IPEndPoint RemoteEndPoint,
    OSCTimeTag? BundleTimeTag,
    DateTimeOffset ReceivedAtUtc);

/// <summary>
/// Route callback signature used by <see cref="IOSCServer"/>.
/// </summary>
public delegate ValueTask OSCMessageHandler(OSCMessageContext context, CancellationToken cancellationToken);

/// <summary>
/// UDP OSC client contract.
/// </summary>
public interface IOSCClient : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Encodes and sends an OSC packet.
    /// </summary>
    ValueTask SendAsync(OSCPacket packet, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience wrapper that builds and sends a single OSC message packet.
    /// </summary>
    ValueTask SendMessageAsync(string address, IReadOnlyList<OSCArgument>? arguments = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// UDP OSC server contract.
/// </summary>
public interface IOSCServer : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Active server options.
    /// </summary>
    OSCServerOptions Options { get; }

    /// <summary>
    /// Indicates whether the receive loop is currently active.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Registers a route handler and returns a token that unregisters on dispose.
    /// </summary>
    IDisposable RegisterHandler(string addressPattern, OSCMessageHandler handler);

    /// <summary>
    /// Starts the UDP receive loop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <paramref name="cancellationToken"/> controls the lifetime of the receive loop.
    /// When cancelled, the server stops receiving datagrams and the returned task completes.
    /// This is equivalent to calling <see cref="StopAsync"/>.
    /// </para>
    /// <para>
    /// The token is linked internally — disposing the server also cancels any active receive loop.
    /// Callers may pass a scoped <see cref="CancellationToken"/> to limit the server's
    /// lifetime without explicitly calling <see cref="StopAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Token that stops the receive loop when cancelled. Passing <see cref="CancellationToken.None"/>
    /// runs until <see cref="StopAsync"/> or <see cref="IAsyncDisposable.DisposeAsync"/> is called.
    /// </param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the UDP receive loop.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
