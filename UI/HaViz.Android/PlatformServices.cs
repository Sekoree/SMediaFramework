using Android.Content;
using Android.Net.Wifi;
using HaViz.Android.Services;

namespace HaViz.Android;

/// <summary>
/// Activity-scoped platform plumbing handed to the view model: the current activity (SAF pickers
/// and the MediaProjection consent intent need one), the NDI multicast lock, and the on-first-run
/// preset deployment. Initialized from <see cref="MainActivity.OnCreate"/>.
/// </summary>
public sealed class PlatformServices
{
    private WifiManager.MulticastLock? _multicastLock;

    public static PlatformServices Instance { get; private set; } = null!;

    public MainActivity Activity { get; private set; } = null!;

    /// <summary>Logcat-backed logger factory (also installed as MediaDiagnostics.LoggerFactory).</summary>
    public Microsoft.Extensions.Logging.ILoggerFactory? LoggerFactory { get; private set; }

    /// <summary>App-private presets directory (populated from the bundled pack on first run).</summary>
    public string PresetDirectory { get; private set; } = string.Empty;

    public string TextureDirectory { get; private set; } = string.Empty;

    public static void Initialize(MainActivity activity)
    {
        // Explicit resolver installs: the [ModuleInitializer]-based registration was observed NOT
        // to run under Mono on Android before the first P/Invoke, leaving DllImports on the
        // desktop soname. Install() is idempotent, so this is safe belt-and-braces.
        Instance ??= new PlatformServices();
        Instance.LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
            Microsoft.Extensions.Logging.LoggingBuilderExtensions.AddProvider(
                builder, new Platform.LogcatLoggerProvider()));
        S.Media.Core.Diagnostics.MediaDiagnostics.LoggerFactory = Instance.LoggerFactory;
        NDILib.Runtime.NDILibraryResolver.Install();
        ProjectMLib.Runtime.ProjectMLibraryResolver.Install();

        Instance.Activity = activity;
        var (presets, textures) = PresetDeployer.EnsureDeployed(activity);
        Instance.PresetDirectory = presets;
        Instance.TextureDirectory = textures;
    }

    /// <summary>
    /// NDI discovery is mDNS: without a held multicast lock Android's Wi-Fi stack drops the
    /// packets and the source is invisible to receivers. Held while the engine runs.
    /// </summary>
    public void AcquireMulticastLock()
    {
        if (_multicastLock is { IsHeld: true })
            return;
        var wifi = (WifiManager?)Activity.GetSystemService(Context.WifiService);
        _multicastLock = wifi?.CreateMulticastLock("haviz-ndi");
        if (_multicastLock is not null)
        {
            _multicastLock.SetReferenceCounted(false);
            _multicastLock.Acquire();
        }
    }

    public void ReleaseMulticastLock()
    {
        if (_multicastLock is { IsHeld: true })
            _multicastLock.Release();
    }
}
