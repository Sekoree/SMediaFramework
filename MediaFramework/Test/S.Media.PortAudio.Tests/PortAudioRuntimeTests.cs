using S.Media.PortAudio;
using Xunit;

namespace S.Media.PortAudio.Tests;

public class PortAudioRuntimeTests
{
    [Fact]
    public void Acquire_Release_DoesNotThrow()
    {
        PortAudioRuntime.Acquire();
        PortAudioRuntime.Release();
    }

    [Fact]
    public void Acquire_IsRefCounted()
    {
        PortAudioRuntime.Acquire();
        PortAudioRuntime.Acquire();
        PortAudioRuntime.Release();
        // Library still up here — second Release tears it down.
        var version = PortAudioRuntime.VersionText;
        Assert.False(string.IsNullOrEmpty(version));
        PortAudioRuntime.Release();
    }

    [Fact]
    public void VersionText_ReturnsSomething()
    {
        var version = PortAudioRuntime.VersionText;
        Assert.False(string.IsNullOrEmpty(version));
        Assert.Contains("PortAudio", version, StringComparison.OrdinalIgnoreCase);
    }
}
