using S.Media.Core.Triggers;
using S.Media.Session;
using Xunit;

namespace S.Media.Session.Tests;

public sealed class TriggerBindingSetTests
{
    [Fact]
    public void DispatchHistoryIsBoundedForLongRunningInputs()
    {
        var bindings = new TriggerBindingSet();
        var trigger = new TriggerDescriptor(TriggerSourceKind.MIDI, "controller", "cc1");
        bindings.AddOrReplace(new TriggerBinding(
            "binding",
            trigger,
            new TriggerActionDescriptor(TriggerActionKind.Custom, "target", "set")));

        for (var i = 0; i < 2_000; i++)
            bindings.Simulate(trigger, TriggerPayload.FromNumeric(i));

        Assert.InRange(bindings.Dispatches.Count, 1, 575);
    }
}
