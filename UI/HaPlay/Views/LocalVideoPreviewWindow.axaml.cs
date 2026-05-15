using Avalonia.Controls;
using S.Media.Avalonia;

namespace HaPlay.Views;

public sealed partial class LocalVideoPreviewWindow : Window
{
    public LocalVideoPreviewWindow()
    {
        InitializeComponent();
        Video = new VideoOpenGlControl();
        VideoHostContainer.Content = Video;
    }

    public VideoOpenGlControl Video { get; }
}
