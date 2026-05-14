using System.Runtime;
using S.Media.Core.Audio;
using S.Media.Core.Video;
using S.Media.NDI.Video;
using Xunit;

namespace S.Media.NDI.Tests;

/// <summary>
/// Tier F row 25 stepping stone: bounded create/dispose churn for <see cref="NDIOutput"/> (native sender + runtime)
/// under optional lab env <c>RUN_NDI_MEMORY_PRESSURE=1</c>. Optional <c>RUN_NDI_MEMORY_PRESSURE_HEAP=1</c> (requires
/// <c>RUN_NDI_MEMORY_PRESSURE=1</c>) runs a loose post-GC managed heap cap after a capped round count (catches gross
/// regressions only). Optional <c>RUN_NDI_MEMORY_PRESSURE_LONG=1</c> raises the lab round clamp to <c>2_000_000</c>
/// (overnight-style churn; still bounded). Optional <c>RUN_NDI_MEMORY_PRESSURE_HEAP_STRICT=1</c> (requires heap lab)
/// adds a stricter managed-delta cap for dedicated CI / lab hosts. Native SDK sampling (Instruments, ETW, etc.) remains manual.
/// </summary>
public sealed class NdiOutputLifecycleMemoryTests
{
    private const int DefaultRoundsCi = 80;
    private const int LabDefaultRounds = 5_000;
    private const int LabMinRounds = 200;
    private const int LabMaxRounds = 100_000;
    private const int LabMaxRoundsLong = 2_000_000;

    private static bool LabEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE"), "1", StringComparison.Ordinal);

    private static bool HeapAssertEnabled =>
        LabEnabled
        && string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_HEAP"), "1",
            StringComparison.Ordinal);

    private static bool LongLabEnabled =>
        LabEnabled
        && string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_LONG"), "1",
            StringComparison.Ordinal);

    private static bool HeapStrictEnabled =>
        HeapAssertEnabled
        && string.Equals(Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_HEAP_STRICT"), "1",
            StringComparison.Ordinal);

    private static int LabRoundsUpperBound => LongLabEnabled ? LabMaxRoundsLong : LabMaxRounds;

    /// <summary>Round count for <see cref="Repeated_create_dispose_does_not_throw"/>.</summary>
    public static int ResolveOutputLifecycleRounds()
    {
        if (!LabEnabled)
            return DefaultRoundsCi;
        var raw = Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS");
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, out var n))
            return LabDefaultRounds;
        return Math.Clamp(n, LabMinRounds, LabRoundsUpperBound);
    }

    private static bool TryProbeNdi(out string? failReason)
    {
        failReason = null;
        try
        {
            var name = $"mf-ndi-probe-{Guid.NewGuid():N}";
            using var o = new NDIOutput(name, clockVideo: false, clockAudio: false,
                videoTimecodeMode: NDIVideoTimecodeMode.Synthesize);
            _ = o.ConnectionCount;
            return true;
        }
        catch (Exception ex)
        {
            failReason = ex.Message;
            return false;
        }
    }

    [Fact]
    public void ResolveOutputLifecycleRounds_clamps_RUN_NDI_MEMORY_PRESSURE_ROUNDS()
    {
        var oldLab = Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE");
        var oldLong = Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_LONG");
        var oldRounds = Environment.GetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS");
        try
        {
            Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE", "1");
            Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_LONG", null);

            Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS", "50");
            Assert.Equal(LabMinRounds, ResolveOutputLifecycleRounds());

            Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS", "8000");
            Assert.Equal(8_000, ResolveOutputLifecycleRounds());

            Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS", "999999");
            Assert.Equal(LabMaxRounds, ResolveOutputLifecycleRounds());

            Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_LONG", "1");
            Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS", "9000000");
            Assert.Equal(LabMaxRoundsLong, ResolveOutputLifecycleRounds());
        }
        finally
        {
            if (oldLab is null) Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE", null);
            else Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE", oldLab);
            if (oldLong is null) Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_LONG", null);
            else Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_LONG", oldLong);
            if (oldRounds is null) Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS", null);
            else Environment.SetEnvironmentVariable("RUN_NDI_MEMORY_PRESSURE_ROUNDS", oldRounds);
        }
    }

    [Fact]
    public void Repeated_create_dispose_does_not_throw()
    {
        if (!TryProbeNdi(out _))
            return;

        var rounds = ResolveOutputLifecycleRounds();
        for (var i = 0; i < rounds; i++)
        {
            var name = $"mf-ndi-mem-{i}-{Guid.NewGuid():N}";
            using var o = new NDIOutput(name, clockVideo: false, clockAudio: false,
                videoTimecodeMode: NDIVideoTimecodeMode.PresentationRelativeTicks);
            _ = o.ConnectionCount;
            o.ResetVideoPresentationTimecodeAnchor();
            _ = o.EnableAudio(new AudioFormat(48_000, 2));
            var fmt = new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(30, 1));
            o.VideoSink.Configure(fmt);
            _ = o.VideoSink.Format;

            if (LabEnabled && (i + 1) % 500 == 0)
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        }
    }

    /// <summary>
    /// Optional lab: set <c>RUN_NDI_MEMORY_PRESSURE=1</c> and <c>RUN_NDI_MEMORY_PRESSURE_HEAP=1</c>. Uses a capped
    /// round count (min of resolver vs <c>4_000</c>) so the job stays bounded. Threshold is intentionally loose (managed
    /// heap only; ignores native NDI growth) to avoid flakes in parallel test hosts.
    /// </summary>
    [Fact]
    public void When_RUN_NDI_MEMORY_PRESSURE_HEAP_1_managed_heap_within_loose_cap()
    {
        if (!HeapAssertEnabled)
            return;
        if (!TryProbeNdi(out _))
            return;

        static void compactingCollect()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        compactingCollect();
        var baseline = GC.GetTotalMemory(true);

        var rounds = Math.Min(ResolveOutputLifecycleRounds(), 4_000);
        for (var i = 0; i < rounds; i++)
        {
            var name = $"mf-ndi-heap-{i}-{Guid.NewGuid():N}";
            using var o = new NDIOutput(name, clockVideo: false, clockAudio: false,
                videoTimecodeMode: NDIVideoTimecodeMode.PresentationRelativeTicks);
            _ = o.ConnectionCount;
            o.ResetVideoPresentationTimecodeAnchor();
            _ = o.EnableAudio(new AudioFormat(48_000, 2));
            var fmt = new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(30, 1));
            o.VideoSink.Configure(fmt);
            _ = o.VideoSink.Format;
        }

        compactingCollect();
        var after = GC.GetTotalMemory(true);
        const long capBytes = 512L * 1024 * 1024;
        Assert.True(after <= baseline + capBytes,
            $"Managed heap after {rounds} NDIOutput cycles: baseline={baseline} after={after} (cap +{capBytes} bytes).");
    }

    /// <summary>
    /// Optional lab: <c>RUN_NDI_MEMORY_PRESSURE=1</c>, <c>RUN_NDI_MEMORY_PRESSURE_HEAP=1</c>, and
    /// <c>RUN_NDI_MEMORY_PRESSURE_HEAP_STRICT=1</c>. Uses a bounded round count and a **stricter** managed-delta cap than
    /// <see cref="When_RUN_NDI_MEMORY_PRESSURE_HEAP_1_managed_heap_within_loose_cap"/> for dedicated hosts that run NDI
    /// tests serially or with ample headroom (not default parallel CI).
    /// </summary>
    [Fact]
    public void When_RUN_NDI_MEMORY_PRESSURE_HEAP_STRICT_1_managed_heap_within_stricter_cap()
    {
        if (!HeapStrictEnabled)
            return;
        if (!TryProbeNdi(out _))
            return;

        static void compactingCollect()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        compactingCollect();
        var baseline = GC.GetTotalMemory(true);

        var rounds = Math.Min(ResolveOutputLifecycleRounds(), 4_000);
        for (var i = 0; i < rounds; i++)
        {
            var name = $"mf-ndi-heap-strict-{i}-{Guid.NewGuid():N}";
            using var o = new NDIOutput(name, clockVideo: false, clockAudio: false,
                videoTimecodeMode: NDIVideoTimecodeMode.PresentationRelativeTicks);
            _ = o.ConnectionCount;
            o.ResetVideoPresentationTimecodeAnchor();
            _ = o.EnableAudio(new AudioFormat(48_000, 2));
            var fmt = new VideoFormat(64, 64, PixelFormat.Bgra32, new Rational(30, 1));
            o.VideoSink.Configure(fmt);
            _ = o.VideoSink.Format;
        }

        compactingCollect();
        var after = GC.GetTotalMemory(true);
        const long capBytes = 128L * 1024 * 1024;
        Assert.True(after <= baseline + capBytes,
            $"Managed heap after {rounds} NDIOutput cycles (STRICT): baseline={baseline} after={after} (cap +{capBytes} bytes).");
    }
}
