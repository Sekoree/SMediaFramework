using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HaViz.Android.ViewModels;
using HaViz.Android.Views;

namespace HaViz.Android;

public class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            // Deferred: this runs during Application.OnCreate, before any activity exists; the
            // factory fires from MainActivity's OnCreate, after PlatformServices.Initialize.
            activityLifetime.MainViewFactory = () =>
            {
                // A recreated activity gets a fresh view model; the previous one must release
                // its engine/player/capture first or they leak past the old activity.
                PlatformServices.Instance.MainViewModel?.Dispose();
                var viewModel = new MainViewModel(PlatformServices.Instance);
                PlatformServices.Instance.MainViewModel = viewModel;
                return new MainView { DataContext = viewModel };
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
