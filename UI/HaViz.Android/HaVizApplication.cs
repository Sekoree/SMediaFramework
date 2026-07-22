using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace HaViz.Android;

/// <summary>Avalonia 12 Android bootstrap: the app builder lives on the Android Application (one
/// process-wide Avalonia app; activities only host views).</summary>
[Application]
public class HaVizApplication(nint javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<App>(javaReference, transfer)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}
