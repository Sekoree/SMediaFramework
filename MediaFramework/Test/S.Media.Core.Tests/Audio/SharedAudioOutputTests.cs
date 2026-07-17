using Xunit;

namespace S.Media.Core.Tests.Audio;

public sealed class SharedAudioOutputTests
{
    private static readonly AudioFormat Stereo48k = new(48_000, 2);

    [Fact]
    public void Acquire_ReturnsIndependentLeases_AndReleaseLeavesOtherClientAttached()
    {
        using var terminal = new GatedOutput(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);

        var first = shared.Acquire();
        using var second = shared.Acquire();

        Assert.NotSame(first.Output, second.Output);
        Assert.Equal(2, shared.ActiveLeaseCount);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, shared.ActiveLeaseCount);
    }

    [Fact]
    public async Task LeaseChurn_WithWaitersAndFlushes_DisposesCleanly()
    {
        // Review P3-1: every client lease owns a wait event that can inflate to a kernel handle.
        // Churn leases hard - including waits that BLOCK (full queue) and are released by disposal -
        // so a leaked/undrained event would surface as hangs or ObjectDisposedExceptions here.
        using var terminal = new GatedOutput(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);

        var chunk = new float[64 * 2];
        for (var round = 0; round < 50; round++)
        {
            var lease = shared.Acquire();
            var clocked = Assert.IsAssignableFrom<IClockedOutput>(lease.Output);

            // Saturate the client queue (Submit drops on overflow, never blocks) so the waiter
            // below genuinely parks on the event instead of returning immediately.
            for (var i = 0; i < 400; i++)
                lease.Output.Submit(chunk);

            var blocked = Task.Run(() => clocked.WaitForCapacity(64, CancellationToken.None));
            lease.Dispose();
            var hadCapacity = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(hadCapacity, "a waiter on a disposed lease must report no capacity");
        }

        Assert.Equal(0, shared.ActiveLeaseCount);
    }

    [Fact]
    public void TwoClientInputs_AreMixedIntoOneTerminalSubmission()
    {
        using var terminal = new GatedOutput(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 2);
        using var first = shared.Acquire();
        using var second = shared.Acquire();

        first.Output.Submit(Enumerable.Repeat(1f, 64 * 2).ToArray());
        second.Output.Submit(Enumerable.Repeat(2f, 64 * 2).ToArray());
        terminal.AllowPlayback();

        Assert.True(terminal.WaitForFirstSubmission(TimeSpan.FromSeconds(2)));
        Assert.All(terminal.FirstSubmission, sample => Assert.Equal(3f, sample));
    }

    [Fact]
    public void ClientInput_BuffersFullHardwareRefillBurst_BeforeApplyingBackpressure()
    {
        using var terminal = new GatedOutput(Stereo48k);
        using var shared = new SharedAudioOutput(terminal, chunkSamples: 64, pumpCapacityChunks: 4);
        using var lease = shared.Acquire();
        var clocked = Assert.IsAssignableFrom<IClockedOutput>(lease.Output);
        var chunk = new float[64 * Stereo48k.Channels];

        // The terminal is gated, so the shared mixer cannot consume yet. All eight chunks must
        // fit: JACK/PipeWire may release several callback periods of capacity at once, and a
        // three-chunk reservoir produced audible zero-filled gaps during those refill bursts.
        for (var i = 0; i < 8; i++)
        {
            Assert.True(clocked.WaitForCapacity(64, CancellationToken.None));
            lease.Output.Submit(chunk);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        Assert.False(clocked.WaitForCapacity(64, timeout.Token));

        terminal.AllowPlayback();
        using var resumed = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.True(clocked.WaitForCapacity(64, resumed.Token));
    }

    private sealed class GatedOutput(AudioFormat format) : IAudioOutput, IClockedOutput, IDisposable
    {
        private readonly ManualResetEventSlim _playbackAllowed = new(false);
        private readonly ManualResetEventSlim _submitted = new(false);
        private readonly Lock _gate = new();
        private float[]? _firstSubmission;

        public AudioFormat Format { get; } = format;

        public float[] FirstSubmission
        {
            get
            {
                lock (_gate)
                    return _firstSubmission?.ToArray() ?? [];
            }
        }

        public void AllowPlayback() => _playbackAllowed.Set();
        public bool WaitForFirstSubmission(TimeSpan timeout) => _submitted.Wait(timeout);

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            _ = chunkSamples;
            try
            {
                _playbackAllowed.Wait(token);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return !token.WaitHandle.WaitOne(1);
        }

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            lock (_gate)
                _firstSubmission ??= packedSamples.ToArray();
            _submitted.Set();
        }

        public void Dispose()
        {
            _playbackAllowed.Set();
            _playbackAllowed.Dispose();
            _submitted.Dispose();
        }
    }
}
