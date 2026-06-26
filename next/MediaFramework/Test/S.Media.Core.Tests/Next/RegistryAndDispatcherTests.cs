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
