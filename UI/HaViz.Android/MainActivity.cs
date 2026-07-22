using Android.App;
using Android.Content.PM;
using Android.Views;
using Avalonia.Android;

namespace HaViz.Android;

[Activity(
    Label = "HaViz",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    Exported = true,
    // SingleTask, not SingleInstance: a singleInstance activity can get an immediate
    // RESULT_CANCELED from StartActivityForResult on some devices/OEMs, breaking the SAF folder
    // picker and the MediaProjection consent dialog (MainActivity.Results.cs).
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public partial class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        // Before base: base.OnCreate builds the Avalonia view via the app's MainViewFactory, and
        // that factory resolves PlatformServices.
        PlatformServices.Initialize(this);
        base.OnCreate(savedInstanceState);
        // Dedicated-device ergonomics: the show must not stop because the screen timed out.
        Window?.AddFlags(WindowManagerFlags.KeepScreenOn);
    }

    protected override void OnDestroy()
    {
        // Only on a real finish (recreates get a fresh view model via the MainViewFactory):
        // nothing else ever disposes the view model, and the engine/player/capture service
        // would outlive the UI.
        if (IsFinishing)
        {
            PlatformServices.Instance.MainViewModel?.Dispose();
            PlatformServices.Instance.MainViewModel = null;
        }

        base.OnDestroy();
    }
}
