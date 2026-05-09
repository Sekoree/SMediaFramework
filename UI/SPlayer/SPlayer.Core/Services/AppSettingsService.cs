using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core;
using S.Media.Playback;

namespace SPlayer.Core.Services;

/// <summary>
/// <see cref="System.Text.Json"/> source-generation context for
/// <see cref="AppSettings"/>. Using this instead of the reflection-based
/// serializer keeps the settings load/save path NativeAOT- and
/// trim-compatible: every reachable type (including
/// <c>List&lt;string&gt;</c>, <c>Dictionary&lt;string,int&gt;</c> and
/// <see cref="AvDriftSettings"/>) gets a generated metadata stub at compile
/// time, so no runtime reflection / dynamic code is needed.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Persistent user-facing preferences. Lives at <see cref="DefaultPath"/> as a
/// human-editable JSON file. Anything the user toggles in the UI that should
/// survive an app restart belongs here.
/// </summary>
public sealed record AppSettings
{
    /// <summary>
    /// Output rows that should be auto-selected on startup / when the matching
    /// endpoint is added. Identifiers are <c>RowKey</c> values formatted as
    /// <c>{Audio|Video|Ndi}:Name</c>, matching <see cref="ViewModels.PlayerViewModel"/>.
    /// </summary>
    public List<string> DefaultOutputs { get; init; } = new();

    /// <summary>NDI rows: per-row default A/V mode (0 = Both, 1 = AudioOnly, 2 = VideoOnly).</summary>
    public Dictionary<string, int> NdiAveDefaults { get; init; } = new();

    public double VolumePercent { get; init; } = 100;
    public bool Loop { get; init; }
    public bool AutoAdvance { get; init; } = true;

    /// <summary>Snapshot of <see cref="AvDriftCorrectionOptions"/> defaults.</summary>
    public AvDriftSettings AvDrift { get; init; } = new();

    /// <summary>Whether the player tab should remember per-playlist output overrides on save.</summary>
    public bool RememberPlaylistOverrides { get; init; } = true;

    /// <summary>
    /// When true, every <c>AvaloniaOpenGlVideoEndpoint</c> attached to the
    /// player paces its render-request loop to the source's frame rate
    /// instead of vsync. Reduces CPU/GPU load for low-fps content; may
    /// introduce a small per-tick latency for refresh-rate-bound use cases.
    /// </summary>
    public bool LimitRenderFpsToSource { get; init; }

    [JsonIgnore]
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SPlayer",
        "settings.json");
}

public sealed record AvDriftSettings
{
    public double InitialDelaySec { get; init; } = 10;
    public double IntervalSec { get; init; } = 5;
    public double MinDriftMs { get; init; } = 8;
    public double IgnoreOutlierDriftMs { get; init; } = 250;
    public int OutlierConsecutiveSamples { get; init; } = 3;
    public double CorrectionGain { get; init; } = 0.15;
    // §heavy-media-fixes phase 6 — was 5/250, raised so SPlayer can converge
    // multi-hundred-ms drifts on heavy media. Settings UI lets the user tune
    // back down for sinks that prefer gentler corrections.
    public double MaxStepMs { get; init; } = 20;
    public double MaxAbsOffsetMs { get; init; } = 2000;

    public AvDriftCorrectionOptions ToOptions() => new()
    {
        InitialDelay = TimeSpan.FromSeconds(Math.Max(0, InitialDelaySec)),
        Interval = TimeSpan.FromSeconds(Math.Max(0.1, IntervalSec)),
        MinDriftMs = Math.Max(0, MinDriftMs),
        IgnoreOutlierDriftMs = Math.Max(MinDriftMs, IgnoreOutlierDriftMs),
        OutlierConsecutiveSamples = Math.Max(1, OutlierConsecutiveSamples),
        CorrectionGain = Math.Clamp(CorrectionGain, 0, 1),
        MaxStepMs = Math.Max(0, MaxStepMs),
        MaxAbsOffsetMs = Math.Max(0, MaxAbsOffsetMs)
    };
}

/// <summary>
/// Loads / saves <see cref="AppSettings"/> from a single well-known JSON file.
/// All operations are synchronous and best-effort: missing / corrupt files are
/// recovered by returning <see cref="AppSettings"/> defaults so the UI is never
/// blocked behind the store.
/// </summary>
public sealed class AppSettingsService
{
    private static readonly ILogger Log = MediaCoreLogging.GetLogger("SPlayer.AppSettingsService");

    private readonly string _path;
    private readonly Lock _saveLock = new();

    public AppSettingsService(string? path = null)
    {
        _path = path ?? AppSettings.DefaultPath;
    }

    public string Path => _path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
            var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // Corrupt config should not break the app — start fresh and let the
            // next save overwrite the bad file.
            Log.LogWarning(ex, "AppSettings: load failed at {Path}; using defaults.", _path);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            lock (_saveLock)
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Atomic-ish write: temp file + replace, so a crash mid-write does
                // not leave the user with a half-written settings.json that fails to
                // parse on next launch.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
                if (File.Exists(_path))
                    File.Replace(tmp, _path, null);
                else
                    File.Move(tmp, _path);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "AppSettings: save failed at {Path}; in-memory settings still apply.", _path);
        }
    }
}
