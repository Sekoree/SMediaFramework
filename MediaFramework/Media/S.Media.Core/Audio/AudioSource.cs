using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>Discoverable entry points for file- and stream-backed <see cref="IAudioSource"/> instances.</summary>
public static class AudioSource
{
    /// <summary>Opens an audio file. Requires <c>.UseFFmpeg()</c> on <see cref="MediaFrameworkRuntime"/>.</summary>
    public static IAudioSource OpenFile(string path, AudioSourceOpenOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var factory = MediaFrameworkPlugins.AudioSourceFileFactory
            ?? throw new InvalidOperationException(
                "AudioSource.OpenFile: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(path, options);
    }

    /// <summary>Opens the audio track of a media stream. Requires FFmpeg init.</summary>
    public static IAudioSource OpenStream(Stream stream, AudioSourceOpenOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var factory = MediaFrameworkPlugins.AudioSourceStreamFactory
            ?? throw new InvalidOperationException(
                "AudioSource.OpenStream: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(stream, options);
    }
}
