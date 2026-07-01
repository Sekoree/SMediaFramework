using Microsoft.Extensions.Logging;
using S.Media.Audio.MiniAudio;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.NDI;

namespace HaPlay;

/// <summary>
/// The process-wide media registry — the rewritten framework's single composition root. Replaces the old
/// static <c>MediaFrameworkRuntime.Init().UseFFmpeg()/UsePortAudio()/…</c> + <c>AudioBackends</c> +
/// <c>MediaFrameworkPlugins</c> surface (all removed in the AOT-pure rewrite, P2). Built once at startup;
/// engines open media via <c>MediaPlayer.OpenFile(MediaRuntime.Registry, MediaRuntime.Registry, …)</c> and resolve output devices
/// from <see cref="IMediaRegistry.AudioBackends"/>. Module registration is best-effort per module so an
/// unavailable backend (e.g. NDI on a host without the runtime) degrades rather than blocking startup.
/// </summary>
internal static class MediaRuntime
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.MediaRuntime");
    private static readonly object Gate = new();
    private static MediaHost? _host;

    /// <summary>The owning host (NXT-05): builds the registry and, when disposed on app shutdown, releases the
    /// modules' native runtime holds (PortAudio <c>Pa_Terminate</c>, NDI runtime) instead of leaking them. Lazily
    /// built on first access (idempotent + thread-safe).</summary>
    private static MediaHost Host
    {
        get
        {
            if (_host is not null)
                return _host;
            lock (Gate)
                return _host ??= Build();
        }
    }

    /// <summary>The built registry. Lazily built on first access (idempotent + thread-safe), so anything that
    /// resolves backends/decoders works whether or not <see cref="Initialize"/> ran first (tests, dialogs).</summary>
    public static IMediaRegistry Registry => Host.Registry;

    public static bool IsInitialized => _host is not null;

    /// <summary>Eagerly builds the registry at startup (for deterministic timing/logging). Idempotent.</summary>
    public static void Initialize() => _ = Host;

    /// <summary>
    /// Disposes the owning host at app shutdown, releasing the modules' native runtime holds deterministically
    /// (NXT-05 — without this the process-wide registry was never disposed and <c>Pa_Terminate</c>/NDI release
    /// never ran). Idempotent and thread-safe; call <em>after</em> sessions/engines that borrow the registry have
    /// been torn down. A subsequent <see cref="Registry"/> access would rebuild a fresh host, so only call this on
    /// the way out.
    /// </summary>
    public static void Shutdown()
    {
        MediaHost? host;
        lock (Gate)
        {
            host = _host;
            _host = null;
        }

        if (host is null)
            return;
        try
        {
            host.Dispose();
            Trace.LogInformation("MediaRuntime shut down — module native runtimes released.");
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "MediaRuntime: host disposal during shutdown");
        }
    }

    private static MediaHost Build()
    {
        var host = MediaHost.Build(b =>
        {
            TryUse(b, static () => new FFmpegModule(), "FFmpeg");
            TryUse(b, static () => new PortAudioModule(), "PortAudio");
            TryUse(b, static () => new MiniAudioModule(), "MiniAudio");
            // NDI is frequently absent (no runtime / unsupported CPU). Only attempt it when the probe says so,
            // and still guard the Use in case discovery state changed since the probe.
            if (RuntimeModules.IsNdiAvailable)
                TryUse(b, static () => new NDIModule(), "NDI");
            else
                Trace.LogInformation("MediaRuntime: NDI module skipped — {Reason}", RuntimeModules.NdiUnavailableReason);
        });

        Trace.LogInformation("MediaRuntime ready — audio backends: {Backends}",
            string.Join(", ", host.Registry.AudioBackends.Select(x => x.Name)));
        return host;
    }

    private static void TryUse(IMediaRegistryBuilder builder, Func<IMediaModule> factory, string name)
    {
        try
        {
            builder.Use(factory());
        }
        catch (Exception ex)
        {
            // A module's Register runs synchronously inside Build's configure callback, so a throw here just
            // skips that one module — the others still register.
            Trace.LogWarning(ex, "MediaRuntime: '{Module}' module unavailable — continuing without it", name);
        }
    }

    /// <summary>Looks up a registered audio backend by name (the new-framework replacement for AudioBackends.TryGet).</summary>
    public static bool TryGetAudioBackend(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IAudioBackend? backend)
    {
        backend = Registry.AudioBackends.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return backend is not null;
    }
}
