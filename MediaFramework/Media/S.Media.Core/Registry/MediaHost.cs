using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;

namespace S.Media.Core.Registry;

/// <summary>
/// The single owning host (NXT-05) for the media capability <see cref="Registry"/>, the native-runtime module
/// lifetimes it acquired (PortAudio <c>Pa_Terminate</c>, the NDI runtime, …), and reference-counted plugin
/// leases. Build one at the composition root and dispose it exactly once at process/app teardown: disposal
/// reports any still-outstanding plugin leases (a plugin object that outlived its host - a lifetime bug), then
/// disposes the registry, which releases the module lifetimes in reverse registration order. Disposal is
/// idempotent and thread-safe.
///
/// <para><b>Ownership rule:</b> whoever builds the host disposes it. A borrowing player / session / ShowSession
/// must <em>not</em> dispose a host (or its registry) it was merely handed - the same borrow-vs-own boundary the
/// review draws for output leases (NXT-01) and the registry's own <see cref="MediaRegistry.Dispose"/> note.</para>
///
/// <para>The plugin-lease surface is deliberately minimal - it is the ownership seam the dynamic plugin host
/// (NXT-09) will hang capability / surface / factory leases off, so a native library's unload can be gated until
/// every plugin-created object is released and a finalizer can never destroy a plugin object on the wrong thread.
/// Until that host lands nothing acquires a lease, so it is inert but ready, and the leak report already fails
/// loudly if a future consumer forgets to release one.</para>
/// </summary>
public sealed class MediaHost : IDisposable, IAsyncDisposable
{
    private static readonly ILogger Log = MediaDiagnostics.CreateLogger("S.Media.MediaHost");

    private readonly MediaRegistry _registry;
    private readonly Action<IReadOnlyList<string>>? _onLeasesLeaked;
    private readonly object _gate = new();
    private readonly Dictionary<long, string> _leases = [];
    private long _nextLeaseId;
    private int _disposed;

    private MediaHost(MediaRegistry registry, Action<IReadOnlyList<string>>? onLeasesLeaked)
    {
        _registry = registry;
        _onLeasesLeaked = onLeasesLeaked;
    }

    /// <summary>The owned capability registry. Never dispose this directly - dispose the host instead.</summary>
    public IMediaRegistry Registry => _registry;

    /// <summary>True once the host has been disposed (idempotent guard for consumers that may race teardown).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Number of plugin leases currently outstanding (0 in a clean state at disposal).</summary>
    public int OutstandingPluginLeases
    {
        get { lock (_gate) return _leases.Count; }
    }

    /// <summary>
    /// Builds a host that owns a freshly-built registry (the composition root). <paramref name="onLeasesLeaked"/>
    /// (optional) is invoked on disposal when any plugin lease is still outstanding - the escalation hook for
    /// tests / debug builds so a leaked plugin object surfaces as a failure rather than a silent log line.
    /// </summary>
    public static MediaHost Build(Action<IMediaRegistryBuilder> configure, Action<IReadOnlyList<string>>? onLeasesLeaked = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return new MediaHost(MediaRegistry.Build(configure), onLeasesLeaked);
    }

    /// <summary>
    /// Takes ownership of an already-built <paramref name="registry"/>, for consumers that build the registry
    /// separately (e.g. the C ABI's per-session registry). The host owns and disposes it - do not dispose
    /// <paramref name="registry"/> elsewhere.
    /// </summary>
    public static MediaHost Own(MediaRegistry registry, Action<IReadOnlyList<string>>? onLeasesLeaked = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new MediaHost(registry, onLeasesLeaked);
    }

    /// <summary>
    /// Acquires a reference-counted lease tagged with <paramref name="owner"/> (a plugin / capability id). A live
    /// lease represents a plugin-created object (source / surface / factory) that must be released before the host
    /// is torn down; disposal reports any that remain (NXT-05). Disposing the returned handle releases the lease
    /// (idempotent). Throws <see cref="ObjectDisposedException"/> if the host is already disposed.
    /// </summary>
    public IDisposable AcquirePluginLease(string owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(owner);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var id = ++_nextLeaseId;
            _leases[id] = owner;
            return new PluginLease(this, id);
        }
    }

    private void ReleaseLease(long id)
    {
        lock (_gate)
            _leases.Remove(id);
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeCore();

    /// <inheritdoc/>
    /// <remarks>Registry / lifetime release is synchronous today; the async surface exists because the review
    /// names the host <c>IAsyncDisposable</c> and it will own async session / dispatcher resources later.</remarks>
    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        string[] leaked;
        lock (_gate)
        {
            leaked = [.. _leases.Values];
            _leases.Clear();
        }

        if (leaked.Length > 0)
        {
            // A plugin object outlived the host - a lifetime bug. Report it, but never throw out of disposal:
            // one warning per distinct owner, then the optional escalation hook (tests / debug builds assert on it).
            foreach (var owner in leaked.Distinct())
                Log.LogWarning(
                    "MediaHost disposed with an outstanding plugin lease held by '{Owner}' - a plugin object leaked its lease.",
                    owner);
            try { _onLeasesLeaked?.Invoke(leaked); }
            catch (Exception ex)
            {
                // An escalation hook must never break teardown.
                Log.LogWarning(ex, "MediaHost plugin-lease-leak reporter threw; continuing disposal.");
            }
        }

        _registry.Dispose(); // releases module native-runtime lifetimes in reverse order (PortAudio / NDI)
    }

    private sealed class PluginLease(MediaHost host, long id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                host.ReleaseLease(id);
        }
    }
}
