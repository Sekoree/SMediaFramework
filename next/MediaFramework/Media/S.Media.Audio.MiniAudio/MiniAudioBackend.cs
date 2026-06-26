
namespace S.Media.Audio.MiniAudio;

/// <summary>
/// Backend-neutral miniaudio adapter: device discovery plus ready-to-use capture/playback devices.
/// </summary>
public sealed class MiniAudioBackend : IAudioBackend
{
    public string Name => "miniaudio";

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices()
    {
        using var context = MiniAudioContext.Create();
        return context.Enumerate(MiniAudioDeviceType.Playback);
    }

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices()
    {
        using var context = MiniAudioContext.Create();
        return context.Enumerate(MiniAudioDeviceType.Capture);
    }

    public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        var opt = options ?? new AudioBackendOptions();
        var output = new MiniAudioOutput(
            format,
            deviceId,
            FramesPerBuffer(format, opt),
            RingCapacityFrames(opt));
        return Started(output);
    }

    public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        var opt = options ?? new AudioBackendOptions();
        var input = new MiniAudioInput(
            format,
            deviceId,
            FramesPerBuffer(format, opt),
            RingCapacityFrames(opt));
        try
        {
            input.Start();
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static MiniAudioOutput Started(MiniAudioOutput output)
    {
        try
        {
            output.Start();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static int FramesPerBuffer(AudioFormat format, AudioBackendOptions opt)
    {
        if (opt.FramesPerBuffer > 0)
            return opt.FramesPerBuffer;
        if (opt.SuggestedLatencySeconds is { } latency && latency > 0)
            return Math.Max(16, (int)Math.Round(format.SampleRate * latency));
        return 0;
    }

    private static int RingCapacityFrames(AudioBackendOptions opt) =>
        opt.RingCapacityFrames > 0 ? opt.RingCapacityFrames : 16384;
}
