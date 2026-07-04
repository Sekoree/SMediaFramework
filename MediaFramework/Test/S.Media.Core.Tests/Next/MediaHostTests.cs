using Xunit;

namespace S.Media.Core.Tests.Next;

/// <summary>
/// NXT-05 (Gate 2): the single owning <see cref="MediaHost"/> — disposing it releases the registry's module
/// native-runtime lifetimes deterministically, and reports plugin leases that outlived the host. These are the
/// invariants HaPlay's shutdown and the C-ABI per-session teardown now depend on.
/// </summary>
public class MediaHostTests
{
    private sealed class DisposeSpy(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void Dispose_ReleasesRegistryLifetimes_Once_AndIsIdempotent()
    {
        var releases = 0;
        var host = MediaHost.Build(b => b.AddLifetime(new DisposeSpy(() => releases++)));
        Assert.Equal(0, releases); // nothing released until the host is disposed

        host.Dispose();
        Assert.Equal(1, releases);
        Assert.True(host.IsDisposed);

        host.Dispose(); // idempotent — no second release of the module lifetime
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesRegistryLifetimes()
    {
        var releases = 0;
        var host = MediaHost.Build(b => b.AddLifetime(new DisposeSpy(() => releases++)));

        await host.DisposeAsync();
        Assert.Equal(1, releases);
        Assert.True(host.IsDisposed);
    }

    [Fact]
    public void Registry_ExposesTheOwnedRegistry_Stably()
    {
        using var host = MediaHost.Build(_ => { });
        Assert.NotNull(host.Registry);
        Assert.Same(host.Registry, host.Registry); // same owned instance across accesses
        Assert.False(host.IsDisposed);
    }

    [Fact]
    public void Own_TakesOwnershipOfRegistry_AndDisposesIt()
    {
        var released = false;
        var registry = MediaRegistry.Build(b => b.AddLifetime(new DisposeSpy(() => released = true)));
        var host = MediaHost.Own(registry);
        Assert.Same(registry, host.Registry);

        host.Dispose();
        Assert.True(released); // the host owns and disposes the registry it was handed
    }

    [Fact]
    public void PluginLease_Released_DoesNotReportLeakOnDispose()
    {
        IReadOnlyList<string>? leaked = null;
        var host = MediaHost.Build(_ => { }, onLeasesLeaked: l => leaked = l);

        var lease = host.AcquirePluginLease("org.example.plugin");
        Assert.Equal(1, host.OutstandingPluginLeases);

        lease.Dispose();
        Assert.Equal(0, host.OutstandingPluginLeases);

        lease.Dispose(); // idempotent — releasing twice does not underflow the count
        Assert.Equal(0, host.OutstandingPluginLeases);

        host.Dispose();
        Assert.Null(leaked); // clean teardown → no leak reported
    }

    [Fact]
    public void PluginLease_Outstanding_ReportsLeakOnDispose_ButStillReleasesRegistry()
    {
        IReadOnlyList<string>? leaked = null;
        var registryReleased = false;
        var host = MediaHost.Build(
            b => b.AddLifetime(new DisposeSpy(() => registryReleased = true)),
            onLeasesLeaked: l => leaked = l);

        host.AcquirePluginLease("org.example.mmd"); // never released — simulates a plugin object outliving the host
        host.AcquirePluginLease("org.example.mmd"); // same owner reported once (distinct)
        host.AcquirePluginLease("org.example.youtube");
        Assert.Equal(3, host.OutstandingPluginLeases);

        host.Dispose();

        Assert.NotNull(leaked);
        Assert.Equal(3, leaked!.Count); // all outstanding leases surfaced to the reporter
        Assert.Contains("org.example.mmd", leaked);
        Assert.Contains("org.example.youtube", leaked);
        Assert.True(registryReleased); // a leak must NOT abort registry teardown — native runtimes still release
    }

    [Fact]
    public void LeakReporterThrowing_DoesNotBreakDisposal()
    {
        var registryReleased = false;
        var host = MediaHost.Build(
            b => b.AddLifetime(new DisposeSpy(() => registryReleased = true)),
            onLeasesLeaked: _ => throw new InvalidOperationException("reporter blew up"));

        host.AcquirePluginLease("p");
        host.Dispose(); // must not propagate the reporter's throw

        Assert.True(registryReleased);
        Assert.True(host.IsDisposed);
    }

    [Fact]
    public void AcquirePluginLease_AfterDispose_Throws()
    {
        var host = MediaHost.Build(_ => { });
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => host.AcquirePluginLease("p"));
    }
}
