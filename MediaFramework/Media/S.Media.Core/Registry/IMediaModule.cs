namespace S.Media.Core.Registry;

/// <summary>
/// A build-time capability contributor (FFmpeg, PortAudio, SDL3, …). Modules register their providers
/// through the typed builder; this is the AOT-pure replacement for the old static
/// <c>MediaFrameworkPlugins</c> slots (P2). Native C-ABI plugins are adapted into modules by S.Abi (05).
/// </summary>
public interface IMediaModule
{
    /// <summary>Stable module name, e.g. <c>"FFmpeg"</c> / <c>"PortAudio"</c>. Diagnostics + ordering.</summary>
    string Name { get; }

    /// <summary>Register this module's capabilities on <paramref name="builder"/>.</summary>
    void Register(IMediaRegistryBuilder builder);
}
