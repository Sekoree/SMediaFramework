using System.Text.Json;
using System.Text.Json.Serialization;

namespace S.Media.Core.Audio;

/// <summary>
/// A named, file-persistable gain matrix for <see cref="AudioRouter.ApplyMatrix"/> — the shareable
/// form of a routing-matrix layout ("5.1 → stereo broadcast fold", "stems to monitors", …).
/// </summary>
/// <remarks>
/// <para>
/// Gains are <strong>linear</strong> (1.0 = unity, 0 = no route), indexed
/// <c>[sourceChannel][outputChannel]</c>; jagged rows keep the JSON human-editable. Built-in
/// standard layouts come from <see cref="AudioChannelLayoutPresets"/>; this type is for the custom
/// matrices hosts let operators save and trade between shows/machines. Serialization is
/// source-generated (NativeAOT-safe).
/// </para>
/// </remarks>
public sealed record AudioMixPreset
{
    public const string CurrentSchema = "MediaFramework.AudioMixPreset/v1";

    /// <summary>Conventional file extension (hosts may prepend their own pickers/filters).</summary>
    public const string FileExtension = "mfmix";

    public string Schema { get; init; } = CurrentSchema;

    public required string Name { get; init; }

    public int SourceChannels { get; init; }

    public int OutputChannels { get; init; }

    /// <summary>Linear cell gains, <c>Gains[sourceChannel][outputChannel]</c>. Rows may be ragged in
    /// hand-edited files; missing cells read as 0 (no route).</summary>
    public required float[][] Gains { get; init; }

    /// <summary>Dense matrix for <see cref="AudioRouter.ApplyMatrix"/> (ragged rows zero-padded).</summary>
    public float[,] ToMatrix()
    {
        var src = Math.Max(SourceChannels, Gains.Length);
        var dst = OutputChannels;
        foreach (var row in Gains)
            dst = Math.Max(dst, row?.Length ?? 0);
        var m = new float[src, Math.Max(0, dst)];
        for (var s = 0; s < Gains.Length; s++)
        {
            var row = Gains[s];
            if (row is null) continue;
            for (var d = 0; d < row.Length; d++)
                m[s, d] = row[d];
        }

        return m;
    }

    public static AudioMixPreset FromMatrix(string name, float[,] gains)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(gains);
        var src = gains.GetLength(0);
        var dst = gains.GetLength(1);
        var rows = new float[src][];
        for (var s = 0; s < src; s++)
        {
            rows[s] = new float[dst];
            for (var d = 0; d < dst; d++)
                rows[s][d] = gains[s, d];
        }

        return new AudioMixPreset { Name = name, SourceChannels = src, OutputChannels = dst, Gains = rows };
    }

    public void Save(string path)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, this, AudioMixPresetJsonContext.Default.AudioMixPreset);
    }

    /// <summary>Loads and validates a preset file. Throws <see cref="InvalidDataException"/> on a
    /// foreign schema or an empty/None payload so hosts surface one clear message.</summary>
    public static AudioMixPreset Load(string path)
    {
        using var stream = File.OpenRead(path);
        var preset = JsonSerializer.Deserialize(stream, AudioMixPresetJsonContext.Default.AudioMixPreset)
                     ?? throw new InvalidDataException($"'{path}' contains no JSON object.");
        if (!string.Equals(preset.Schema, CurrentSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"'{path}' is not an audio mix preset (schema '{preset.Schema}').");
        if (preset.Gains is null || preset.Gains.Length == 0)
            throw new InvalidDataException($"'{path}' carries no gain rows.");
        return preset;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AudioMixPreset))]
internal sealed partial class AudioMixPresetJsonContext : JsonSerializerContext;
