namespace VideoPlaybackSmoke;

/// <summary>Optional SDL window title and initial size caps for <see cref="VideoPlaybackSmokeSession.TryCreate"/>.</summary>
public readonly record struct SmokePresentationOptions(
    string? WindowTitle,
    int MaxSdlWindowWidth,
    int MaxSdlWindowHeight)
{
    public static SmokePresentationOptions Default =>
        new(null, SmokeDefaults.MaxSdlWindowWidth, SmokeDefaults.MaxSdlWindowHeight);
}
