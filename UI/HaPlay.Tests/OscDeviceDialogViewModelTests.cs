using HaPlay.ControlGraph;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;
using Xunit;

namespace HaPlay.Tests;

public sealed class OscDeviceDialogViewModelTests
{
    private static IReadOnlyList<ControlDeviceProfile> OscProfiles() =>
        BuiltInControlDeviceProfileRepository.Instance.Profiles
            .Where(p => p.Protocol == ControlDeviceProtocol.Osc)
            .ToList();

    [Fact]
    public void SwitchingProfile_SuggestsThatProfilesDefaultPort()
    {
        var dialog = new OscDeviceDialogViewModel(
            "Add OSC device", name: "Mixer", profileId: "behringer.x32.osc",
            host: "192.168.1.10", port: 10023, alias: "x32", localPort: null, isEnabled: true,
            oscProfiles: OscProfiles());

        Assert.Equal("10023", dialog.PortText);

        dialog.SelectedProfile = dialog.ProfileOptions.Single(o => o.ProfileId == "behringer.xair.osc");
        Assert.Equal("10024", dialog.PortText); // X-Air default

        dialog.SelectedProfile = dialog.ProfileOptions.Single(o => o.ProfileId == "behringer.x32.osc");
        Assert.Equal("10023", dialog.PortText); // back to X32 default
    }

    [Fact]
    public void SwitchingProfile_KeepsCustomPort()
    {
        var dialog = new OscDeviceDialogViewModel(
            "Add OSC device", name: "Mixer", profileId: "behringer.x32.osc",
            host: "192.168.1.10", port: 9000, alias: "x32", localPort: null, isEnabled: true,
            oscProfiles: OscProfiles());

        Assert.Equal("9000", dialog.PortText);

        dialog.SelectedProfile = dialog.ProfileOptions.Single(o => o.ProfileId == "behringer.xair.osc");
        Assert.Equal("9000", dialog.PortText); // user's custom port is preserved
    }
}
