using System.Runtime.InteropServices;
using S.Control;
using S.Media.Compositor;
using S.Media.Core.Registry;

namespace S.Abi;

/// <summary>
/// The dynamic plugin host's product surface (NXT-09): scans one directory for native plugin libraries,
/// loads every library that exports <c>mfp_plugin_register</c> through <see cref="AbiPluginHost"/>, and
/// registers their capabilities into the app's registries. Loading is fail-soft per file - a library that
/// is not a plugin (no register export) is skipped, and one that fails to load/register is recorded in
/// <see cref="Failures"/> without stopping the scan - so a bad plugin degrades exactly like an unavailable
/// module, never blocking startup.
///
/// <para><b>Ownership:</b> the directory owns its <see cref="AbiLoadedPlugin"/>s. Dispose it at app
/// shutdown AFTER the registries built from it - adapters hold reference-counted plugin leases, so a
/// library only truly unloads once every adapter created from it has been disposed (a still-referenced
/// library simply stays loaded until process exit rather than risking an unload-while-referenced crash).</para>
/// </summary>
public sealed class MediaPluginDirectory : IDisposable
{
    private readonly List<AbiLoadedPlugin> _plugins;
    private bool _disposed;

    private MediaPluginDirectory(List<AbiLoadedPlugin> plugins, IReadOnlyList<(string Path, string Error)> failures, IReadOnlyList<string> skipped)
    {
        _plugins = plugins;
        Failures = failures;
        Skipped = skipped;
    }

    /// <summary>The successfully loaded plugins, in load order.</summary>
    public IReadOnlyList<AbiLoadedPlugin> Plugins => _plugins;

    /// <summary>Libraries that LOOKED like plugins but failed to load or register (path + reason).</summary>
    public IReadOnlyList<(string Path, string Error)> Failures { get; }

    /// <summary>Native libraries in the directory that are not plugins (no <c>mfp_plugin_register</c> export)
    /// - dependency libraries shipped next to a plugin land here, silently.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>The platform's native-library extension used by the scan.</summary>
    public static string NativeLibraryExtension =>
        OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

    /// <summary>
    /// Scans <paramref name="directory"/> (non-recursive) and loads every plugin in it. A missing directory
    /// yields an empty host (the "no plugins installed" default) rather than an error.
    /// </summary>
    public static MediaPluginDirectory Load(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var plugins = new List<AbiLoadedPlugin>();
        var failures = new List<(string, string)>();
        var skipped = new List<string>();

        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*" + NativeLibraryExtension).Order(StringComparer.Ordinal))
            {
                // Probe for the register export first so a plain dependency library is a silent skip,
                // not a failure. Load errors (bad image, missing transitive deps) are failures.
                nint probe = 0;
                try
                {
                    probe = NativeLibrary.Load(path);
                    if (!NativeLibrary.TryGetExport(probe, "mfp_plugin_register", out _))
                    {
                        skipped.Add(path);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add((path, ex.Message));
                    continue;
                }
                finally
                {
                    // AbiPluginHost.Load re-loads by path (refcounted by the OS loader); release the probe
                    // handle so the plugin's unload isn't pinned by it.
                    if (probe != 0)
                        NativeLibrary.Free(probe);
                }

                try
                {
                    plugins.Add(AbiPluginHost.Load(path));
                }
                catch (Exception ex)
                {
                    failures.Add((path, ex.Message));
                }
            }
        }

        return new MediaPluginDirectory(plugins, failures, skipped);
    }

    /// <summary>
    /// Registers every loaded plugin's capabilities into the given registries (each optional - pass what
    /// the app actually composes). Call inside the registry's build/configure phase.
    /// </summary>
    public void RegisterInto(
        IMediaRegistryBuilder? media = null,
        ControlMeterBlobDecoderRegistry? control = null,
        ICompositorRegistryBuilder? compositor = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var plugin in _plugins)
            AbiPluginHost.RegisterInto(plugin, media, control, compositor);
    }

    /// <summary>Requests unload of every plugin (reverse load order). Libraries with outstanding adapter
    /// leases stay loaded until those adapters dispose - never an unload-while-referenced.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var i = _plugins.Count - 1; i >= 0; i--)
        {
            try
            {
                _plugins[i].Dispose();
            }
            catch
            {
                // best-effort teardown - one plugin's failing unload must not block the rest
            }
        }
    }
}
