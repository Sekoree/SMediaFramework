using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OSCLib;

public sealed class OSCClient : IOSCClient
{
    private const int MaxBundleDepth = 32;

    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _remoteEndPoint;
    private readonly ILogger<OSCClient> _logger;
    private readonly OSCRouter _router = new();
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    /// <summary>
    /// Creates an <see cref="OSCClient"/> for the given endpoint.
    /// </summary>
    /// <param name="remoteEndPoint">The peer to connect to.</param>
    /// <param name="options">Client options.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="localPort">
    /// Optional fixed local UDP port to bind the socket to (so replies arrive on a known source port).
    /// When null, the OS assigns an ephemeral port. Use 0 for an explicit ephemeral bind.
    /// </param>
    public OSCClient(IPEndPoint remoteEndPoint, OSCClientOptions? options = null, ILogger<OSCClient>? logger = null, int? localPort = null)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        if (localPort is { } lp && (lp < IPEndPoint.MinPort || lp > IPEndPoint.MaxPort))
            throw new ArgumentOutOfRangeException(nameof(localPort), lp, "Local port must be between 0 and 65535.");

        Options = options ?? new OSCClientOptions();
        if (Options.MaxPacketBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxPacketBytes), Options.MaxPacketBytes, "MaxPacketBytes must be greater than 0.");

        _remoteEndPoint = remoteEndPoint;
        _logger = logger ?? NullLogger<OSCClient>.Instance;

        _udpClient = localPort is { } port
            ? new UdpClient(port, remoteEndPoint.AddressFamily)
            : new UdpClient(remoteEndPoint.AddressFamily);
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
                "OSC packet size {Size}B exceeds configured max {Max}B; send aborted.",
                encoded.Length, Options.MaxPacketBytes);
            throw new OSCPacketTooLargeException(encoded.Length, Options.MaxPacketBytes);
        }

        _logger.LogDebug(
            "Sending OSC {Kind} to {Endpoint} ({Bytes}B)",
            packet.Kind, _remoteEndPoint, encoded.Length);

        // Use the connected-default overload — Connect() was already called in the constructor.
        await _udpClient.SendAsync(encoded.Memory, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask SendMessageAsync(string address, IReadOnlyList<OSCArgument>? arguments = null, CancellationToken cancellationToken = default)
        => SendAsync(OSCPacket.FromMessage(new OSCMessage(address, arguments)), cancellationToken);

    /// <summary>
    /// Registers a handler for replies the connected peer sends back to this client's socket (e.g. an X32
    /// answering an address-only query or streaming <c>/xremote</c> updates). The first registration starts
    /// the background receive loop on the same connected socket used for sending.
    /// </summary>
    public IDisposable RegisterHandler(string addressPattern, OSCMessageHandler handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var registration = _router.Register(addressPattern, handler);
        EnsureReceiving();
        return registration;
    }

    private void EnsureReceiving()
    {
        if (_receiveTask is not null || _disposed)
            return;

        _receiveCts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var decodeOptions = new OSCDecodeOptions();
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OSC client socket receive failed for {RemoteEndPoint}.", _remoteEndPoint);
                continue;
            }

            var receivedAt = DateTimeOffset.UtcNow;
            if (received.Buffer.Length > Options.MaxPacketBytes)
                continue;

            if (!OSCPacketCodec.TryDecode(received.Buffer, decodeOptions, out var packet, out var error))
            {
                _logger.LogWarning("Failed to decode OSC reply from {Remote}: {Error}", received.RemoteEndPoint, error);
                continue;
            }

            await DispatchPacketAsync(packet!, received.RemoteEndPoint, bundleTimeTag: null, receivedAt, cancellationToken, depth: 0)
                .ConfigureAwait(false);
        }
    }

    private async Task DispatchPacketAsync(
        OSCPacket packet,
        IPEndPoint remote,
        OSCTimeTag? bundleTimeTag,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken,
        int depth)
    {
        if (packet.Kind == OSCPacketKind.Message)
        {
            var context = new OSCMessageContext(packet.Message!, remote, bundleTimeTag, receivedAt);
            _ = await _router.DispatchAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (depth >= MaxBundleDepth)
            return;

        var bundle = packet.Bundle!;
        foreach (var child in bundle.Elements)
            await DispatchPacketAsync(child, remote, bundle.TimeTag, receivedAt, cancellationToken, depth + 1).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _receiveCts?.Cancel();
        _udpClient.Dispose();
        _receiveCts?.Dispose();
        _logger.LogDebug("OSC client disposed (sync) for {RemoteEndPoint}", _remoteEndPoint);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

}
