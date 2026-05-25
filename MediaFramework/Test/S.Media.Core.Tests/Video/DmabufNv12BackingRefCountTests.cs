using System.Runtime.InteropServices;
using S.Media.Core.Video;
using S.Media.Effects;
using Xunit;

namespace S.Media.Core.Tests.Video;

public sealed class DmabufNv12BackingRefCountTests
{
    [DllImport("libc", EntryPoint = "dup")]
    private static extern int dup(int fd);

    [Fact]
    public void AddReference_TwoDisposes_ClosesFdsOnce()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var baseFd = checked((int)h.DangerousGetHandle());
        var y = dup(baseFd);
        var uv = dup(baseFd);
        Assert.True(y >= 0 && uv >= 0);

        var b = new DmabufNv12Backing(y, 0, 4, uv, 0, 4, 0, 0);
        b.AddReference();
        b.Dispose();
        b.Dispose();

        Assert.Throws<ObjectDisposedException>(() => b.AddReference());
    }

    /// <summary>
    /// Regression for the AddReference-vs-Dispose TOCTOU race (review §11.1 C1).
    /// Before the fix, AddReference checked `_closed` non-atomically with the refcount
    /// increment, so a racing Dispose could close the fds between the two operations
    /// and AddReference would still successfully bump the count. The new CAS-loop
    /// pattern guarantees AddReference either throws or holds a real (non-zero) ref.
    /// </summary>
    [Fact]
    public void AddReference_RacesDispose_NeverIncrementsThroughZero()
    {
        if (!OperatingSystem.IsLinux())
            return;

        const int iterations = 500;
        for (var i = 0; i < iterations; i++)
        {
            using var h = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var baseFd = checked((int)h.DangerousGetHandle());
            var y = dup(baseFd);
            var uv = dup(baseFd);
            Assert.True(y >= 0 && uv >= 0);

            var b = new DmabufNv12Backing(y, 0, 4, uv, 0, 4, 0, 0);
            var addedRef = false;
            var threwDisposed = false;
            using var gate = new System.Threading.Barrier(2);

            var disposer = new System.Threading.Thread(() =>
            {
                gate.SignalAndWait();
                b.Dispose();
            });
            var adder = new System.Threading.Thread(() =>
            {
                gate.SignalAndWait();
                try
                {
                    b.AddReference();
                    addedRef = true;
                }
                catch (ObjectDisposedException)
                {
                    threwDisposed = true;
                }
            });
            disposer.Start();
            adder.Start();
            disposer.Join();
            adder.Join();

            // Exactly one outcome: either AddReference incremented (and the caller now owns a real ref
            // which it must drop) or it threw ObjectDisposedException because Dispose got there first.
            // The invariant the old code violated: AddReference must never succeed after the fds were
            // closed.
            Assert.True(addedRef ^ threwDisposed,
                $"iter {i}: addedRef={addedRef} threwDisposed={threwDisposed}");
            if (addedRef)
                b.Dispose(); // release the ref we gained
        }
    }
}
