using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace HaPlay.Remote;

/// <summary>
/// Minimal HTTP front-end for <see cref="RemoteApiDispatcher"/> built on <see cref="HttpListener"/>
/// (no web-framework dependency — the app publishes NativeAOT). Listens on all interfaces so show
/// controllers (Companion, touch panels, curl) on the LAN can reach it; on Windows, where the
/// wildcard prefix needs a URL ACL, it falls back to loopback-only and says so.
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
    public bool Start(int port, RemoteApiDispatcher dispatcher)
    {
        Stop();
        StatusNote = null;

        var listener = TryStart($"http://*:{port}/", out var error);
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

        _listener = listener;
        _cts = new CancellationTokenSource();
        BaseUrl = $"http://{ResolveAdvertisedHost()}:{port}";
        _ = AcceptLoopAsync(listener, dispatcher, _cts.Token);
        Trace.LogInformation("RestApiServer: listening on port {Port} (advertised {BaseUrl})", port, BaseUrl);
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

    private async Task AcceptLoopAsync(HttpListener listener, RemoteApiDispatcher dispatcher, CancellationToken ct)
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

            _ = HandleRequestAsync(context, dispatcher);
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context, RemoteApiDispatcher dispatcher)
    {
        var response = context.Response;
        try
        {
            // Permissive CORS so browser-based remotes (tablet dashboards) can call straight in.
            response.Headers["Access-Control-Allow-Origin"] = "*";
            response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            response.Headers["Access-Control-Allow-Headers"] = "Content-Type";

            if (context.Request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
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

            var result = await dispatcher.ExecuteAsync(
                context.Request.HttpMethod,
                context.Request.Url?.AbsolutePath ?? "/",
                query).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(result.Body);
            response.StatusCode = result.Status;
            response.ContentType = "application/json";
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            response.Close();
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "RestApiServer: request failed");
            try { response.Abort(); } catch { /* best effort */ }
        }
    }

    /// <summary>Best host to advertise in copy-paste URLs: the first up, non-loopback IPv4.</summary>
    internal static string ResolveAdvertisedHost()
    {
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
