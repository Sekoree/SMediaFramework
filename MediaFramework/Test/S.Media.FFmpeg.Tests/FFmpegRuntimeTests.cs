using S.Media.FFmpeg;
using Xunit;

namespace S.Media.FFmpeg.Tests;

public class FFmpegRuntimeTests
{
    [Fact]
    public void EnsureInitialized_TwiceWithDifferentRootPath_DoesNotThrow()
    {
        FFmpegRuntime.EnsureInitialized();
        FFmpegRuntime.EnsureInitialized("/__path_that_should_be_ignored_after_first_init__");
    }
}
