using S.Media.FFmpeg.Diagnostics;
using S.Media.FFmpeg.Video;
using Xunit;

namespace S.Media.FFmpeg.Tests.Diagnostics;

[Collection(nameof(PassThroughArenaDiagnosticsCollection))]
public sealed class PassThroughArenaSerializationTests
{
    [Fact]
    public void Parallel_rent_return_underSerialization_has_no_treiber_retries_when_profiling()
    {
        PassThroughArenaProfiling.ResetCounters();
        PassThroughArenaSerialization.SetTestOverride(true);
        PassThroughArenaProfiling.SetTestOverride(true);
        try
        {
            using var arena = new PassThroughDescriptorArena();
            const int total = 80_000;
            Parallel.For(0, total, _ => {
                var h = arena.Rent(1);
                arena.Return(in h);
            });

            Assert.Equal(0, PassThroughArenaProfiling.TreiberCasRetries);
        }
        finally
        {
            PassThroughArenaProfiling.SetTestOverride(null);
            PassThroughArenaSerialization.SetTestOverride(null);
        }
    }
}
