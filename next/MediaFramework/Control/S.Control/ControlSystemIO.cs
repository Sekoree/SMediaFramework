using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace S.Control;

public sealed record ControlSystemDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? Generator { get; init; }

    public ControlSystemConfig ControlSystem { get; init; } = new();
}

public static class ControlSystemIO
{
    public const string FileExtension = "scontrol";

    public static async Task<ControlSystemDocument> LoadDocumentAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer
            .DeserializeAsync(stream, ControlSystemJsonContext.Default.ControlSystemDocument, cancellationToken)
            .ConfigureAwait(false);
        if (document is null)
            throw new InvalidDataException($"Control file '{path}' contains no JSON object.");
        if (document.SchemaVersion is < 1 or > ControlSystemDocument.CurrentSchemaVersion)
            throw new UnsupportedControlSystemSchemaVersionException(
                document.SchemaVersion,
                ControlSystemDocument.CurrentSchemaVersion);

        return document;
    }

    public static async Task<ControlSystemConfig> LoadConfigAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        (await LoadDocumentAsync(path, cancellationToken).ConfigureAwait(false)).ControlSystem;

    public static async Task SaveDocumentAsync(
        ControlSystemDocument document,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await AtomicControlJsonFile.SaveAsync(
                document,
                path,
                ControlSystemJsonContext.Default.ControlSystemDocument,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task SaveConfigAsync(
        ControlSystemConfig config,
        string path,
        string? generator = null,
        CancellationToken cancellationToken = default) =>
        SaveDocumentAsync(
            new ControlSystemDocument { Generator = generator, ControlSystem = config ?? new ControlSystemConfig() },
            path,
            cancellationToken);

    public static string Serialize(ControlSystemDocument document) =>
        JsonSerializer.Serialize(document, ControlSystemJsonContext.Default.ControlSystemDocument);

    public static ControlSystemDocument Deserialize(string json)
    {
        var document = JsonSerializer.Deserialize(json, ControlSystemJsonContext.Default.ControlSystemDocument);
        if (document is null)
            throw new InvalidDataException("Control JSON is empty.");
        if (document.SchemaVersion is < 1 or > ControlSystemDocument.CurrentSchemaVersion)
            throw new UnsupportedControlSystemSchemaVersionException(
                document.SchemaVersion,
                ControlSystemDocument.CurrentSchemaVersion);

        return document;
    }
}

internal static class AtomicControlJsonFile
{
    public static async Task SaveAsync<T>(
        T value,
        string path,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        else
            directory = Directory.GetCurrentDirectory();

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, jsonTypeInfo, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}

public sealed class UnsupportedControlSystemSchemaVersionException : Exception
{
    public UnsupportedControlSystemSchemaVersionException(int fileVersion, int supportedVersion)
        : base($"Unsupported control file schema version {fileVersion}; this build supports up to {supportedVersion}.")
    {
        FileVersion = fileVersion;
        SupportedVersion = supportedVersion;
    }

    public int FileVersion { get; }

    public int SupportedVersion { get; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlSystemDocument))]
[JsonSerializable(typeof(ControlSystemConfig))]
[JsonSerializable(typeof(ControlDeviceProfile))]
[JsonSerializable(typeof(ControlDevicePortProfile))]
[JsonSerializable(typeof(ControlControlProfile))]
[JsonSerializable(typeof(ControlCommandProfile))]
[JsonSerializable(typeof(ControlLayerProfile))]
[JsonSerializable(typeof(ControlDeviceTaskProfile))]
[JsonSerializable(typeof(ControlDeviceProfileBehaviors))]
[JsonSerializable(typeof(ControlProtocolMaintenanceBehavior))]
[JsonSerializable(typeof(ControlOscListenerConfig))]
[JsonSerializable(typeof(ControlMonitorOptions))]
[JsonSerializable(typeof(ControlDeviceInstanceConfig))]
[JsonSerializable(typeof(ControlDeviceBindingConfig))]
[JsonSerializable(typeof(ControlPeriodicOscSendConfig))]
[JsonSerializable(typeof(ControlOscArgumentConfig))]
[JsonSerializable(typeof(ControlLayerConfig))]
[JsonSerializable(typeof(ControlScriptConfig))]
[JsonSerializable(typeof(ControlScriptFailurePolicy))]
[JsonSerializable(typeof(ControlScriptTriggerConfig))]
internal partial class ControlSystemJsonContext : JsonSerializerContext;
