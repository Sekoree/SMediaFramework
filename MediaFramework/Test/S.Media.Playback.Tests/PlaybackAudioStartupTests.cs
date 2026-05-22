using S.Media.Playback;
using Xunit;

namespace S.Media.Playback.Tests;

public sealed class PlaybackAudioStartupTests
{
    [Fact]
    public void GetCallbacks_FirstCall_ReturnsPrefillAndStartHardware()
    {
        var prefillCalls = 0;
        var startCalls = 0;
        var startup = new PlaybackAudioStartup(
            () => prefillCalls++,
            () => startCalls++);

        startup.GetCallbacks(out var prefill, out var startHardware);

        Assert.NotNull(prefill);
        Assert.NotNull(startHardware);

        prefill!();
        startHardware!();

        Assert.Equal(1, prefillCalls);
        Assert.Equal(1, startCalls);
    }

    [Fact]
    public void GetCallbacks_AfterStartHardware_ReturnsNullCallbacks()
    {
        var startup = new PlaybackAudioStartup(() => { }, () => { });

        startup.GetCallbacks(out var prefill, out var startHardware);
        Assert.NotNull(prefill);
        Assert.NotNull(startHardware);

        startHardware!();
        startup.GetCallbacks(out prefill, out startHardware);

        Assert.Null(prefill);
        Assert.Null(startHardware);
    }
}
