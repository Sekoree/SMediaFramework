using Xunit;

namespace S.Media.Core.Tests.Next;

public class MediaRegistryTests
{
    [Fact]
    public void Empty_registry_offers_no_capabilities()
    {
        var r = MediaRegistry.Build(_ => { });
        Assert.Empty(r.AudioBackends);
        Assert.Empty(r.Decoders);
        Assert.False(r.CanOpen("file:///x.mp4", MediaKind.Video));
        Assert.False(r.TryOpenVideo("file:///x.mp4", null, out _));
        Assert.Null(r.CreateCpuConverter());
        Assert.Null(r.CreateResampler(new FakeAudioSource(), 48000));
    }

    private sealed class DisposeSpy(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void Dispose_ReleasesRegisteredLifetimes_InReverseOrder_AndIsIdempotent()
    {
        // NXT-05: modules register native-runtime leases as lifetimes; disposing the registry releases them all,
        // last-registered first, so a per-session (e.g. C-ABI) registry frees its native holds deterministically.
        var order = new List<string>();
        var registry = MediaRegistry.Build(b =>
        {
            b.AddLifetime(new DisposeSpy(() => order.Add("a")));
            b.AddLifetime(new DisposeSpy(() => order.Add("b")));
        });
        Assert.Empty(order); // nothing released until the registry is disposed

        registry.Dispose();
        Assert.Equal(new[] { "b", "a" }, order); // reverse registration order

        registry.Dispose(); // idempotent — no second release
        Assert.Equal(new[] { "b", "a" }, order);
    }

    [Fact]
    public async Task OpenAsync_AtomicResult_OwnsAsset_DisposedExactlyOnce()
    {
        // NXT-02: an atomic open returns one MediaOpenResult owning the (shared) asset; disposing the result
        // tears it down once — both correlated tracks come from the single asset, not two independent opens.
        var provider = new SharedAssetProvider();
        IMediaRegistry registry = MediaRegistry.Build(b => b.AddDecoder(provider));

        await using (var result = await registry.OpenAsync(MediaOpenRequest.AudioAndVideo("file:///x.mp4")))
        {
            Assert.NotNull(result.Video);
            Assert.NotNull(result.Audio);
            Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
            Assert.True(result.CanSeek);
            Assert.False(result.IsLive);
            Assert.Equal(0, provider.AssetDisposeCount); // alive while in use
        }
        Assert.Equal(1, provider.AssetDisposeCount); // exactly one teardown on result dispose
    }

    [Fact]
    public async Task OpenAsync_DefaultBridge_PicksHighestConfidence_AndHonoursRequestedKinds()
    {
        // The default OpenAsync bridges to OpenVideo/OpenAudio; the registry selects the highest-confidence provider.
        IMediaRegistry registry = MediaRegistry.Build(b => b
            .AddDecoder(new FakeDecoderProvider("A", 0.5))
            .AddDecoder(new FakeDecoderProvider("B", 0.9)));

        await using var both = await registry.OpenAsync(MediaOpenRequest.AudioAndVideo("file:///x.mp4"));
        Assert.Equal("B", ((FakeVideoSource)both.Video!).Tag);
        Assert.Equal("B", ((FakeAudioSource)both.Audio!).Tag);

        // A video-only request opens no audio track.
        await using var videoOnly = await registry.OpenAsync(
            new MediaOpenRequest("file:///x.mp4") { Video = new VideoSourceOpenOptions() });
        Assert.NotNull(videoOnly.Video);
        Assert.Null(videoOnly.Audio);
    }

    [Fact]
    public async Task OpenAsync_NoProviderForUri_Throws()
    {
        IMediaRegistry registry = MediaRegistry.Build(_ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await registry.OpenAsync(MediaOpenRequest.AudioAndVideo("file:///x.mp4")));
    }

    [Fact]
    public async Task OpenAsync_HonoursCancellation()
    {
        // NXT-02: the open is cancellable — an already-cancelled token aborts before doing work.
        IMediaRegistry registry = MediaRegistry.Build(b => b.AddDecoder(new FakeDecoderProvider("A", 0.9)));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await registry.OpenAsync(MediaOpenRequest.AudioAndVideo("file:///x.mp4"), cancellationToken: cts.Token));
    }

    [Fact]
    public void Highest_confidence_decoder_wins()
    {
        var r = MediaRegistry.Build(b => b
            .AddDecoder(new FakeDecoderProvider("A", 0.5))
            .AddDecoder(new FakeDecoderProvider("B", 0.9)));

        Assert.True(r.CanOpen("file:///x.mp4", MediaKind.Video));
        Assert.True(r.TryOpenVideo("file:///x.mp4", null, out var source));
        Assert.Equal("B", ((FakeVideoSource)source).Tag);
    }

    [Fact]
    public void Confidence_ties_break_to_registration_order()
    {
        var r = MediaRegistry.Build(b => b
            .AddDecoder(new FakeDecoderProvider("A", 0.5))
            .AddDecoder(new FakeDecoderProvider("B", 0.5)));

        Assert.True(r.TryOpenAudio("file:///x.mp4", null, out var source));
        Assert.Equal("A", ((FakeAudioSource)source).Tag);   // earliest-registered wins the tie (D3)
    }

    [Fact]
    public void Zero_confidence_providers_are_not_selected()
    {
        var r = MediaRegistry.Build(b => b.AddDecoder(new FakeDecoderProvider("A", 0.0)));
        Assert.False(r.CanOpen("ndi://cam", MediaKind.Video));
        Assert.False(r.TryOpenVideo("ndi://cam", null, out _));
    }

    [Fact]
    public void Image_sources_resolve_by_extension()
    {
        var r = MediaRegistry.Build(b => b.AddImageSource(".png", _ => new FakeVideoSource { Tag = "png" }));
        Assert.True(r.TryOpenImage("/pics/logo.PNG", out var source));   // case-insensitive
        Assert.Equal("png", ((FakeVideoSource)source).Tag);
        Assert.False(r.TryOpenImage("/pics/logo.jpg", out _));
    }

    [Fact]
    public void Cpu_converter_factory_is_used_when_set()
    {
        var r = MediaRegistry.Build(b => b.SetCpuConverterFactory(() => throw new InvalidOperationException("called")));
        Assert.Throws<InvalidOperationException>(() => r.CreateCpuConverter());
    }

    [Fact]
    public void Explicit_provider_pin_bypasses_confidence()
    {
        var r = MediaRegistry.Build(b => b
            .AddDecoder(new FakeDecoderProvider("A", 0.9))    // would win on confidence
            .AddDecoder(new FakeDecoderProvider("B", 0.1)));  // pinned anyway (D3)

        Assert.True(r.TryOpenVideo("file:///x.mp4", null, "B", out var source));
        Assert.Equal("B", ((FakeVideoSource)source).Tag);
        Assert.NotNull(r.FindDecoder("a"));                   // case-insensitive lookup
        Assert.False(r.TryOpenVideo("file:///x.mp4", null, "missing", out _));
    }
}

public class SessionDispatcherTests
{
    [Fact]
    public async Task InvokeAsync_runs_work_and_returns_result()
    {
        using var d = new SessionDispatcher();
        var result = await d.InvokeAsync(() => 21 * 2);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Work_runs_on_the_dispatcher_thread_in_order()
    {
        using var d = new SessionDispatcher();
        var log = new List<int>();
        d.Post(() => log.Add(1));
        d.Post(() => log.Add(2));
        await d.InvokeAsync(() => log.Add(3));   // FIFO: completes only after the two Posts ran
        Assert.Equal([1, 2, 3], log);
    }

    [Fact]
    public async Task IsOnDispatcherThread_is_true_only_inside_the_loop()
    {
        using var d = new SessionDispatcher();
        Assert.False(d.IsOnDispatcherThread);
        Assert.True(await d.InvokeAsync(() => d.IsOnDispatcherThread));
    }

    [Fact]
    public async Task Async_reentrancy_survives_await_without_deadlock()
    {
        using var d = new SessionDispatcher();

        var result = await d.InvokeAsync(async () =>
        {
            await Task.Delay(1);
            Assert.True(d.IsOnDispatcherThread);
            return await d.InvokeAsync(() => 7);
        });

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task Exceptions_surface_through_the_returned_task()
    {
        using var d = new SessionDispatcher();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => d.InvokeAsync(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task InvokeAsync_after_dispose_faults_instead_of_hanging()
    {
        var d = new SessionDispatcher();
        d.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => d.InvokeAsync(() => 1));
    }

    [Fact]
    public void Post_after_dispose_returns_false()
    {
        var d = new SessionDispatcher();
        d.Dispose();
        Assert.False(d.Post(() => { }));
    }
}
