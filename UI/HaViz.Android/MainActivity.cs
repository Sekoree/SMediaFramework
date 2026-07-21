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
    LaunchMode = LaunchMode.SingleInstance,
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
}
