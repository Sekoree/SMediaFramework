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
/// (no web-framework dependency — the app publishes NativeAOT). Binds loopback by default; LAN binding is
/// an explicit operator choice. The access token is <strong>optional</strong>: this control surface targets
/// closed-LAN automation (e.g. Bitfocus Companion), so when no token is configured every request is allowed;
/// set a token to require it (compared in constant time). GET and POST are both accepted so simple HTTP
/// action clients work without a body.
/// </summary>
public sealed class RestApiServer : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Remote.RestApiServer");

    // API-03 resource bounds. A remote controller is one or a few clients; these caps stop a hostile or
    // buggy peer from exhausting threads/memory or wedging shutdown, without affecting normal use.
    private const int MaxConcurrentRequests = 32;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopDrainTimeout = TimeSpan.FromSeconds(2);
    private const int MaxHeaderCount = 100;
    private const int MaxHeaderValueLength = 8 * 1024;
    private const int MaxQueryLength = 4 * 1024;

    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly object _inflightGate = new();
    private readonly HashSet<Task> _inflight = [];

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Human-facing base URL (LAN address when available) — what Copy-API-URL uses.</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>Non-null when the last <see cref="Start"/> failed or degraded (e.g. loopback fallback).</summary>
    public string? StatusNote { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Starts (or restarts) the listener. Returns false when no prefix could be bound.
    /// A null/empty <paramref name="accessToken"/> means no authentication is required (optional token).</summary>
    public bool Start(
        int port,
        RemoteApiDispatcher dispatcher,
        string? accessToken,
        bool bindAllInterfaces = false)
    {
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
        // Prevent new dispatch first (the accept loop checks the token), then stop the listener.
        try { _cts?.Cancel(); } catch { /* best effort */ }
        if (_listener is { } listener)
        {
            _listener = null;
            try { listener.Stop(); } catch { /* best effort */ }
            try { listener.Close(); } catch { /* best effort */ }
        }

        // API-03: await in-flight handlers with a deadline so shutdown neither hangs on a slow client nor
        // tears state out from under a live request. Handlers return promptly (they kick commands off
        // without awaiting playback), so this is normally instant.
        Task[] pending;
        lock (_inflightGate)
            pending = [.. _inflight];
        if (pending.Length > 0)
        {
            try { Task.WaitAll(pending, StopDrainTimeout); }
            catch { /* best effort — individual handlers log their own failures */ }
        }

        _cts?.Dispose();
        _cts = null;
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
        string? accessToken,
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

            // API-03: bound concurrent in-flight handlers. While at capacity the accept loop pauses here
            // (the OS TCP backlog absorbs pending connections) rather than spawning unbounded work.
            try
            {
                await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { context.Response.Abort(); } catch { /* best effort */ }
                return;
            }

            DispatchTracked(context, dispatcher, accessToken, ct);
        }
    }

    /// <summary>Starts a handler, tracks it for drain-on-<see cref="Stop"/>, and releases the concurrency
    /// slot when it finishes (API-03).</summary>
    private void DispatchTracked(
        HttpListenerContext context,
        RemoteApiDispatcher dispatcher,
        string? accessToken,
        CancellationToken serverToken)
    {
        var task = HandleTrackedAsync(context, dispatcher, accessToken, serverToken);
        lock (_inflightGate)
            _inflight.Add(task);
        _ = task.ContinueWith(
            t => { lock (_inflightGate) _inflight.Remove(t); },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleTrackedAsync(
        HttpListenerContext context,
        RemoteApiDispatcher dispatcher,
        string? accessToken,
        CancellationToken serverToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        requestCts.CancelAfter(RequestTimeout);
        try
        {
            await HandleRequestAsync(context, dispatcher, accessToken, requestCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static async Task HandleRequestAsync(
        HttpListenerContext context,
        RemoteApiDispatcher dispatcher,
        string? accessToken,
        CancellationToken requestToken)
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

            // API-03: reject oversized requests before doing any work.
            if (TryRejectOversized(context.Request, out var limitStatus, out var limitMessage))
            {
                statusCode = limitStatus;
                await WriteResultAsync(response, RemoteApiResult.Fail(statusCode, limitMessage)).ConfigureAwait(false);
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

            // Bound a hung dispatch by the per-request deadline (API-03).
            var result = await dispatcher.ExecuteAsync(method, path, query)
                .WaitAsync(requestToken).ConfigureAwait(false);

            statusCode = result.Status;
            await WriteResultAsync(response, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
        {
            statusCode = 503;
            Trace.LogWarning("RestApiServer: request {Method} {Path} from {Remote} exceeded the {Timeout}s deadline or was cancelled during shutdown", method, path, remote, RequestTimeout.TotalSeconds);
            try { response.Abort(); } catch { /* best effort */ }
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

    /// <summary>API-03: cheap header/query-size guard so a malformed or hostile request is rejected before
    /// auth or dispatch. Returns true (with a status/message) when the request should be refused.</summary>
    private static bool TryRejectOversized(HttpListenerRequest request, out int status, out string message)
    {
        var headers = request.Headers;
        if (headers.Count > MaxHeaderCount)
        {
            status = 431; // Request Header Fields Too Large
            message = "Too many request headers.";
            return true;
        }

        foreach (var key in headers.AllKeys)
        {
            if (key is null) continue;
            var value = headers[key];
            if (value is not null && value.Length > MaxHeaderValueLength)
            {
                status = 431;
                message = "Request header value too large.";
                return true;
            }
        }

        if ((request.Url?.Query.Length ?? 0) > MaxQueryLength)
        {
            status = 414; // URI Too Long
            message = "Query string too long.";
            return true;
        }

        status = 0;
        message = string.Empty;
        return false;
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

    private static bool IsAuthorized(HttpListenerRequest request, IReadOnlyDictionary<string, string> query, string? accessToken)
    {
        // Optional token: no token configured ⇒ no auth required (closed-LAN automation).
        if (string.IsNullOrEmpty(accessToken))
            return true;

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

    private static bool FixedTimeEquals(string? candidate, string? expected)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(expected))
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
