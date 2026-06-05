using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.PortAudio;

namespace HaPlay.Playback;

/// <summary>Opens a PortAudio capture stream for live input (§6.4, §6.11 pre-connect).</summary>
internal static class PortAudioInputConnector
{
    public static bool TryOpen(
        PortAudioInputPlaylistItem item,
        [NotNullWhen(true)] out PortAudioInput? input,
        out AudioFormat format,
        out string? errorMessage)
    {
        input = null;
        format = default;
        errorMessage = null;

        int? deviceIndex = null;
        foreach (var d in PortAudioDeviceCatalog.EnumerateInputDevices())
        {
            if (string.Equals(d.Name, item.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                deviceIndex = d.GlobalDeviceIndex;
                break;
            }
        }

        if (deviceIndex is null && item.GlobalDeviceIndex is { } gi)
            deviceIndex = gi;

        if (deviceIndex is null)
        {
            errorMessage = $"PortAudio input '{item.DeviceName}' not found.";
            return false;
        }

        try
        {
            format = new AudioFormat(item.SampleRate, item.Channels);
            input = new PortAudioInput(format, deviceIndex, item.SuggestedLatency);
            input.Start();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            try { input?.Dispose(); } catch { /* best effort */ }
            input = null;
            return false;
        }
    }
}
