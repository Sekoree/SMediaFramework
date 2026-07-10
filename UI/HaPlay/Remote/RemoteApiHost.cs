namespace HaPlay.Remote;

/// <summary>
/// APP-02: owns the remote-API listener lifecycle that used to live inline in <c>MainViewModel</c> - the
/// <see cref="RestApiServer"/> instance and the lazily-built <see cref="RemoteApiDispatcher"/>, plus the
/// stop/start dance on every settings change. The view model keeps only the bound settings (enabled/port/
/// token/LAN) and presentation; this service turns those into a running (or stopped) listener with an explicit
/// lifetime (<see cref="Dispose"/> stops and closes it).
/// </summary>
public sealed class RemoteApiHost : IDisposable
{
    private readonly RestApiServer _server = new();
    private RemoteApiDispatcher? _dispatcher;

    public bool IsRunning => _server.IsRunning;

    /// <summary>Advertised base URL of the running listener, or null when stopped/unbound.</summary>
    public string? BaseUrl => _server.BaseUrl;

    /// <summary>Degradation note (e.g. Windows loopback fallback) or bind error; null when clean.</summary>
    public string? StatusNote => _server.StatusNote;

    /// <summary>
    /// Reconciles the running listener with the given settings: (re)starts it when <paramref name="enabled"/>
    /// and the port is valid, otherwise stops it. The dispatcher is built once via <paramref name="dispatcherFactory"/>
    /// and reused across restarts (it closes over the live view-model collections).
    /// </summary>
    public void Restart(bool enabled, int port, string? accessToken, bool allowLan, Func<RemoteApiDispatcher> dispatcherFactory)
    {
        ArgumentNullException.ThrowIfNull(dispatcherFactory);
        _dispatcher ??= dispatcherFactory();
        _server.Stop();
        if (enabled && port is >= 1 and <= 65535)
            _server.Start(port, _dispatcher, accessToken, allowLan);
    }

    /// <summary>The URL to advertise for Copy-API-URL menus: the live listener's URL when running, else the
    /// best-known host for the configured port so a copied URL becomes live the moment the API is enabled.</summary>
    public string AdvertisedBaseUrl(int port, bool allowLan) =>
        BaseUrl ?? $"http://{RestApiServer.ResolveAdvertisedHost(allowLan)}:{port}";

    public void Dispose() => _server.Dispose();
}
