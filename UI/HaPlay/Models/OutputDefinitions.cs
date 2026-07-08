using System.Globalization;
using System.Text.Json.Serialization;
using S.Media.Core.Video;

namespace HaPlay.Models;

public enum ManagedOutputKind
{
    PortAudio,
    NDI,
    SDLOpenGlVideo,
    AvaloniaOpenGlVideo,
}

public enum VideoOutputEngine
{
    SDLOpenGl,
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

/// <summary>How a LOCAL video output fits the composited canvas into its window/screen. This is a display-side
/// choice only: it maps to the SDL/Avalonia output's viewport fit and never changes what the compositor
/// produces, so NDI (and any other) outputs continue to carry the full letterboxed canvas untouched.</summary>
public enum LocalVideoFit
{
    /// <summary>Preserve aspect; letterbox/pillarbox the canvas into the display (default). No content is lost.</summary>
    Letterbox,

    /// <summary>Preserve aspect but fill the whole display, cropping the overflowing edges.</summary>
    Cover,

    /// <summary>Fill the whole display, ignoring aspect ratio (may distort the image).</summary>
    Stretch,
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

    /// <summary>
    /// Operator-given name (UI rewrite P2, plan §5): the single naming truth shown wherever this
    /// output appears — I/O rows, player routing matrix columns, cue route pickers. Null/blank
    /// falls back to the device-derived <see cref="DisplayName"/>. Absent in pre-P2 project files
    /// (deserializes as null — no migration needed).
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>The name to display: <see cref="Alias"/> when set, else <see cref="DisplayName"/>.</summary>
    [JsonIgnore]
    public string EffectiveName => string.IsNullOrWhiteSpace(Alias) ? DisplayName : Alias;
}

public sealed record PortAudioOutputDefinition(
    Guid Id,
    string DisplayName,
    int HostApiIndex,
    string HostApiName,
    int GlobalDeviceIndex,
    string DeviceName,
    int ChannelCount,
    int SampleRate,
    string AudioBackendName = "PortAudio",
    string? AudioBackendDeviceId = null) : OutputDefinition(Id, DisplayName)
{
    public const string PortAudioBackendName = "PortAudio";

    [JsonIgnore]
    public override ManagedOutputKind Kind => ManagedOutputKind.PortAudio;

    [JsonIgnore]
    public string EffectiveAudioBackendName =>
        string.IsNullOrWhiteSpace(AudioBackendName) ? PortAudioBackendName : AudioBackendName;

    [JsonIgnore]
    public bool UsesPortAudioBackend =>
        string.Equals(EffectiveAudioBackendName, PortAudioBackendName, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string? EffectiveAudioBackendDeviceId =>
        !string.IsNullOrWhiteSpace(AudioBackendDeviceId)
            ? AudioBackendDeviceId
            : UsesPortAudioBackend
                ? GlobalDeviceIndex.ToString(CultureInfo.InvariantCulture)
                : null;
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
    Guid? CloneOfId = null,
    // Optional background image shown as the idle frame when no media is routed to this output. Padded
    // to the output's pixel format (letterboxed) — applies to both the SDL and Avalonia engines.
    string? BackgroundImagePath = null,
    // Keep the output window above other windows. Honoured by the Avalonia (in-app preview) engine;
    // the SDL standalone-window engine ignores it (no always-on-top hook exposed by the framework).
    bool AlwaysOnTop = false,
    // How this display fits the composited canvas (letterbox by default). Display-side only — the compositor
    // and NDI outputs are unaffected. Absent in pre-fit project files (deserializes as Letterbox, the prior
    // hardcoded behaviour), so no schema migration is needed.
    LocalVideoFit VideoFit = LocalVideoFit.Letterbox) : OutputDefinition(Id, DisplayName)
{
    [JsonIgnore]
    public override ManagedOutputKind Kind =>
        Engine == VideoOutputEngine.SDLOpenGl ? ManagedOutputKind.SDLOpenGlVideo : ManagedOutputKind.AvaloniaOpenGlVideo;
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
