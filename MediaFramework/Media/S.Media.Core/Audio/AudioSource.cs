using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>Discoverable entry points for file- and stream-backed <see cref="IAudioSource"/> instances.</summary>
public static class AudioSource
{
    /// <summary>Opens an audio file. Requires <c>.UseFFmpeg()</c> on <see cref="MediaFrameworkRuntime"/>.</summary>
    public static IAudioSource OpenFile(string path, AudioSourceOpenOptions? options = null)
        => OpenFile(path, options, scopedFactory: null);

    /// <summary>
    /// Opens an audio file. Resolution order is scoped factory, process-wide plugin factory.
    /// </summary>
    public static IAudioSource OpenFile(
        string path,
        AudioSourceOpenOptions? options,
        Func<string, AudioSourceOpenOptions?, IAudioSource>? scopedFactory)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var factory = scopedFactory ?? MediaFrameworkPlugins.AudioSourceFileFactory
            ?? throw new InvalidOperationException(
                "AudioSource.OpenFile: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(path, options);
    }

    /// <summary>Opens the audio track of a media stream. Requires FFmpeg init.</summary>
    public static IAudioSource OpenStream(Stream stream, AudioSourceOpenOptions? options = null)
        => OpenStream(stream, options, scopedFactory: null);

    /// <summary>
    /// Opens the audio track of a media stream. Resolution order is scoped factory, process-wide plugin factory.
    /// </summary>
    public static IAudioSource OpenStream(
        Stream stream,
        AudioSourceOpenOptions? options,
        Func<Stream, AudioSourceOpenOptions?, IAudioSource>? scopedFactory)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var factory = scopedFactory ?? MediaFrameworkPlugins.AudioSourceStreamFactory
            ?? throw new InvalidOperationException(
                "AudioSource.OpenStream: no backend installed — call MediaFrameworkRuntime.Init().UseFFmpeg() (requires S.Media.FFmpeg).");
        return factory(stream, options);
    }
}
