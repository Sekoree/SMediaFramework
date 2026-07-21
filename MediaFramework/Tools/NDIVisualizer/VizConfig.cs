using System.Text.Json;
using System.Text.Json.Serialization;

namespace S.Media.Tools.NDIVisualizer;

/// <summary>
/// Saved settings for one visualizer session. Devices are stored by <em>name</em> (host API + device),
/// not PortAudio index, so a config keeps working after devices are re-ordered / hot-plugged. Channels are
/// stored as the 1-based numbers the user typed at the prompt.
/// </summary>
public sealed record VizConfig
{
    /// <summary>PortAudio host API name (e.g. "ALSA", "JACK Audio Connection Kit", "WASAPI").</summary>
    public string HostApiName { get; init; } = "";

    /// <summary>Input device name as reported by PortAudio, matched case-insensitively within the host API.</summary>
    public string DeviceName { get; init; } = "";

    /// <summary>1-based channel numbers to visualize (the device is always opened at its max channel count).</summary>
    public int[] Channels { get; init; } = [1];

    /// <summary>Directory scanned recursively for *.milk presets; null/empty = projectM's built-in idle preset.</summary>
    public string? PresetDirectory { get; init; }

    public int Width { get; init; } = 1920;

    public int Height { get; init; } = 1080;

    public int Fps { get; init; } = 60;

    public string NDIName { get; init; } = "MFPlayer Visualizer";

    /// <summary>Input gain in decibels applied before the signal reaches projectM and the level meter.</summary>
    public double GainDb { get; init; }

    /// <summary>Capture sample rate; 0 = follow the device's default rate (projectM's tap matches it).</summary>
    public int SampleRate { get; init; }

    /// <summary>Seconds one preset plays before advancing.</summary>
    public double PresetDurationSeconds { get; init; } = 30;

    /// <summary>Random preset order (true) or alphabetical (false).</summary>
    public bool Shuffle { get; init; } = true;

    public static VizConfig? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, VizConfigJsonContext.Default.VizConfig);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warning: could not read config '{path}': {ex.Message}");
            return null;
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, VizConfigJsonContext.Default.VizConfig);
        File.WriteAllText(path, json);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(VizConfig))]
internal sealed partial class VizConfigJsonContext : JsonSerializerContext;
