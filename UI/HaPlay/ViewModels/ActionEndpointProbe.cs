using HaPlay.Models;
using HaPlay.Resources;
using OSCLib;
using PMLib;
using PMLib.Devices;
using PMLib.Types;

namespace HaPlay.ViewModels;

/// <summary>Lightweight reachability checks for action-cue OSC/MIDI endpoints.</summary>
internal static class ActionEndpointProbe
{
    public static async Task<(bool Ok, string Detail)> TryProbeOscAsync(OscActionEndpoint osc, CancellationToken ct = default)
    {
        try
        {
            using var client = await OSCClient.CreateAsync(osc.Host, osc.Port, cancellationToken: ct).ConfigureAwait(false);
            await client.SendMessageAsync("/haplay/ping", [OSCArgument.Int32(1)], ct).ConfigureAwait(false);
            return (true, Strings.Format(nameof(Strings.EndpointProbeOkHostPortFormat), osc.Host, osc.Port));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Detail) TryProbeMidi(MidiActionEndpoint midi, Func<string?> ensureMidiInitialized)
    {
        try
        {
            var initErr = ensureMidiInitialized();
            if (initErr is not null)
                return (false, initErr);

            var devices = PMUtil.GetOutputDevices();
            var device = ResolveMidiDevice(midi, devices);
            if (device is null)
                return (false, Strings.OutputDeviceNotFound);

            using var outDevice = new MIDIOutputDevice(device.Value.Id);
            var openErr = outDevice.Open();
            if (openErr != PmError.NoError)
                return (false, PMUtil.GetErrorText(openErr) ?? openErr.ToString());

            return (true, Strings.Format(
                nameof(Strings.EndpointProbeOkDeviceFormat),
                device.Value.Name ?? Strings.Format(nameof(Strings.DeviceHashIdFormat), device.Value.Id)));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static PmDeviceEntry? ResolveMidiDevice(MidiActionEndpoint midi, IReadOnlyList<PmDeviceEntry> devices)
    {
        if (midi.DeviceId is { } id)
        {
            var byId = devices.FirstOrDefault(d => d.Id == id);
            if (byId.Id == id)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(midi.DeviceName))
        {
            var byName = devices.FirstOrDefault(d =>
                string.Equals(d.Name, midi.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(byName.Name))
                return byName;
        }

        return devices.FirstOrDefault();
    }
}
