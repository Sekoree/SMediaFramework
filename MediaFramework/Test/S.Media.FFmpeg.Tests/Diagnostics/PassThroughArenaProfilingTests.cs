using S.Media.FFmpeg.Diagnostics;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Diagnostics;

[Collection(nameof(PassThroughArenaDiagnosticsCollection))]
public sealed class PassThroughArenaProfilingTests
{
    [Fact]
    public void WithTestOverride_true_RecordsRentAndReturn()
    {
        PassThroughArenaProfiling.ResetCounters();
        PassThroughArenaProfiling.SetTestOverride(true);
        try
        {
            using var arena = new PassThroughDescriptorArena();
            var h = arena.Rent(2);
            arena.Return(in h);
        }
        finally
        {
            PassThroughArenaProfiling.SetTestOverride(null);
        }

        Assert.True(PassThroughArenaProfiling.RentLockCalls >= 1);
        Assert.True(PassThroughArenaProfiling.ReturnLockCalls >= 1);
    }

    [Fact]
    public void Dispose_recordsClear_whenProfiling()
    {
        PassThroughArenaProfiling.ResetCounters();
        PassThroughArenaProfiling.SetTestOverride(true);
        try
        {
            var arena = new PassThroughDescriptorArena();
            arena.Dispose();
        }
        finally
        {
            PassThroughArenaProfiling.SetTestOverride(null);
        }

        Assert.True(PassThroughArenaProfiling.ClearLockCalls >= 1);
    }

    [Fact]
    public void ManyRentReturn_cycles_underProfiling_incrementsCounters()
    {
        PassThroughArenaProfiling.ResetCounters();
        PassThroughArenaProfiling.SetTestOverride(true);
        try
        {
            using var arena = new PassThroughDescriptorArena();
            const int cycles = 2000;
            for (var i = 0; i < cycles; i++)
            {
                var h = arena.Rent(2);
                arena.Return(in h);
            }

            Assert.True(PassThroughArenaProfiling.RentLockCalls >= cycles);
            Assert.True(PassThroughArenaProfiling.ReturnLockCalls >= cycles);
        }
        finally
        {
            PassThroughArenaProfiling.SetTestOverride(null);
        }
    }

    [Fact]
    public void Parallel_rent_return_underProfiling_can_record_treiber_cas_retries()
    {
        PassThroughArenaProfiling.ResetCounters();
        PassThroughArenaSerialization.SetTestOverride(false);
        PassThroughArenaProfiling.SetTestOverride(true);
        try
        {
            using var arena = new PassThroughDescriptorArena();
            const int total = 200_000;
            Parallel.For(0, total, _ => {
                var h = arena.Rent(1);
                arena.Return(in h);
            });

            Assert.True(PassThroughArenaProfiling.RentLockCalls >= total);
            Assert.True(PassThroughArenaProfiling.ReturnLockCalls >= total);
            Assert.True(PassThroughArenaProfiling.TreiberCasRetries > 0);
        }
        finally
        {
            PassThroughArenaProfiling.SetTestOverride(null);
        }
    }
}
