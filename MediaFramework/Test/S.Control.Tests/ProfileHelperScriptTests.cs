using Xunit;

namespace HaPlay.Tests;

// Proves the data-driven helper design: the X32 profile's embedded Mond HelperScript is exposed to control
// scripts as the `x32` global, and its functions read addresses from the profile's own command data
// (devices.command(id).address) - no hardcoded device C# in the runtime.
public class ProfileHelperScriptTests
{
    private static ControlScriptFileHost CreateHost(string scriptSource)
    {
        var x32 = BuiltInProfileLoader.Load().Single(p => p.Id == "behringer.x32.osc");
        var services = new ControlScriptRuntimeServices(profiles: [x32]);
        var sources = new InMemoryControlScriptSourceProvider(
            new Dictionary<string, string> { ["test.mnd"] = scriptSource });
        return new ControlScriptFileHost(sources, runtimeServices: services);
    }

    [Fact]
    public void X32HelperScript_BuildsAddressesFromProfileData()
    {
        var host = CreateHost(
            "return { ch: fun() { return x32.channelFaderAddress(1); }, " +
            "bus: fun() { return x32.busMuteAddress(10); }, " +
            "dca: fun() { return x32.dcaFaderAddress(3); }, " +
            "main: fun() { return x32.mainFaderAddress(); } };");

        Assert.Equal("/ch/01/mix/fader", (string)host.Invoke("test.mnd", "ch"));
        Assert.Equal("/bus/10/mix/on", (string)host.Invoke("test.mnd", "bus"));
        Assert.Equal("/dca/3/fader", (string)host.Invoke("test.mnd", "dca"));
        Assert.Equal("/main/st/mix/fader", (string)host.Invoke("test.mnd", "main"));
    }

    [Fact]
    public void X32HelperScript_PortsFaderCurveMath()
    {
        var host = CreateHost("return { db: fun() { return x32.faderToDb(0.75); } };");

        // 0.75 is in the top segment: 0.75 * 40 - 30 = 0 dB (unity).
        Assert.Equal(0.0, (double)host.Invoke("test.mnd", "db"), 3);
    }
}
