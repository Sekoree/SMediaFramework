using S.Media.FFmpeg.Diagnostics;
using Xunit;

namespace S.Media.FFmpeg.Tests.Diagnostics;

public sealed class PassThroughArenaProfilingTests : IDisposable
{
    public PassThroughArenaProfilingTests() => PassThroughArenaProfiling.SetTestOverride(null);

    public void Dispose()
    {
        PassThroughArenaProfiling.SetTestOverride(null);
        PassThroughArenaProfiling.ResetCounters();
    }

    [Fact]
    public void ResetCounters_clears_rent_return_and_max()
    {
        PassThroughArenaProfiling.SetTestOverride(true);
        PassThroughArenaProfiling.ResetCounters();
        PassThroughArenaProfiling.RecordRent(50);
        PassThroughArenaProfiling.RecordReturn(10);
        PassThroughArenaProfiling.RecordClear(2);
        Assert.Equal(1, PassThroughArenaProfiling.RentLockCalls);
        Assert.Equal(50, PassThroughArenaProfiling.RentLockTicksTotal);
        Assert.Equal(50, PassThroughArenaProfiling.RentLockMaxTicks);
        PassThroughArenaProfiling.ResetCounters();
        Assert.Equal(0, PassThroughArenaProfiling.RentLockCalls);
        Assert.Equal(0, PassThroughArenaProfiling.ReturnLockCalls);
        Assert.Equal(0, PassThroughArenaProfiling.ClearLockCalls);
    }
}
