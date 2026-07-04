using System.Linq;
using System.Threading.Tasks;
using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class MIDIDeviceAliasTests
{
    [Fact]
    public void MIDIDeviceDialog_BuildValues_TrimsAliasAndReturnsProfile()
    {
        // Data-driven profiles replaced the old BuiltInControlDeviceProfileFactory; the dialog only needs a
        // profile's Id/DisplayName/Protocol to list and select it, so a minimal inline profile suffices here.
        var profile = new ControlDeviceProfile
        {
            Id = "behringer.bcf2000",
            DisplayName = "Behringer BCF2000",
            Protocol = ControlDeviceProtocol.MIDI,
        };
        var vm = new MIDIDeviceDialogViewModel(
            "Edit MIDI device", "Behringer BCF2000 BCF2000 MIDI 1", "generic-midi", alias: null, isEnabled: true,
            midiProfiles: [profile]);

        vm.SelectedProfile = vm.ProfileOptions.Single(o => o.ProfileId == "behringer.bcf2000");
        vm.Alias = "  bcf  ";

        var values = vm.BuildValues();
        Assert.Equal("bcf", values.Alias);
        Assert.Equal("behringer.bcf2000", values.ProfileId);
        Assert.True(values.IsEnabled);
    }

    [Fact]
    public void MIDIDeviceDialog_BlankAlias_BecomesNullAndDefaultsToGenericProfile()
    {
        var vm = new MIDIDeviceDialogViewModel("Edit", "Some MIDI Port", profileId: null, alias: null, isEnabled: false, midiProfiles: []);
        vm.Alias = "   ";

        var values = vm.BuildValues();
        Assert.Null(values.Alias);
        Assert.Equal("generic-midi", values.ProfileId); // the "(generic MIDI — no profile)" default option
        Assert.False(values.IsEnabled);
    }

    [Fact]
    public void MIDIDeviceDialog_PreselectsExistingProfile()
    {
        var profile = new ControlDeviceProfile
        {
            Id = "behringer.xtouch-mini.mc",
            DisplayName = "Behringer X-Touch Mini",
            Protocol = ControlDeviceProtocol.MIDI,
        };
        var vm = new MIDIDeviceDialogViewModel("Edit", "X-Touch MINI", "behringer.xtouch-mini.mc", "xt", true, [profile]);

        Assert.Equal("behringer.xtouch-mini.mc", vm.SelectedProfile.ProfileId);
        Assert.Equal("xt", vm.Alias);
    }

    [Fact]
    public async Task EditMIDIDevice_UpdatesAliasAndProfileOnTheDevice()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.AddOrUpdateMIDIInputDevice(1, "Behringer BCF2000 BCF2000 MIDI 1");

        vm.MIDIDevicePrompt = dialog =>
        {
            dialog.Alias = "bcf";
            dialog.SelectedProfile = dialog.ProfileOptions.Single(o => o.ProfileId == "behringer.bcf2000");
            return Task.FromResult(true);
        };

        var row = Assert.Single(vm.StructureRows, r => r.CanEditMIDIDevice);
        row.EditMIDIDeviceCommand!.Execute(null);
        await Task.Yield();

        var device = Assert.Single(vm.BuildSnapshot().Devices);
        Assert.Equal("bcf", device.Binding.Alias);
        Assert.Equal("behringer.bcf2000", device.ProfileId);
    }

    [Fact]
    public async Task EditMIDIDevice_CancelLeavesDeviceUnchanged()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.AddOrUpdateMIDIInputDevice(1, "Long MIDI Port Name");
        var originalAlias = Assert.Single(vm.BuildSnapshot().Devices).Binding.Alias;

        vm.MIDIDevicePrompt = dialog =>
        {
            dialog.Alias = "should-not-apply";
            return Task.FromResult(false); // user cancelled
        };

        var row = Assert.Single(vm.StructureRows, r => r.CanEditMIDIDevice);
        row.EditMIDIDeviceCommand!.Execute(null);
        await Task.Yield();

        Assert.Equal(originalAlias, Assert.Single(vm.BuildSnapshot().Devices).Binding.Alias);
    }
}
