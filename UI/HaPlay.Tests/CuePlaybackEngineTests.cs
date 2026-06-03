using HaPlay.Playback;
using Xunit;

namespace HaPlay.Tests;

public sealed class CuePlaybackEngineTests
{
    [Fact]
    public void NaturalEnd_UsesAwaitableCallbackContract()
    {
        var evt = typeof(CuePlaybackEngine).GetEvent(nameof(CuePlaybackEngine.NaturalEnd));

        Assert.NotNull(evt);
        Assert.Equal(typeof(Func<Task>), evt.EventHandlerType);
    }
}
