using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Remote;

/// <summary>
/// Minimal HTTP front-end for <see cref="RemoteApiDispatcher"/> built on <see cref="HttpListener"/>
/// (no web-framework dependency — the app publishes NativeAOT). Binds loopback by default; LAN
/// binding is an explicit operator choice and every request must carry the per-machine access token.
/// </summary>
public sealed class RestApiServer : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Remote.RestApiServer");

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Human-facing base URL (LAN address when available) — what Copy-API-URL uses.</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>Non-null when the last <see cref="Start"/> failed or degraded (e.g. loopback fallback).</summary>
    public string? StatusNote { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Starts (or restarts) the listener. Returns false when no prefix could be bound.</summary>
    public bool Start(
        int port,
        RemoteApiDispatcher dispatcher,
        string accessToken,
        bool bindAllInterfaces = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        Stop();
        StatusNote = null;

        HttpListener? listener;
        if (bindAllInterfaces)
        {
            listener = TryStart($"http://*:{port}/", out var error);
            if (listener is null)
            {
                // Windows refuses wildcard prefixes without an ACL (netsh http add urlacl) — degrade
                // to loopback so the API still works locally rather than not at all.
                listener = TryStart($"http://localhost:{port}/", out var loopbackError);
                if (listener is null)
                {
                    StatusNote = loopbackError ?? error;
                    Trace.LogWarning("RestApiServer: could not bind port {Port}: {Error}", port, StatusNote);
                    return false;
                }

                StatusNote = "Bound to localhost only (wildcard binding was refused; on Windows add a URL ACL for LAN access).";
            }
        }
        else
        {
            listener = TryStart($"http://localhost:{port}/", out var loopbackError);
            if (listener is null)
            {
                StatusNote = loopbackError;
                Trace.LogWarning("RestApiServer: could not bind loopback port {Port}: {Error}", port, StatusNote);
                return false;
            }
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        BaseUrl = $"http://{ResolveAdvertisedHost(bindAllInterfaces && StatusNote is null)}:{port}";
        _ = AcceptLoopAsync(listener, dispatcher, accessToken, _cts.Token);
        Trace.LogInformation("RestApiServer: listening on port {Port} (advertised {BaseUrl}, lan={Lan})", port, BaseUrl, bindAllInterfaces && StatusNote is null);
        return true;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* best effort */ }
        _cts?.Dispose();
        _cts = null;
        if (_listener is { } listener)
        {
            _listener = null;
            try { listener.Stop(); } catch { /* best effort */ }
            try { listener.Close(); } catch { /* best effort */ }
        }

        BaseUrl = null;
    }

    public void Dispose() => Stop();

    private static HttpListener? TryStart(string prefix, out string? error)
    {
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(prefix);
            listener.Start();
            error = null;
            return listener;
        }
        catch (Exception ex)
        {
            try { listener.Close(); } catch { /* best effort */ }
            error = ex.Message;
            return null;
        }
    }

    private async Task AcceptLoopAsync(
        HttpListener listener,
        RemoteApiDispatcher dispatcher,
        string accessToken,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || !listener.IsListening)
            {
                return; // shutdown
            }
            catch (Exception ex)
            {
                Trace.LogWarning(ex, "RestApiServer: accept failed");
                continue;
            }

            _ = HandleRequestAsync(context, dispatcher, accessToken);
        }
    }

    private static async Task HandleRequestAsync(
        HttpListenerContext context,
        RemoteApiDispatcher dispatcher,
        string accessToken)
    {
        var response = context.Response;
        var started = Stopwatch.GetTimestamp();
        var method = context.Request.HttpMethod;
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var remote = context.Request.RemoteEndPoint?.ToString() ?? "<unknown>";
        var statusCode = 500;
        var authorized = false;
        try
        {
            Trace.LogTrace("RestApiServer: request started method={Method} path={Path} remote={Remote}", method, path, remote);
            if (method == "OPTIONS")
            {
                statusCode = 204;
                response.StatusCode = statusCode;
                response.Headers["Allow"] = "GET, POST, OPTIONS";
                response.Close();
                return;
            }

            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queryString = context.Request.QueryString;
            foreach (var key in queryString.AllKeys)
            {
                if (key is not null && queryString[key] is { } value)
                    query[key] = value;
            }

            if (!IsAuthorized(context.Request, query, accessToken))
            {
                statusCode = 401;
                await WriteResultAsync(response, RemoteApiResult.Fail(statusCode, "Remote API token required.")).ConfigureAwait(false);
                return;
            }
            authorized = true;

            var result = await dispatcher.ExecuteAsync(
                method,
                path,
                query).ConfigureAwait(false);

            statusCode = result.Status;
            await WriteResultAsync(response, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "RestApiServer: request failed");
            try { response.Abort(); } catch { /* best effort */ }
        }
        finally
        {
            var elapsedMs = MediaDiagnostics.ElapsedMillisecondsSince(started);
            var level = elapsedMs >= 250 || statusCode >= 500 ? LogLevel.Warning : LogLevel.Debug;
            if (Trace.IsEnabled(level))
            {
                Trace.Log(
                    level,
                    "RestApiServer: request completed method={Method} path={Path} remote={Remote} status={Status} authorized={Authorized} elapsedMs={ElapsedMs:0.00}",
                    method,
                    path,
                    remote,
                    statusCode,
                    authorized,
                    elapsedMs);
            }
        }
    }

    private static async Task WriteResultAsync(HttpListenerResponse response, RemoteApiResult result)
    {
        var payload = Encoding.UTF8.GetBytes(result.Body);
        response.StatusCode = result.Status;
        response.ContentType = "application/json";
        response.ContentLength64 = payload.Length;
        await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        response.Close();
    }

    private static bool IsAuthorized(HttpListenerRequest request, IReadOnlyDictionary<string, string> query, string accessToken)
    {
        if (query.TryGetValue("key", out var key) && FixedTimeEquals(key, accessToken))
            return true;
        if (query.TryGetValue("token", out var token) && FixedTimeEquals(token, accessToken))
            return true;

        var header = request.Headers["Authorization"];
        if (header is not null
            && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && FixedTimeEquals(header["Bearer ".Length..].Trim(), accessToken))
            return true;

        return FixedTimeEquals(request.Headers["X-HaPlay-Api-Key"], accessToken);
    }

    private static bool FixedTimeEquals(string? candidate, string expected)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;

        var max = Math.Max(candidate.Length, expected.Length);
        var diff = candidate.Length ^ expected.Length;
        for (var i = 0; i < max; i++)
        {
            var a = i < candidate.Length ? candidate[i] : 0;
            var b = i < expected.Length ? expected[i] : 0;
            diff |= a ^ b;
        }

        return diff == 0;
    }

    /// <summary>Best host to advertise in copy-paste URLs: the first up, non-loopback IPv4.</summary>
    internal static string ResolveAdvertisedHost(bool preferLan = true)
    {
        if (!preferLan)
            return "localhost";

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Trace.LogDebug(ex, "RestApiServer: LAN address probe failed");
        }

        return "localhost";
    }
}
