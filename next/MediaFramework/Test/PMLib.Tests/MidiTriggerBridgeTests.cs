using PMLib;
using Xunit;

namespace PMLib.Tests;

public sealed class MidiTriggerBridgeTests
{
    [Fact]
    public void MidiTriggerProfile_resolves_note_cc_and_program()
    {
        var profile = new MidiTriggerProfile()
            .MapNoteOn(0, 60, "pad.kick.fire")
            .MapControlChange(0, 7, "out.main.gain")
            .MapProgramChange(0, 3, "patch.lead");

        Assert.True(profile.TryResolveNoteOn(0, 60, out var noteId));
        Assert.Equal("pad.kick.fire", noteId);
        Assert.True(profile.TryResolveControlChange(0, 7, out var ccId));
        Assert.Equal("out.main.gain", ccId);
        Assert.True(profile.TryResolveProgramChange(0, 3, out var pcId));
        Assert.Equal("patch.lead", pcId);
    }
}
