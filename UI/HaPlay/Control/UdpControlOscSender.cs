using System.Net;
using OSCLib;

namespace HaPlay.ControlGraph;

/// <summary>
/// Real OSC output transport for the live control system. Caches one <see cref="OSCClient"/> per
/// remote host/port. Host resolution and send failures are handled defensively: a bad host surfaces as
/// a send exception that the command router turns into a monitor failure row rather than crashing the
/// session.
/// </summary>
public sealed class UdpControlOscSender : IControlOscSender, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Host, int Port), OSCClient> _clients = new();
    private bool _disposed;

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

            var client = new OSCClient(new IPEndPoint(ResolveAddress(host), port));
            _clients[key] = client;
            return client;
        }
    }

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
