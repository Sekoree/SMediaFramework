using System.Linq;
using System.Threading.Tasks;
using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;
using S.Control;
using Xunit;

namespace HaPlay.Tests;

public sealed class MidiDeviceAliasTests
{
    [Fact]
    public void MidiDeviceDialog_BuildValues_TrimsAliasAndReturnsProfile()
    {
        var profile = BuiltInControlDeviceProfileFactory.CreateBcf2000Profile();
        var vm = new MidiDeviceDialogViewModel(
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
    public void MidiDeviceDialog_BlankAlias_BecomesNullAndDefaultsToGenericProfile()
    {
        var vm = new MidiDeviceDialogViewModel("Edit", "Some MIDI Port", profileId: null, alias: null, isEnabled: false, midiProfiles: []);
        vm.Alias = "   ";

        var values = vm.BuildValues();
        Assert.Null(values.Alias);
        Assert.Equal("generic-midi", values.ProfileId); // the "(generic MIDI — no profile)" default option
        Assert.False(values.IsEnabled);
    }

    [Fact]
    public void MidiDeviceDialog_PreselectsExistingProfile()
    {
        var profile = BuiltInControlDeviceProfileFactory.CreateXTouchMiniProfile();
        var vm = new MidiDeviceDialogViewModel("Edit", "X-Touch MINI", "behringer.xtouch-mini.mc", "xt", true, [profile]);

        Assert.Equal("behringer.xtouch-mini.mc", vm.SelectedProfile.ProfileId);
        Assert.Equal("xt", vm.Alias);
    }

    [Fact]
    public async Task EditMidiDevice_UpdatesAliasAndProfileOnTheDevice()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.AddOrUpdateMidiInputDevice(1, "Behringer BCF2000 BCF2000 MIDI 1");

        vm.MidiDevicePrompt = dialog =>
        {
            dialog.Alias = "bcf";
            dialog.SelectedProfile = dialog.ProfileOptions.Single(o => o.ProfileId == "behringer.bcf2000");
            return Task.FromResult(true);
        };

        var row = Assert.Single(vm.StructureRows, r => r.CanEditMidiDevice);
        row.EditMidiDeviceCommand!.Execute(null);
        await Task.Yield();

        var device = Assert.Single(vm.BuildSnapshot().Devices);
        Assert.Equal("bcf", device.Binding.Alias);
        Assert.Equal("behringer.bcf2000", device.ProfileId);
    }

    [Fact]
    public async Task EditMidiDevice_CancelLeavesDeviceUnchanged()
    {
        await using var vm = new ControlWorkspaceViewModel();
        vm.AddOrUpdateMidiInputDevice(1, "Long MIDI Port Name");
        var originalAlias = Assert.Single(vm.BuildSnapshot().Devices).Binding.Alias;

        vm.MidiDevicePrompt = dialog =>
        {
            dialog.Alias = "should-not-apply";
            return Task.FromResult(false); // user cancelled
        };

        var row = Assert.Single(vm.StructureRows, r => r.CanEditMidiDevice);
        row.EditMidiDeviceCommand!.Execute(null);
        await Task.Yield();

        Assert.Equal(originalAlias, Assert.Single(vm.BuildSnapshot().Devices).Binding.Alias);
    }
}
