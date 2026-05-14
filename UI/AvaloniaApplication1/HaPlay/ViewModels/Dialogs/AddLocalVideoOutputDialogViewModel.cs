using System.Collections.ObjectModel;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using HaPlay.Models;

namespace HaPlay.ViewModels.Dialogs;

public sealed class ScreenListItem
{
    public required int Index { get; init; }
    public required string Label { get; init; }
}

public partial class AddLocalVideoOutputDialogViewModel : ViewModelBase
{
    public VideoOutputEngine[] Engines { get; } = Enum.GetValues<VideoOutputEngine>();
    public VideoSurfaceMode[] SurfaceModes { get; } = Enum.GetValues<VideoSurfaceMode>();

    [ObservableProperty] private string _displayName = "Program output";
    [ObservableProperty] private string? _validationMessage;

    public ObservableCollection<ScreenListItem> Screens { get; } = new();

    [ObservableProperty] private ScreenListItem? _selectedScreen;

    [ObservableProperty] private VideoOutputEngine _engine = VideoOutputEngine.SdlOpenGl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowMetricsEnabled))]
    private VideoSurfaceMode _surfaceMode = VideoSurfaceMode.Windowed;

    public bool WindowMetricsEnabled => SurfaceMode == VideoSurfaceMode.Windowed;

    [ObservableProperty] private int _windowWidth = 1280;
    [ObservableProperty] private int _windowHeight = 720;

    public void InitializeScreens(IReadOnlyList<Screen> screens)
    {
        Screens.Clear();
        for (var i = 0; i < screens.Count; i++)
        {
            var b = screens[i].Bounds;
            Screens.Add(new ScreenListItem
            {
                Index = i,
                Label = $"#{i} — {b.Width:0}×{b.Height:0} @ ({b.X:0}, {b.Y:0})",
            });
        }

        SelectedScreen = Screens.FirstOrDefault();
    }

    public LocalVideoOutputDefinition? TryCommit()
    {
        ValidationMessage = null;
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ValidationMessage = "Display name is required.";
            return null;
        }

        if (SelectedScreen is null)
        {
            ValidationMessage = "Select a display.";
            return null;
        }

        if (SurfaceMode == VideoSurfaceMode.Windowed)
        {
            if (WindowWidth < 320 || WindowHeight < 240)
            {
                ValidationMessage = "Windowed mode needs a reasonable width and height (at least about 320×240).";
                return null;
            }
        }

        return new LocalVideoOutputDefinition(
            Guid.NewGuid(),
            DisplayName.Trim(),
            Engine,
            SurfaceMode,
            SelectedScreen.Index,
            SurfaceMode == VideoSurfaceMode.Windowed ? WindowWidth : null,
            SurfaceMode == VideoSurfaceMode.Windowed ? WindowHeight : null);
    }
}
