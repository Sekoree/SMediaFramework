namespace HaPlay.Models;

public enum ManagedOutputKind
{
    PortAudio,
    Ndi,
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

public enum NdiOutputStreamMode
{
    VideoAndAudio,
    VideoOnly,
    AudioOnly,
}

/// <summary>User-defined output entry (playback wiring consumes this later).</summary>
public abstract record OutputDefinition(Guid Id, string DisplayName)
{
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
    public override ManagedOutputKind Kind => ManagedOutputKind.PortAudio;
}

public sealed record LocalVideoOutputDefinition(
    Guid Id,
    string DisplayName,
    VideoOutputEngine Engine,
    VideoSurfaceMode SurfaceMode,
    int ScreenIndex,
    int? WindowWidth,
    int? WindowHeight) : OutputDefinition(Id, DisplayName)
{
    public override ManagedOutputKind Kind =>
        Engine == VideoOutputEngine.SdlOpenGl ? ManagedOutputKind.SdlOpenGlVideo : ManagedOutputKind.AvaloniaOpenGlVideo;
}

public sealed record NdiOutputDefinition(
    Guid Id,
    string DisplayName,
    string SourceName,
    string? Groups,
    NdiOutputStreamMode StreamMode,
    int AudioChannelCount,
    int AudioSampleRate) : OutputDefinition(Id, DisplayName)
{
    public override ManagedOutputKind Kind => ManagedOutputKind.Ndi;
}
