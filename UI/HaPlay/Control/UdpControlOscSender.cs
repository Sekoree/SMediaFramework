using System.Net;
using HaPlay.Models;
using OSCLib;

namespace HaPlay.ControlGraph;

/// <summary>An OSC reply received on a sending client's connected socket (e.g. an X32 answering us).</summary>
public sealed record ControlOscReceivedMessage(string Host, int Port, OSCMessageContext Context);

/// <summary>Surfaces replies the peer sends back to our OSC client sockets so the host can route them.</summary>
public interface IControlOscReceiver
{
    event Action<ControlOscReceivedMessage>? MessageReceived;
}

/// <summary>
/// Real OSC output transport for the live control system. Caches one <see cref="OSCClient"/> per
/// remote host/port. Each client also listens on its own connected socket, so replies the peer sends back
/// to the request's source port (the X32 answering an address-only query or streaming <c>/xremote</c>
/// updates) surface via <see cref="MessageReceived"/>. Host resolution and send failures are handled
/// defensively: a bad host surfaces as a send exception that the command router turns into a monitor
/// failure row rather than crashing the session.
/// </summary>
public sealed class UdpControlOscSender : IControlOscSender, IControlOscReceiver, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Host, int Port), OSCClient> _clients = new();
    private readonly ControlSystemConfig? _config;
    private bool _disposed;

    /// <param name="config">
    /// Optional control config used to bind a device's client socket to its configured fixed local port
    /// (<see cref="ControlDeviceBindingConfig.OscLocalPort"/>). When null, sockets use an ephemeral port.
    /// </param>
    public UdpControlOscSender(ControlSystemConfig? config = null)
    {
        _config = config;
    }

    public event Action<ControlOscReceivedMessage>? MessageReceived;

    public ValueTask SendAsync(
        string host,
        int port,
        string address,
        IReadOnlyList<OSCArgument> arguments,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = GetClient(host, port);
        return client.SendMessageAsync(address, arguments, cancellationToken);
    }

    private OSCClient GetClient(string host, int port)
    {
        var key = (host, port);
        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var existing))
                return existing;

            var client = new OSCClient(new IPEndPoint(ResolveAddress(host), port), localPort: ResolveLocalPort(host, port));
            // Receive replies on this client's own socket and surface them to the host. The X32 replies
            // to the source port of the request, which is this connected socket — not a separate listener.
            client.RegisterHandler("//", (context, _) =>
            {
                MessageReceived?.Invoke(new ControlOscReceivedMessage(host, port, context));
                return ValueTask.CompletedTask;
            });
            _clients[key] = client;
            return client;
        }
    }

    private int? ResolveLocalPort(string host, int port) =>
        _config?.Devices.FirstOrDefault(d =>
            d.Protocol == ControlDeviceProtocol.Osc
            && d.Binding.OscPort == port
            && string.Equals(d.Binding.OscHost, host, StringComparison.OrdinalIgnoreCase))
            ?.Binding.OscLocalPort;

    private static IPAddress ResolveAddress(string host)
    {
        if (IPAddress.TryParse(host, out var address))
            return address;

        // Hostnames are uncommon for control gear (X32 is an IP), but resolve defensively.
        var resolved = Dns.GetHostAddresses(host);
        return resolved.Length > 0
            ? resolved[0]
            : throw new ArgumentException($"Could not resolve OSC host '{host}'.", nameof(host));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        lock (_gate)
        {
            foreach (var client in _clients.Values)
                client.Dispose();
            _clients.Clear();
        }
    }
}
