using S.Media.NDI.Video;

namespace VideoPlaybackSmoke;

/// <summary>Library defaults without embedding the media path.</summary>
public readonly record struct SmokePlaybackOptions(
    bool NdiEnable = false,
    string NdiName = SmokeDefaults.DefaultNdiOutputName,
    bool NoHardwareDecode = false,
    bool LinuxDrmDmabufGl = false,
    bool WindowsD3d11SharedGl = false,
    bool WindowsD3d11ZeroHostGl = false,
    bool WindowsD3d11GlSharedHandleOnly = false,
    int AudioChunkSamples = SmokeDefaults.DefaultAudioChunkSamples,
    int? DeviceLatencyMs = null,
    int NdiAudioAggregateSamples = -1,
    int? NdiAudioPumpCapacityChunks = null,
    bool NdiClockVideo = false,
    bool NdiDisableWallPace = false,
    int NdiVideoPumpFrames = SmokeDefaults.DefaultNdiVideoPumpFrames,
    NDIVideoTimecodeMode NdiVideoTimecodeMode = NDIVideoTimecodeMode.PresentationRelativeTicks,
    int NdiWaitFirstReceiverMs = 0)
{
    public static SmokePlaybackOptions Default => new(NdiName: SmokeDefaults.DefaultNdiOutputName);

    public SmokeToolOptions ToToolOptions(string mediaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        return new SmokeToolOptions(
            mediaPath,
            NdiEnable,
            NdiName,
            NoHardwareDecode,
            LinuxDrmDmabufGl,
            WindowsD3d11SharedGl,
            WindowsD3d11ZeroHostGl,
            WindowsD3d11GlSharedHandleOnly,
            AudioChunkSamples,
            DeviceLatencyMs,
            NdiAudioAggregateSamples,
            NdiAudioPumpCapacityChunks,
            NdiClockVideo,
            NdiDisableWallPace,
            NdiVideoPumpFrames,
            NdiVideoTimecodeMode,
            NdiWaitFirstReceiverMs);
    }
}
