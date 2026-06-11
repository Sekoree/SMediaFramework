using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Mapping editor: the preview canvas draws every section's destination rect to scale (rotation
/// included) and supports drag-to-move; everything else is plain numeric binding. The canvas is
/// re-rendered from the VM on any mapping change — except while a drag is in flight, where only
/// the dragged visual moves (a rebuild would steal pointer capture mid-drag).
/// </summary>
public partial class MappingEditorDialog : Window
{
    private MappingEditorViewModel? _vm;
    private MappingSectionViewModel? _dragSection;
    private Border? _dragVisual;
    private Point _dragStart;
    private (double X, double Y) _dragOrigin;
    private double _scale = 1;

    public MappingEditorDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachVm();
        PreviewHost.SizeChanged += (_, _) => RenderPreview();
        Closed += (_, _) => _vm?.OnEditorClosed();
    }

    private void AttachVm()
    {
        if (_vm is not null)
        {
            _vm.MappingChanged -= OnMappingChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }

        _vm = DataContext as MappingEditorViewModel;
        if (_vm is not null)
        {
            _vm.MappingChanged += OnMappingChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }

        RenderPreview();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MappingEditorViewModel.SelectedSection))
            RenderPreview();
    }

    private void OnMappingChanged()
    {
        if (_dragSection is null)
            RenderPreview();
    }

    private void RenderPreview()
    {
        if (_vm is null)
            return;

        var outW = Math.Max(16, _vm.EffectiveOutputWidth);
        var outH = Math.Max(16, _vm.EffectiveOutputHeight);
        var avail = PreviewHost.Bounds.Size;
        if (avail.Width < 40 || avail.Height < 40)
            return;

        _scale = Math.Min((avail.Width - 16) / outW, (avail.Height - 16) / outH);
        PreviewCanvas.Width = outW * _scale;
        PreviewCanvas.Height = outH * _scale;
        PreviewCanvas.Children.Clear();

        foreach (var section in _vm.Sections)
            PreviewCanvas.Children.Add(BuildSectionVisual(section));
    }

    private Border BuildSectionVisual(MappingSectionViewModel section)
    {
        var (w, h) = SectionDestSize(section);
        var selected = ReferenceEquals(_vm?.SelectedSection, section);
        var visual = new Border
        {
            Width = Math.Max(4, w * _scale),
            Height = Math.Max(4, h * _scale),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? Brushes.Orange : Brushes.SteelBlue,
            Background = new SolidColorBrush(Colors.SteelBlue, section.Enabled ? 0.25 : 0.06),
            Tag = section,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new TextBlock
            {
                Text = section.Name,
                FontSize = 11,
                Foreground = Brushes.White,
                Opacity = section.Enabled ? 0.9 : 0.4,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };

        Canvas.SetLeft(visual, section.DestX * _scale);
        Canvas.SetTop(visual, section.DestY * _scale);
        if (section.RotationDegrees != 0)
        {
            visual.RenderTransformOrigin = RelativePoint.Center;
            visual.RenderTransform = new RotateTransform(section.RotationDegrees);
        }

        visual.PointerPressed += OnSectionPointerPressed;
        visual.PointerMoved += OnSectionPointerMoved;
        visual.PointerReleased += OnSectionPointerReleased;
        return visual;
    }

    private (double W, double H) SectionDestSize(MappingSectionViewModel section)
    {
        if (_vm is null)
            return (16, 16);
        // 0 = natural slice size, mirroring OutputMappingResolver.
        var w = section.DestWidth > 0 ? section.DestWidth : section.SrcWidth * _vm.CanvasWidth;
        var h = section.DestHeight > 0 ? section.DestHeight : section.SrcHeight * _vm.CanvasHeight;
        return (w, h);
    }

    private void OnSectionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: MappingSectionViewModel section } visual || _vm is null)
            return;

        _vm.SelectedSection = section;
        _dragSection = section;
        _dragVisual = visual;
        _dragStart = e.GetPosition(PreviewCanvas);
        _dragOrigin = (section.DestX, section.DestY);
        e.Pointer.Capture(visual);
        e.Handled = true;
    }

    private void OnSectionPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSection is not { } section || _dragVisual is not { } visual || _scale <= 0)
            return;
        if (!ReferenceEquals(e.Pointer.Captured, visual))
            return;

        var p = e.GetPosition(PreviewCanvas);
        section.DestX = Math.Round(_dragOrigin.X + (p.X - _dragStart.X) / _scale, 1);
        section.DestY = Math.Round(_dragOrigin.Y + (p.Y - _dragStart.Y) / _scale, 1);
        Canvas.SetLeft(visual, section.DestX * _scale);
        Canvas.SetTop(visual, section.DestY * _scale);
        e.Handled = true;
    }

    private void OnSectionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragSection is null)
            return;
        e.Pointer.Capture(null);
        _dragSection = null;
        _dragVisual = null;
        RenderPreview();
        e.Handled = true;
    }
}
