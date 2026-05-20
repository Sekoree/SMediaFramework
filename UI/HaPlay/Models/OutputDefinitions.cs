using System.Text.Json.Serialization;
using S.Media.Core.Video;

namespace HaPlay.Models;

public enum ManagedOutputKind
{
    PortAudio,
    NDI,
    SdlOpenGlVideo,
    AvaloniaOpenGlVideo,
}

public enum VideoOutputEngine
{
    SdlOpenGl,
    AvaloniaOpenGl,
}

public enum VideoSurfaceMode
{
    Windowed,
    FullScreen,
}

public enum NDIOutputStreamMode
{
    VideoAndAudio,
    VideoOnly,
    AudioOnly,
}

/// <summary>User-defined output entry (playback wiring consumes this later).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(PortAudioOutputDefinition), typeDiscriminator: "portAudio")]
[JsonDerivedType(typeof(LocalVideoOutputDefinition), typeDiscriminator: "localVideo")]
[JsonDerivedType(typeof(NDIOutputDefinition), typeDiscriminator: "ndi")]
public abstract record OutputDefinition(Guid Id, string DisplayName)
{
    [JsonIgnore]
    public abstract ManagedOutputKind Kind { get; }
}

public sealed record PortAudioOutputDefinition(
    Guid Id,
    string DisplayName,
    int HostApiIndex,
    string HostApiName,
    int GlobalDeviceIndex,
    string DeviceName,
    int ChannelCount,
    int SampleRate) : OutputDefinition(Id, DisplayName)
{
    [JsonIgnore]
    public override ManagedOutputKind Kind => ManagedOutputKind.PortAudio;
}

public sealed record LocalVideoOutputDefinition(
    Guid Id,
    string DisplayName,
    VideoOutputEngine Engine,
    VideoSurfaceMode SurfaceMode,
    int ScreenIndex,
    int? WindowWidth,
    int? WindowHeight,
    // Phase A forward-compat field (§3.4): when set, this output is a clone of the referenced parent.
    // Runtime semantics land in Phase B; for now the field is persisted so a saved project survives.
    Guid? CloneOfId = null) : OutputDefinition(Id, DisplayName)
{
    [JsonIgnore]
    public override ManagedOutputKind Kind =>
        Engine == VideoOutputEngine.SdlOpenGl ? ManagedOutputKind.SdlOpenGlVideo : ManagedOutputKind.AvaloniaOpenGlVideo;
}

public sealed record NDIOutputDefinition(
    Guid Id,
    string DisplayName,
    string SourceName,
    string? Groups,
    NDIOutputStreamMode StreamMode,
    int AudioChannelCount,
    int AudioSampleRate,
    // Phase A forward-compat fields (§3.3): pixel-format / resolution locks for the negotiated NDI output.
    // The framework's NDIOutput router branch needs work before these are honoured (§9.6); persisted now
    // so a saved project doesn't need a schemaVersion bump when Phase B wires them through.
    PixelFormat? PixelFormatLock = null,
    int? ResolutionLockWidth = null,
    int? ResolutionLockHeight = null) : OutputDefinition(Id, DisplayName)
{
    [JsonIgnore]
    public override ManagedOutputKind Kind => ManagedOutputKind.NDI;
}
