using S.Media.NDI.Video;

namespace VideoPlaybackSmoke;

/// <summary>Library defaults without embedding the media path.</summary>
public readonly record struct SmokePlaybackOptions(
    bool NDIEnable = false,
    string NDIName = SmokeDefaults.DefaultNDIOutputName,
    bool NoHardwareDecode = false,
    bool LinuxDrmDmabufGl = false,
    bool WindowsD3d11SharedGl = false,
    bool WindowsD3d11ZeroHostGl = false,
    bool WindowsD3d11GlSharedHandleOnly = false,
    int AudioChunkSamples = SmokeDefaults.DefaultAudioChunkSamples,
    int? DeviceLatencyMs = null,
    int NDIAudioAggregateSamples = -1,
    int? NDIAudioPumpCapacityChunks = null,
    bool NDIClockVideo = false,
    bool NDIDisableWallPace = false,
    int NDIVideoPumpFrames = SmokeDefaults.DefaultNDIVideoPumpFrames,
    NDIVideoTimecodeMode NDIVideoTimecodeMode = NDIVideoTimecodeMode.PresentationRelativeTicks,
    int NDIWaitFirstReceiverMs = 0)
{
    public static SmokePlaybackOptions Default => new(NDIName: SmokeDefaults.DefaultNDIOutputName);

    public SmokeToolOptions ToToolOptions(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        return new SmokeToolOptions(
            mediaPath,
            NDIEnable,
            NDIName,
            NoHardwareDecode,
            LinuxDrmDmabufGl,
            WindowsD3d11SharedGl,
            WindowsD3d11ZeroHostGl,
            WindowsD3d11GlSharedHandleOnly,
            AudioChunkSamples,
            DeviceLatencyMs,
            NDIAudioAggregateSamples,
            NDIAudioPumpCapacityChunks,
            NDIClockVideo,
            NDIDisableWallPace,
            NDIVideoPumpFrames,
            NDIVideoTimecodeMode,
            NDIWaitFirstReceiverMs);
    }
}
