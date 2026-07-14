using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace S.Media.Stream.Http;

/// <summary>One named endpoint on a shared server: its TS broadcast buffer and/or HLS scratch dir.</summary>
internal sealed class HttpMount
{
    public required string Name { get; init; }
    public TsFanOutBuffer? TsBuffer { get; init; }
    public string? HlsDirectory { get; init; }
    public long BytesServed;
    public int ActiveClients;
}

/// <summary>
/// Minimal raw-socket HTTP/1.1 server for LAN playback - deliberately not ASP.NET (AOT-safe). ONE
/// server per port, MULTIPLEXING several named mounts so multiple live streams can share a port under
/// different names: <c>/&lt;mount&gt;.ts</c> (MPEG-TS broadcast, plays in VLC/mpv/ffplay) and
/// <c>/&lt;mount&gt;/hls/live.m3u8</c> (+ segments). GET/HEAD only. Reference-counted per mount; the
/// listener stops when its last mount is released. Acquire via <see cref="AcquireMount"/>.
/// </summary>
internal sealed class HttpMediaServer : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Stream.Http.HttpMediaServer");
    private const int MaxConcurrentClients = 64;

    // One server per bound port, shared across streams. Keyed by the REQUESTED port (0 excluded - an
    // ephemeral-port server is never shared since its port isn't known in advance).
    private static readonly Lock ServersGate = new();
    private static readonly Dictionary<int, HttpMediaServer> ServersByPort = new();

    private readonly ConcurrentDictionary<string, HttpMount> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _requestedPort;
    private readonly IPAddress _bindAddress;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _activeClients;
    private bool _disposed;

    private HttpMediaServer(int requestedPort, IPAddress bindAddress)
    {
        _requestedPort = requestedPort;
        _bindAddress = bindAddress;
        _listener = new TcpListener(bindAddress, requestedPort);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        Trace.LogInformation("LAN media server listening on port {Port}", Port);
    }

    public int Port { get; }

    public int ActiveClients => Volatile.Read(ref _activeClients);

    /// <summary>Registers a mount, creating/sharing the port's server. Dispose the handle to release
    /// it; the server stops when its last mount is released. Throws when the mount name is already in
    /// use on that port (two streams can't claim the same endpoint).</summary>
    public static MountHandle AcquireMount(
        int port, string mountName, TsFanOutBuffer? tsBuffer, string? hlsDirectory, IPAddress? bindAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountName);
        if (port is < 0 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(port), port, "port must be between 0 and 65535.");
        if (tsBuffer is null && hlsDirectory is null)
            throw new ArgumentException("a mount needs at least one of: TS buffer, HLS directory.");
        var normalized = NormalizeMount(mountName);
        var requestedAddress = bindAddress ?? IPAddress.Any;

        lock (ServersGate)
        {
            // Ephemeral (port 0) servers are never shared - each gets its own instance.
            HttpMediaServer server;
            if (port == 0)
            {
                server = new HttpMediaServer(0, requestedAddress);
            }
            else if (ServersByPort.TryGetValue(port, out var existing))
            {
                if (!existing._bindAddress.Equals(requestedAddress))
                    throw new InvalidOperationException(
                        $"port {port} is already bound to {existing._bindAddress}; it cannot also bind {requestedAddress}.");
                server = existing;
            }
            else
            {
                server = new HttpMediaServer(port, requestedAddress);
                ServersByPort[port] = server;
            }

            var mount = new HttpMount { Name = normalized, TsBuffer = tsBuffer, HlsDirectory = hlsDirectory };
            if (!server._mounts.TryAdd(normalized, mount))
            {
                if (port == 0)
                {
                    server.BeginDisposeInternal();
                    server.FinishDisposeInternal();
                }
                throw new InvalidOperationException(
                    $"the endpoint '/{normalized}' is already served on port {server.Port} - choose a different endpoint name.");
            }

            Trace.LogInformation("mount '/{Mount}' registered on port {Port} (ts={Ts} hls={Hls})",
                normalized, server.Port, tsBuffer is not null, hlsDirectory is not null);
            return new MountHandle(server, mount);
        }
    }

    private static string NormalizeMount(string name)
    {
        var trimmed = new string(name.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return trimmed.Length == 0 ? "stream" : trimmed.ToLowerInvariant();
    }

    private void ReleaseMount(string mountName)
    {
        var stop = false;
        lock (ServersGate)
        {
            _mounts.TryRemove(mountName, out _);
            if (!_mounts.IsEmpty)
                return;
            // Last mount gone - retire the server.
            if (_requestedPort != 0)
                ServersByPort.Remove(_requestedPort);
            BeginDisposeInternal();
            stop = true;
        }

        if (stop)
            FinishDisposeInternal();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested)
                    break;
                Trace.LogWarning(ex, "accept failed - continuing");
                continue;
            }

            if (Interlocked.Increment(ref _activeClients) > MaxConcurrentClients)
            {
                Interlocked.Decrement(ref _activeClients);
                client.Dispose();
                continue;
            }

            try
            {
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _activeClients);
                client.Dispose();
                Trace.LogWarning(ex, "could not dispatch accepted client");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            using var _ = client;
            var stream = client.GetStream();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var (method, path) = await ReadRequestAsync(stream, requestTimeout.Token).ConfigureAwait(false);
            if (method is null || path is null)
                return;
            if (method is not ("GET" or "HEAD"))
            {
                await WriteSimpleResponseAsync(
                    stream, "405 Method Not Allowed", "text/plain", "GET and HEAD only"u8.ToArray(),
                    extraHeaders: "Allow: GET, HEAD\r\n").ConfigureAwait(false);
                return;
            }

            var headOnly = method == "HEAD";
            if (path is "/" or "/index.html" or "/status")
            {
                await ServeStatusAsync(stream, headOnly).ConfigureAwait(false);
                return;
            }

            // /<mount>.ts  → TS broadcast for that mount.
            if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("/hls/", StringComparison.OrdinalIgnoreCase))
            {
                var mountName = path.Trim('/');
                mountName = mountName[..^3]; // strip ".ts"
                if (_mounts.TryGetValue(mountName, out var mount) && mount.TsBuffer is not null)
                {
                    Interlocked.Increment(ref mount.ActiveClients);
                    try { await ServeTsStreamAsync(stream, mount, headOnly).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref mount.ActiveClients); }
                    return;
                }
            }

            // /<mount>/hls/<file>  → the mount's HLS scratch dir.
            var hlsIndex = path.IndexOf("/hls/", StringComparison.OrdinalIgnoreCase);
            if (hlsIndex > 0)
            {
                var mountName = path[1..hlsIndex];
                var fileName = path[(hlsIndex + "/hls/".Length)..];
                if (_mounts.TryGetValue(mountName, out var mount) && mount.HlsDirectory is not null)
                {
                    Interlocked.Increment(ref mount.ActiveClients);
                    try { await ServeHlsFileAsync(stream, mount, fileName, headOnly).ConfigureAwait(false); }
                    finally { Interlocked.Decrement(ref mount.ActiveClients); }
                    return;
                }
            }

            await WriteSimpleResponseAsync(stream, "404 Not Found", "text/plain", "not found"u8.ToArray()).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client disconnects are business as usual for a live stream.
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "client handler failed");
        }
        finally
        {
            Interlocked.Decrement(ref _activeClients);
        }
    }

    private static async Task<(string? Method, string? Path)> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        var used = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), token).ConfigureAwait(false);
            if (read == 0)
                return (null, null);
            used += read;
            var text = Encoding.ASCII.GetString(buffer, 0, used);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal) || text.Contains("\n\n", StringComparison.Ordinal))
            {
                var firstLine = text.Split('\n')[0].TrimEnd('\r');
                var parts = firstLine.Split(' ');
                if (parts.Length < 2)
                    return (null, null);
                var rawPath = parts[1];
                var q = rawPath.IndexOf('?');
                return (parts[0].ToUpperInvariant(), q >= 0 ? rawPath[..q] : rawPath);
            }

            if (used >= buffer.Length)
                return (null, null);
        }
    }

    private async Task ServeTsStreamAsync(NetworkStream stream, HttpMount mount, bool headOnly)
    {
        var headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: video/mp2t\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cts.Token).ConfigureAwait(false);
        if (headOnly)
            return;

        var reader = mount.TsBuffer!.Register(out var registration);
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await stream.WriteAsync(chunk, _cts.Token).ConfigureAwait(false);
                Interlocked.Add(ref mount.BytesServed, chunk.Length);
            }
        }
        finally
        {
            mount.TsBuffer.Unregister(registration);
        }
    }

    private async Task ServeHlsFileAsync(NetworkStream stream, HttpMount mount, string fileName, bool headOnly)
    {
        // Strict allowlist: file name only (no separators/dot-dot), known extensions, inside the scratch dir.
        if (fileName.Length == 0
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)
            || !(fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)))
        {
            await WriteSimpleResponseAsync(stream, "404 Not Found", "text/plain", "not found"u8.ToArray()).ConfigureAwait(false);
            return;
        }

        var fullPath = Path.Combine(mount.HlsDirectory!, fileName);
        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(fullPath, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            await WriteSimpleResponseAsync(stream, "404 Not Found", "text/plain", "not found"u8.ToArray()).ConfigureAwait(false);
            return;
        }

        var mime = fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.apple.mpegurl"
            : "video/mp2t";
        await WriteSimpleResponseAsync(stream, "200 OK", mime, headOnly ? [] : content, content.Length).ConfigureAwait(false);
        if (!headOnly)
            Interlocked.Add(ref mount.BytesServed, content.Length);
    }

    private async Task ServeStatusAsync(NetworkStream stream, bool headOnly)
    {
        var sb = new StringBuilder($"HaPlay LAN stream server (port {Port}, {ActiveClients} clients)\n\n");
        foreach (var mount in _mounts.Values)
        {
            if (mount.TsBuffer is not null)
                sb.Append($"  /{mount.Name}.ts\n");
            if (mount.HlsDirectory is not null)
                sb.Append($"  /{mount.Name}/hls/live.m3u8\n");
        }

        var body = Encoding.UTF8.GetBytes(sb.ToString());
        await WriteSimpleResponseAsync(
            stream, "200 OK", "text/plain; charset=utf-8", headOnly ? [] : body, body.Length).ConfigureAwait(false);
    }

    private async Task WriteSimpleResponseAsync(
        NetworkStream stream, string status, string contentType, byte[] body, int? contentLength = null,
        string extraHeaders = "")
    {
        var headers =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength ?? body.Length}\r\n" +
            extraHeaders +
            "Cache-Control: no-cache, no-store\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cts.Token).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Public Dispose is a no-op safety net: lifetime is refcount-driven via ReleaseMount. A stream
        // that leaks its MountHandle won't kill the shared server for others.
    }

    private void BeginDisposeInternal()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); }
        catch { /* best effort */ }
    }

    private void FinishDisposeInternal()
    {
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }
        _cts.Dispose();
        Trace.LogInformation("LAN media server on port {Port} stopped (no mounts left)", Port);
    }

    /// <summary>A registered mount; dispose to release it (refcounted at the server).</summary>
    internal sealed class MountHandle : IDisposable
    {
        private readonly HttpMediaServer _server;
        private readonly HttpMount _mount;
        private int _released;

        internal MountHandle(HttpMediaServer server, HttpMount mount)
        {
            _server = server;
            _mount = mount;
        }

        public int Port => _server.Port;

        public int ActiveClients => Volatile.Read(ref _mount.ActiveClients);

        public long BytesServed => Volatile.Read(ref _mount.BytesServed);

        public string MountName => _mount.Name;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _server.ReleaseMount(_mount.Name);
        }
    }
}
