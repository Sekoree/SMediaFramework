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
/// The process-wide media registry - the rewritten framework's single composition root. Replaces the old
/// static <c>MediaFrameworkRuntime.Init().UseFFmpeg()/UsePortAudio()/…</c> + <c>AudioBackends</c> +
/// <c>MediaFrameworkPlugins</c> surface (all removed in the AOT-pure rewrite, P2). Built once at startup;
/// engines open media via <c>MediaPlayer.OpenFile(MediaRuntime.Registry, path)</c> and resolve output devices
/// from <see cref="IMediaRegistry.AudioBackends"/>. Module registration is best-effort per module so an
/// unavailable backend (e.g. NDI on a host without the runtime) degrades rather than blocking startup.
/// </summary>
internal static class MediaRuntime
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.MediaRuntime");
    private static readonly object Gate = new();
    private static readonly List<ModuleDiagnostic> _moduleDiagnostics = [];
    private static MediaHost? _host;
    private static S.Abi.MediaPluginDirectory? _plugins;
    private static S.Media.Compositor.ICompositorRegistry _compositorSurfaces =
        new S.Media.Compositor.CompositorRegistryBuilder().Build();
    private static bool _shutdown;

    /// <summary>The plugins directory (NXT-09): <c>HAPLAY_PLUGINS_DIR</c>, else <c>&lt;app&gt;/plugins</c>.
    /// Native libraries exporting <c>mfp_plugin_register</c> found there register their capabilities into the
    /// media registry (decoders, audio backends) and <see cref="CompositorSurfaces"/> (layer-surface kinds).</summary>
    internal static string PluginsDirectory =>
        Environment.GetEnvironmentVariable("HAPLAY_PLUGINS_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>Layer-surface kinds contributed by native plugins (NXT-09/10) - resolve by kind + config
    /// JSON to place a plugin-rendered GPU layer on a composition. Populated when the host builds.</summary>
    public static S.Media.Compositor.ICompositorRegistry CompositorSurfaces
    {
        get
        {
            _ = Host; // surface kinds register during the host build
            return _compositorSurfaces;
        }
    }

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
            {
                if (_host is null && _shutdown)
                {
                    // A late poll/timer touched the registry AFTER Shutdown() released the native runtimes -
                    // rebuilding resurrects PortAudio/NDI holds that will now leak (nothing disposes the fresh
                    // host). Rebuild anyway so a straggling teardown path can't crash the exit, but make the
                    // ordering bug loud: fix the caller to stop before MediaRuntime.Shutdown().
                    Trace.LogError("MediaRuntime: Registry accessed AFTER Shutdown() - rebuilding a fresh host "
                                   + "(native runtime holds will leak). Stop the caller before shutdown.");
                    System.Diagnostics.Debug.Assert(false, "MediaRuntime.Registry accessed after Shutdown()");
                }

                return _host ??= Build();
            }
        }
    }

    /// <summary>The built registry. Lazily built on first access (idempotent + thread-safe), so anything that
    /// resolves backends/decoders works whether or not <see cref="Initialize"/> ran first (tests, dialogs).</summary>
    public static IMediaRegistry Registry => Host.Registry;

    public static bool IsInitialized => _host is not null;

    /// <summary>AUDIO-02: per-module availability captured during the registry build, so HaPlay can SHOW which
    /// backends registered and why an optional one (e.g. NDI, PortAudio) was skipped - instead of that only
    /// living in a log line. Building the host first ensures the list is populated.</summary>
    public static IReadOnlyList<ModuleDiagnostic> ModuleDiagnostics
    {
        get
        {
            _ = Host;
            lock (Gate)
                return [.. _moduleDiagnostics];
        }
    }

    /// <summary>Eagerly builds the registry at startup (for deterministic timing/logging). Idempotent.</summary>
    public static void Initialize() => _ = Host;

    /// <summary>
    /// Disposes the owning host at app shutdown, releasing the modules' native runtime holds deterministically
    /// (NXT-05 - without this the process-wide registry was never disposed and <c>Pa_Terminate</c>/NDI release
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
            _shutdown = true; // a later Registry access is an ordering bug - the getter asserts + logs it
        }

        if (host is null)
            return;
        try
        {
            host.Dispose();
            Trace.LogInformation("MediaRuntime shut down - module native runtimes released.");
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "MediaRuntime: host disposal during shutdown");
        }

        // AFTER the registry (its plugin-backed adapters hold the unload-gating leases): request plugin
        // unload - a library with still-live adapters simply stays loaded until process exit.
        S.Abi.MediaPluginDirectory? plugins;
        lock (Gate)
        {
            plugins = _plugins;
            _plugins = null;
        }
        plugins?.Dispose();
    }

    private static MediaHost Build()
    {
        // Dynamic native plugins (NXT-09): fail-soft like every module - a broken plugin is a log line,
        // not a startup failure. Loaded before the registry build so capabilities register inside it.
        S.Abi.MediaPluginDirectory? plugins = null;
        try
        {
            plugins = S.Abi.MediaPluginDirectory.Load(PluginsDirectory);
            foreach (var plugin in plugins.Plugins)
                Trace.LogInformation("MediaRuntime: plugin '{Id}' ({Name}) loaded from {Dir}",
                    plugin.Id, plugin.DisplayName, PluginsDirectory);
            foreach (var (path, error) in plugins.Failures)
                Trace.LogWarning("MediaRuntime: plugin '{Path}' failed to load - {Error}", path, error);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "MediaRuntime: plugin directory scan failed - continuing without plugins");
        }

        lock (Gate)
            _moduleDiagnostics.Clear(); // fresh per build (a post-shutdown rebuild re-probes)

        var surfaceBuilder = new S.Media.Compositor.CompositorRegistryBuilder();
        var host = MediaHost.Build(b =>
        {
            TryUse(b, static () => new FFmpegModule(), "FFmpeg");
            TryUse(b, static () => new PortAudioModule(), "PortAudio");
            TryUse(b, static () => new MiniAudioModule(), "MiniAudio");
            // NDI is frequently absent (no runtime / unsupported CPU). Only attempt it when the probe says so,
            // and still guard the Use in case discovery state changed since the probe.
            if (RuntimeModules.IsNDIAvailable)
                TryUse(b, static () => new NDIModule(), "NDI");
            else
            {
                Trace.LogInformation("MediaRuntime: NDI module skipped - {Reason}", RuntimeModules.NDIUnavailableReason);
                RecordDiagnostic(new ModuleDiagnostic("NDI", false, RuntimeModules.NDIUnavailableReason));
            }

            // Text cues (NXT-06 cutover): a `text:` provider so the ShowSession path can play a rendered text cue
            // through the registry like any other source (the old engine rendered it via a held frame directly).
            b.AddDecoder(new S.Media.Source.Text.TextDecoderProvider());

            // YouTube (Gate 5): plays prepared cache assets behind youtube:// URIs. Purely managed +
            // local-file playback, so no native probe is needed; shares the app-wide preparer/cache with
            // the add/edit dialogs (Playback.YouTubeRuntime).
            TryUse(b, static () => Playback.YouTubeRuntime.Module, "YouTube");

            // PMX/VMD scenes behind mmd:// URIs; GL material surface with a managed CPU fallback.
            TryUse(b, static () => new S.Media.Source.MMD.MMDSourceModule(), "MMD");

            // Dynamic plugin capabilities register LAST so a plugin can extend but not silently pre-empt a
            // built-in for the same probe (registry probe scoring still decides the winner per open).
            if (plugins is not null)
            {
                try
                {
                    plugins.RegisterInto(media: b, compositor: surfaceBuilder);
                }
                catch (Exception ex)
                {
                    Trace.LogWarning(ex, "MediaRuntime: plugin capability registration failed - continuing");
                }
            }
        });

        lock (Gate)
        {
            _plugins = plugins;
            _compositorSurfaces = surfaceBuilder.Build();
        }

        Trace.LogInformation("MediaRuntime ready - audio backends: {Backends}",
            string.Join(", ", host.Registry.AudioBackends.Select(x => x.Name)));
        return host;
    }

    private static void TryUse(IMediaRegistryBuilder builder, Func<IMediaModule> factory, string name)
    {
        try
        {
            builder.Use(factory());
            RecordDiagnostic(new ModuleDiagnostic(name, true, null));
        }
        catch (Exception ex)
        {
            // A module's Register runs synchronously inside Build's configure callback, so a throw here just
            // skips that one module - the others still register.
            Trace.LogWarning(ex, "MediaRuntime: '{Module}' module unavailable - continuing without it", name);
            RecordDiagnostic(new ModuleDiagnostic(name, false, DescribeUnavailability(ex)));
        }
    }

    private static void RecordDiagnostic(ModuleDiagnostic diagnostic)
    {
        lock (Gate)
            _moduleDiagnostics.Add(diagnostic);
    }

    /// <summary>Maps a module registration failure to the AUDIO-02 vocabulary (not installed / incompatible /
    /// open failed) so the surfaced reason is meaningful rather than a raw exception dump.</summary>
    private static string DescribeUnavailability(Exception ex) => ex switch
    {
        DllNotFoundException => "native library not installed",
        BadImageFormatException => "native library incompatible (wrong architecture)",
        EntryPointNotFoundException => "native library incompatible (missing entry point)",
        _ => $"failed to initialize ({ex.GetType().Name})",
    };

    /// <summary>Looks up a registered audio backend by name (the new-framework replacement for AudioBackends.TryGet).</summary>
    public static bool TryGetAudioBackend(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IAudioBackend? backend)
    {
        backend = Registry.AudioBackends.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return backend is not null;
    }
}

/// <summary>AUDIO-02: the availability of one media module after the registry build. <paramref name="Detail"/>
/// explains an unavailable one (e.g. "native library not installed").</summary>
public readonly record struct ModuleDiagnostic(string Name, bool Available, string? Detail);
