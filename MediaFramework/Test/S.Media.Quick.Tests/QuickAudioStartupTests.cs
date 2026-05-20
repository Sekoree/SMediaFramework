using S.Media.Quick;
using Xunit;

namespace S.Media.Quick.Tests;

public sealed class QuickAudioStartupTests
{
    [Fact]
    public void GetCallbacks_FirstCall_ReturnsPrefillAndStartHardware()
    {
        var prefillCalls = 0;
        var startCalls = 0;
        var startup = new QuickAudioStartup(
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
        var startup = new QuickAudioStartup(() => { }, () => { });

        startup.GetCallbacks(out var prefill, out var startHardware);
        Assert.NotNull(prefill);
        Assert.NotNull(startHardware);

        startHardware!();
        startup.GetCallbacks(out prefill, out startHardware);

        Assert.Null(prefill);
        Assert.Null(startHardware);
    }

    [Fact]
    public void GetCallbacks_WhenPrefillThrows_DoesNotMarkStarted()
    {
        var prefillCalls = 0;
        var startup = new QuickAudioStartup(
            () =>
            {
                prefillCalls++;
                throw new InvalidOperationException("prefill failed");
            },
            () => { });

        startup.GetCallbacks(out var prefill, out var startHardware);
        Assert.Throws<InvalidOperationException>(() => prefill!());

        startup.GetCallbacks(out prefill, out startHardware);

        Assert.NotNull(prefill);
        Assert.NotNull(startHardware);
        Assert.Equal(1, prefillCalls);
    }
}
