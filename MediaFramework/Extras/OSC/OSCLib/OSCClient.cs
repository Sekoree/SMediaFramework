using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OSCLib;

public sealed class OSCClient : IOSCClient
{
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly ILogger<OSCClient> _logger;
    private bool _disposed;

    /// <summary>
    /// Creates an <see cref="OSCClient"/> for the given endpoint.
    /// </summary>
    public OSCClient(IPEndPoint remoteEndPoint, OSCClientOptions? options = null, ILogger<OSCClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        Options = options ?? new OSCClientOptions();
        if (Options.MaxPacketBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPacketBytes), Options.MaxPacketBytes, "MaxPacketBytes must be greater than 0.");

        _remoteEndPoint = remoteEndPoint;
        _logger = logger ?? NullLogger<OSCClient>.Instance;

        _udpClient = new UdpClient(remoteEndPoint.AddressFamily);
        if (Options.EnableBroadcast)
            _udpClient.EnableBroadcast = true;

        _udpClient.Connect(remoteEndPoint);
    }

    /// <summary>
    /// Creates an <see cref="OSCClient"/> by resolving <paramref name="host"/> asynchronously.
    /// Prefer this factory over the synchronous string-host constructor to avoid blocking
    /// the calling thread during DNS resolution.
    /// </summary>
    public static async Task<OSCClient> CreateAsync(
        string host,
        int port,
        OSCClientOptions? options = null,
        ILogger<OSCClient>? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port < IPEndPoint.MinPort || port > IPEndPoint.MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var address = addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"No IP addresses were resolved for host '{host}'.");

        return new OSCClient(new IPEndPoint(address, port), options, logger);
    }


    public OSCClientOptions Options { get; }

    public async ValueTask SendAsync(OSCPacket packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packet);

        using var encoded = OSCPacketCodec.EncodeToRented(packet);

        if (encoded.Length > Options.MaxPacketBytes)
        {
            _logger.LogWarning(
                "OSC packet size {Size}B exceeds configured max {Max}B — send aborted.",
                encoded.Length, Options.MaxPacketBytes);
            return; // P3.22: silently drop instead of throwing — consistent with server-side policy.
        }

        _logger.LogDebug(
            "Sending OSC {Kind} to {Endpoint} ({Bytes}B)",
            packet.Kind, _remoteEndPoint, encoded.Length);

        // Use the connected-default overload — Connect() was already called in the constructor.
        await _udpClient.SendAsync(encoded.Memory, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SendMessageAsync(string address, IReadOnlyList<OSCArgument>? arguments = null, CancellationToken cancellationToken = default)
        => SendAsync(OSCPacket.FromMessage(new OSCMessage(address, arguments)), cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;

        _udpClient.Dispose();
        _disposed = true;
        _logger.LogDebug("OSC client disposed (sync) for {RemoteEndPoint}", _remoteEndPoint);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _udpClient.Dispose();
        _disposed = true;
        _logger.LogDebug("OSC client disposed for {RemoteEndPoint}", _remoteEndPoint);
        return ValueTask.CompletedTask;
    }

}
