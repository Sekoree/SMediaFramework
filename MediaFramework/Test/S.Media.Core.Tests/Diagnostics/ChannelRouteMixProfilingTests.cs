using Xunit;

namespace S.Media.Core.Tests.Diagnostics;

public sealed class ChannelRouteMixProfilingTests : IDisposable
{
    public ChannelRouteMixProfilingTests()
    {
        ChannelRouteMixProfiling.SetTestOverride(false);
        ChannelRouteMixProfiling.ResetCounters();
    }

    public void Dispose()
    {
        ChannelRouteMixProfiling.SetTestOverride(false);
        ChannelRouteMixProfiling.ResetCounters();
    }

    [Fact]
    public void ResetCounters_clears_all_fields()
    {
        using (ChannelRouteMixProfiling.EnterTestRecordingScope())
        {
            ChannelRouteMixProfiling.SetTestOverride(true);
            try
            {
                var dst = new float[8];
                var src = new float[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                var map = new ChannelMap([0, 1]);
                AudioRouter.ApplyRoute(src, 2, dst.AsSpan(), 2, map, 0.05f, 0.95f, 2);
                Assert.NotEqual(0, ChannelRouteMixProfiling.ScalarRampLoopCalls);
                ChannelRouteMixProfiling.ResetCounters();
                Assert.Equal(0, ChannelRouteMixProfiling.ApplyAdditiveCalls);
                Assert.Equal(0, ChannelRouteMixProfiling.ScalarRampLoopCalls);
                Assert.Equal(0, ChannelRouteMixProfiling.ScalarUniformGainLoopCalls);
            }
            finally
            {
                ChannelRouteMixProfiling.SetTestOverride(false);
            }
        }
    }

    [Fact]
    public void ApplyRoute_ramp_path_increments_profiling_when_enabled()
    {
        using (ChannelRouteMixProfiling.EnterTestRecordingScope())
        {
            ChannelRouteMixProfiling.SetTestOverride(true);
            try
            {
                ChannelRouteMixProfiling.ResetCounters();
                var dst = new float[8];
                var src = new float[] { 1, 2, 3, 4, 5, 6, 7, 8 };
                var map = new ChannelMap([0, 1]);
                AudioRouter.ApplyRoute(src, 2, dst.AsSpan(), 2, map, 0.1f, 0.4f, 2);
                Assert.Equal(1, ChannelRouteMixProfiling.ScalarRampLoopCalls);
                Assert.True(ChannelRouteMixProfiling.ScalarRampLoopTicksTotal >= 0);
            }
            finally
            {
                ChannelRouteMixProfiling.SetTestOverride(false);
            }
        }
    }

    [Fact]
    public void ApplyRoute_tiny_chunk_unity_gain_may_use_ApplyAdditive_path()
    {
        using (ChannelRouteMixProfiling.EnterTestRecordingScope())
        {
            ChannelRouteMixProfiling.SetTestOverride(true);
            try
            {
                ChannelRouteMixProfiling.ResetCounters();
                var dst = new float[2];
                var src = new float[] { 0.5f, 0.25f };
                AudioRouter.ApplyRoute(src, 2, dst.AsSpan(), 2, new ChannelMap([0, 1]), 1f, 1f, samplesPerChannel: 1);
                Assert.True(ChannelRouteMixProfiling.ApplyAdditiveCalls >= 1);
            }
            finally
            {
                ChannelRouteMixProfiling.SetTestOverride(false);
            }
        }
    }

    [Fact]
    public void ApplyRoute_uniform_non_unity_gain_uses_scalar_loop_when_identity_SIMD_declines()
    {
        using (ChannelRouteMixProfiling.EnterTestRecordingScope())
        {
            ChannelRouteMixProfiling.SetTestOverride(true);
            try
            {
                ChannelRouteMixProfiling.ResetCounters();
                var dst = new float[4];
                var src = new float[] { 1f, 2f };
                var map = new ChannelMap([0, 1]);
                AudioRouter.ApplyRoute(src, 2, dst.AsSpan(), 2, map, 2f, 2f, samplesPerChannel: 1);
                Assert.Equal(1, ChannelRouteMixProfiling.ScalarUniformGainLoopCalls);
            }
            finally
            {
                ChannelRouteMixProfiling.SetTestOverride(false);
            }
        }
    }
}
