using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>
/// Process-wide hook that lets <see cref="AudioRouter.AddSource(IAudioSource, string?, bool)"/> opt-in
/// to transparent resampling when a source's rate differs from the router's nominal rate.
/// </summary>
/// <remarks>
/// <para>
/// Core itself ships no resampler — <see cref="SourceWrapper"/> is <c>null</c> until a package that
/// provides one registers itself. The shipping implementation is in <c>S.Media.FFmpeg</c>:
/// <c>FFmpegRuntime.EnsureInitialized</c> installs a wrapper that builds a
/// <c>ResamplingAudioSource</c> (libswresample). Other resampler packages can install their own.
/// </para>
/// <para>
/// The factory receives the inner source and the target sample rate; it returns a wrapper presenting
/// at the target rate. The wrapper is expected to take ownership of <c>inner</c> — i.e. when the
/// wrapper is disposed, it disposes <c>inner</c> too — so callers that pass the wrapper to
/// <see cref="AudioPlayer.AddOwnedSource(IAudioSource, string?, bool)"/> get a clean lifecycle.
/// </para>
/// </remarks>
public static class AudioRouterAutoResample
{
    /// <summary>
    /// Factory: <c>(innerSource, targetSampleRate) =&gt; wrappedSource</c>. <c>null</c> until a
    /// resampler package installs one. The wrapper assumes ownership of <c>inner</c>.
    /// </summary>
    [Obsolete("Use MediaFrameworkPlugins.AudioResampleSourceWrapper")]
    public static Func<IAudioSource, int, IAudioSource>? SourceWrapper
    {
        get => MediaFrameworkPlugins.AudioResampleSourceWrapper;
        set => MediaFrameworkPlugins.AudioResampleSourceWrapper = value;
    }
}
