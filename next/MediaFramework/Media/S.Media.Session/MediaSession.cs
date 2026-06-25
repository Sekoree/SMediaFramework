using S.Media.Players;
using S.Media.Routing;
using S.Media.Core.Diagnostics;

namespace S.Media.Session;

/// <summary>
/// A single owning handle around a <see cref="MediaPlayer"/> and the resources wired around it
/// (outputs, companion hosts, app-scoped disposables). Dispose the session and everything it owns is
/// torn down in a safe order — the player first (which stops its routers/decoder so nothing is still
/// pushing to an output), then the registered resources in reverse registration order.
/// </summary>
/// <remarks>
/// <para>
/// Resources the caller wants to keep are simply never registered (Borrow). Use <see cref="Owning"/>
/// when the session should dispose the player, or <see cref="Borrowing"/> when the caller keeps the
/// player and the session only owns the extras attached via <see cref="Own{T}"/>.
/// </para>
/// <para>
/// Implements <see cref="IAsyncDisposable"/> (which <see cref="MediaPlayer"/> alone does not) so a
/// registered resource that needs a native drain — an encoder/file output, a router flush — can be
/// awaited rather than blocked. <see cref="Dispose"/> remains available for synchronous hosts.
/// </para>
/// </remarks>
public sealed class MediaSession : IDisposable, IAsyncDisposable
{
    private readonly bool _ownsPlayer;
    private readonly List<object> _owned = [];
    private readonly Lock _gate = new();
    private bool _disposed;

    private MediaSession(MediaPlayer player, bool ownsPlayer)
    {
        Player = player ?? throw new ArgumentNullException(nameof(player));
        _ownsPlayer = ownsPlayer;
    }

    /// <summary>The owned/borrowed player. Drive transport (Play/Pause/Seek) through this.</summary>
    public MediaPlayer Player { get; }

    /// <summary>Creates a session that owns and disposes <paramref name="player"/>.</summary>
    public static MediaSession Owning(MediaPlayer player) => new(player, ownsPlayer: true);

    /// <summary>
    /// Creates a session over a <paramref name="player"/> the caller keeps (Borrow): the session disposes
    /// only the resources registered via <see cref="Own{T}"/>, never the player. The caller must ensure
    /// the borrowed player is not still pushing to those resources when the session is disposed.
    /// </summary>
    public static MediaSession Borrowing(MediaPlayer player) => new(player, ownsPlayer: false);

    /// <summary>
    /// Registers <paramref name="resource"/> for disposal with the session — after the player, and before
    /// any resource registered earlier (reverse order). Returns it so the call can be assigned/chained.
    /// <see cref="IAsyncDisposable"/> resources are awaited on <see cref="DisposeAsync"/> and disposed
    /// synchronously on <see cref="Dispose"/>.
    /// </summary>
    public T Own<T>(T resource) where T : class
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource is not (IDisposable or IAsyncDisposable))
            throw new ArgumentException("resource must implement IDisposable or IAsyncDisposable.", nameof(resource));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _owned.Add(resource);
        }
        return resource;
    }

    public void Dispose()
    {
        if (!TryBeginDispose(out var owned))
            return;

        DisposePlayerIfOwned();
        for (var i = owned.Count - 1; i >= 0; i--)
            DisposeResourceSync(owned[i]);
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose(out var owned))
            return;

        DisposePlayerIfOwned();
        for (var i = owned.Count - 1; i >= 0; i--)
            await DisposeResourceAsync(owned[i]).ConfigureAwait(false);
    }

    private bool TryBeginDispose(out List<object> owned)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                owned = [];
                return false;
            }
            _disposed = true;
            owned = [.. _owned];
            _owned.Clear();
            return true;
        }
    }

    private void DisposePlayerIfOwned()
    {
        if (_ownsPlayer)
            MediaDiagnostics.SwallowDisposeErrors(Player.Dispose, "MediaSession: player");
    }

    private static void DisposeResourceSync(object resource)
    {
        switch (resource)
        {
            case IDisposable d:
                MediaDiagnostics.SwallowDisposeErrors(d.Dispose, "MediaSession: owned resource");
                break;
            case IAsyncDisposable a:
                MediaDiagnostics.SwallowDisposeErrors(
                    () => a.DisposeAsync().AsTask().GetAwaiter().GetResult(),
                    "MediaSession: owned async resource (sync dispose)");
                break;
        }
    }

    private static async ValueTask DisposeResourceAsync(object resource)
    {
        try
        {
            switch (resource)
            {
                case IAsyncDisposable a:
                    await a.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable d:
                    d.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            MediaDiagnostics.LogError(ex, "MediaSession: owned resource DisposeAsync");
        }
    }
}
