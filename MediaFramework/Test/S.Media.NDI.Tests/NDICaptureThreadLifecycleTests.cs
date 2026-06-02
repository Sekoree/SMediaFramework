using S.Media.NDI;
using Xunit;

namespace S.Media.NDI.Tests;

public sealed class NDICaptureThreadLifecycleTests
{
    [Fact]
    public void StopAndDispose_WhenCaptureThreadBlocks_LeaksNativeResourcesAndWakesReaders()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        var nativeDisposed = false;
        var notifiedStopped = false;
        var releasedQueued = false;
        var wokeReaders = false;
        Exception? stuck = null;

        var thread = new Thread(() =>
        {
            entered.Set();
            release.Wait();
        })
        {
            IsBackground = true,
            Name = "NDICaptureThreadLifecycleTests.Blocked",
        };
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        try
        {
            var stopped = NDICaptureThreadLifecycle.StopAndDispose(
                "TestReceiver",
                thread,
                cts,
                TimeSpan.FromMilliseconds(25),
                () => notifiedStopped = true,
                () => nativeDisposed = true,
                () => releasedQueued = true,
                () => wokeReaders = true,
                ex => stuck = ex);

            Assert.False(stopped);
            Assert.False(nativeDisposed);
            Assert.False(notifiedStopped);
            Assert.True(releasedQueued);
            Assert.True(wokeReaders);
            Assert.IsType<TimeoutException>(stuck);
            Assert.True(cts.IsCancellationRequested);
            Assert.True(thread.IsAlive);
        }
        finally
        {
            release.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public void StopAndDispose_WhenCaptureThreadExits_DisposesNativeResourcesAndNotifiesStopped()
    {
        using var entered = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        var nativeDisposed = false;
        var notifiedStopped = false;
        var releasedQueued = false;
        var wokeReaders = false;
        Exception? stuck = null;

        var thread = new Thread(() =>
        {
            entered.Set();
            while (!cts.IsCancellationRequested)
                Thread.Sleep(1);
        })
        {
            IsBackground = true,
            Name = "NDICaptureThreadLifecycleTests.Normal",
        };
        thread.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

        var stopped = NDICaptureThreadLifecycle.StopAndDispose(
            "TestReceiver",
            thread,
            cts,
            TimeSpan.FromSeconds(2),
            () => notifiedStopped = true,
            () => nativeDisposed = true,
            () => releasedQueued = true,
            () => wokeReaders = true,
            ex => stuck = ex);

        Assert.True(stopped);
        Assert.True(nativeDisposed);
        Assert.True(notifiedStopped);
        Assert.True(releasedQueued);
        Assert.True(wokeReaders);
        Assert.Null(stuck);
        Assert.False(thread.IsAlive);
    }

    [Fact]
    public void StopAndDispose_WhenCaptureThreadAlreadyContainedFault_DisposesNativeResources()
    {
        using var cts = new CancellationTokenSource();
        var nativeDisposed = false;
        var notifiedStopped = false;
        Exception? containedFault = null;
        Exception? stuck = null;

        var thread = new Thread(() =>
        {
            try
            {
                throw new InvalidOperationException("capture boom");
            }
            catch (Exception ex)
            {
                containedFault = ex;
            }
        })
        {
            IsBackground = true,
            Name = "NDICaptureThreadLifecycleTests.Faulted",
        };
        thread.Start();
        Assert.True(SpinUntil(() => !thread.IsAlive, TimeSpan.FromSeconds(2)));

        var stopped = NDICaptureThreadLifecycle.StopAndDispose(
            "TestReceiver",
            thread,
            cts,
            TimeSpan.FromSeconds(2),
            () => notifiedStopped = true,
            () => nativeDisposed = true,
            static () => { },
            static () => { },
            ex => stuck = ex);

        Assert.True(stopped);
        Assert.True(nativeDisposed);
        Assert.True(notifiedStopped);
        Assert.IsType<InvalidOperationException>(containedFault);
        Assert.Null(stuck);
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
                return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
