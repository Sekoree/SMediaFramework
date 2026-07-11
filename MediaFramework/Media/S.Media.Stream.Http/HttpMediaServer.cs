using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace S.Media.Stream.Http;

/// <summary>
/// Minimal raw-socket HTTP/1.1 server for LAN playback - deliberately not ASP.NET (AOT-safe, two
/// routes). Serves the live MPEG-TS broadcast at <c>/stream.ts</c> (close-delimited stream, plays in
/// VLC/mpv/ffplay) and the FFmpeg hls muxer's output at <c>/hls/live.m3u8</c> + segments. GET/HEAD
/// only; anything else is 404/405. One task per connection, capped.
/// </summary>
internal sealed class HttpMediaServer : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.Stream.Http.HttpMediaServer");

    private const int MaxConcurrentClients = 32;

    private readonly TsFanOutBuffer? _tsBuffer;
    private readonly string? _hlsDirectory;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _activeClients;
    private long _bytesServed;

    public HttpMediaServer(int port, TsFanOutBuffer? tsBuffer, string? hlsDirectory, IPAddress? bindAddress = null)
    {
        if (tsBuffer is null && hlsDirectory is null)
            throw new ArgumentException("server needs at least one of: TS buffer, HLS directory.");
        _tsBuffer = tsBuffer;
        _hlsDirectory = hlsDirectory;
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        Trace.LogInformation("LAN media server listening on port {Port} (ts={Ts} hls={Hls})",
            Port, tsBuffer is not null, hlsDirectory is not null);
    }

    /// <summary>Actual bound port (relevant when constructed with port 0).</summary>
    public int Port { get; }

    public int ActiveClients => Volatile.Read(ref _activeClients);

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    public long TsClientsEvicted => _tsBuffer?.EvictedClients ?? 0;

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

            if (Volatile.Read(ref _activeClients) >= MaxConcurrentClients)
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        Interlocked.Increment(ref _activeClients);
        try
        {
            client.NoDelay = true;
            using var _ = client;
            var stream = client.GetStream();
            var (method, path) = await ReadRequestAsync(stream, _cts.Token).ConfigureAwait(false);
            if (method is null || path is null)
                return;
            if (method is not ("GET" or "HEAD"))
            {
                await WriteSimpleResponseAsync(stream, "405 Method Not Allowed", "text/plain", "GET only"u8.ToArray()).ConfigureAwait(false);
                return;
            }

            var headOnly = method == "HEAD";
            switch (path)
            {
                case "/stream.ts" when _tsBuffer is not null:
                    await ServeTsStreamAsync(stream, headOnly).ConfigureAwait(false);
                    break;
                case "/" or "/index.html" or "/status":
                    await ServeStatusAsync(stream).ConfigureAwait(false);
                    break;
                default:
                    if (_hlsDirectory is not null && path.StartsWith("/hls/", StringComparison.Ordinal))
                        await ServeHlsFileAsync(stream, path, headOnly).ConfigureAwait(false);
                    else
                        await WriteSimpleResponseAsync(stream, "404 Not Found", "text/plain", "not found"u8.ToArray()).ConfigureAwait(false);
                    break;
            }
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
        // Read until the blank line ending the header block (tiny requests; 8 KB cap).
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

    private async Task ServeTsStreamAsync(NetworkStream stream, bool headOnly)
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

        var reader = _tsBuffer!.Register(out var registration);
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await stream.WriteAsync(chunk, _cts.Token).ConfigureAwait(false);
                Interlocked.Add(ref _bytesServed, chunk.Length);
            }
        }
        finally
        {
            _tsBuffer.Unregister(registration);
        }
    }

    private async Task ServeHlsFileAsync(NetworkStream stream, string path, bool headOnly)
    {
        // Strict allowlist: file name only (no separators/dot-dot), known extensions, inside the scratch dir.
        var fileName = path["/hls/".Length..];
        if (fileName.Length == 0
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal)
            || !(fileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)))
        {
            await WriteSimpleResponseAsync(stream, "404 Not Found", "text/plain", "not found"u8.ToArray()).ConfigureAwait(false);
            return;
        }

        var fullPath = Path.Combine(_hlsDirectory!, fileName);
        byte[] content;
        try
        {
            // Segments rotate (delete_segments) - a read can race deletion; treat as 404.
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
        Interlocked.Add(ref _bytesServed, content.Length);
    }

    private async Task ServeStatusAsync(NetworkStream stream)
    {
        var body = Encoding.UTF8.GetBytes(
            $"HaPlay LAN stream\n" +
            $"ts: {(_tsBuffer is not null ? "/stream.ts" : "off")}\n" +
            $"hls: {(_hlsDirectory is not null ? "/hls/live.m3u8" : "off")}\n" +
            $"clients: {ActiveClients}\n");
        await WriteSimpleResponseAsync(stream, "200 OK", "text/plain; charset=utf-8", body).ConfigureAwait(false);
    }

    private async Task WriteSimpleResponseAsync(
        NetworkStream stream, string status, string contentType, byte[] body, int? contentLength = null)
    {
        var headers =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {contentLength ?? body.Length}\r\n" +
            "Cache-Control: no-cache, no-store\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), _cts.Token).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, _cts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); }
        catch { /* best effort */ }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* best effort */ }
        _cts.Dispose();
    }
}
