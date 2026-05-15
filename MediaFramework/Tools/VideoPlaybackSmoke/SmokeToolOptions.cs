using S.Media.NDI.Video;

namespace VideoPlaybackSmoke;

/// <summary>CLI-shaped options for <see cref="VideoPlaybackSmokeSession.TryCreate"/>.</summary>
public readonly record struct SmokeToolOptions(
    string MediaPath,
    bool NDIEnable,
    string NDIName,
    bool NoHardwareDecode,
    bool LinuxDrmDmabufGl,
    bool WindowsD3d11SharedGl,
    bool WindowsD3d11ZeroHostGl,
    bool WindowsD3d11GlSharedHandleOnly,
    int AudioChunkSamples,
    int? DeviceLatencyMs,
    int NDIAudioAggregateSamples,
    int? NDIAudioPumpCapacityChunks,
    bool NDIClockVideo,
    bool NDIDisableWallPace,
    int NDIVideoPumpFrames,
    NDIVideoTimecodeMode NDIVideoTimecodeMode,
    int NDIWaitFirstReceiverMs);
