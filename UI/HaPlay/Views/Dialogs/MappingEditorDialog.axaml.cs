using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using HaPlay.Models;
using HaPlay.ViewModels.Dialogs;
using S.Media.Compositor;

namespace HaPlay.Views.Dialogs;

/// <summary>
/// Mapping editor: the preview canvas draws every section's destination rect to scale (rotation
/// included) and supports drag-to-move; everything else is plain numeric binding. The canvas is
/// re-rendered from the VM on any mapping change - except while a drag is in flight, where only
/// the dragged visual moves (a rebuild would steal pointer capture mid-drag).
/// The selected section's mesh warp (when enabled) renders as an overlay: the warped grid as
/// polylines (sampled from the same Catmull-Rom surface the GL pass tessellates) plus one drag
/// handle per control point.
/// </summary>
public partial class MappingEditorDialog : Window
{
    private const int MeshOverlaySamplesPerCell = 8;

    private MappingEditorViewModel? _vm;
    private MappingSectionViewModel? _dragSection;
    private Border? _dragVisual;
    private Point _dragStart;
    private (double X, double Y) _dragOrigin;
    private double _scale = 1;
    private int _dragMeshIndex = -1;
    private Border? _dragMeshVisual;
    private readonly List<Polyline> _meshLines = new();
    private readonly List<Border> _meshHandles = new();

    public MappingEditorDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
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
        if (_dragSection is null && _dragMeshVisual is null)
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
        _meshLines.Clear();
        _meshHandles.Clear();

        foreach (var section in _vm.Sections)
            PreviewCanvas.Children.Add(BuildSectionVisual(section));

        if (_vm.SelectedSection is { MeshEnabled: true } selected)
            BuildMeshOverlay(selected);
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
        if (section.MeshEnabled && ReferenceEquals(_vm?.SelectedSection, section))
            UpdateMeshOverlay(section);
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

    // --- Mesh warp overlay (selected section only) ---

    private void BuildMeshOverlay(MappingSectionViewModel section)
    {
        var lineCount = section.MeshRows + section.MeshColumns;
        for (var i = 0; i < lineCount; i++)
        {
            var line = new Polyline
            {
                Stroke = Brushes.Orange,
                StrokeThickness = 1,
                Opacity = 0.55,
                IsHitTestVisible = false,
            };
            _meshLines.Add(line);
            PreviewCanvas.Children.Add(line);
        }

        for (var i = 0; i < section.MeshPoints.Count; i++)
        {
            var handle = new Border
            {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(5.5),
                Background = Brushes.Orange,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                Tag = i,
                Cursor = new Cursor(StandardCursorType.SizeAll),
            };
            handle.PointerPressed += OnMeshHandlePressed;
            handle.PointerMoved += OnMeshHandleMoved;
            handle.PointerReleased += OnMeshHandleReleased;
            _meshHandles.Add(handle);
            PreviewCanvas.Children.Add(handle);
        }

        UpdateMeshOverlay(section);
    }

    /// <summary>Repositions the grid polylines and handles from the section's current state -
    /// cheap enough to run per drag tick (a few hundred surface evaluations).</summary>
    private void UpdateMeshOverlay(MappingSectionViewModel section)
    {
        if (_meshHandles.Count != section.MeshPoints.Count
            || _meshLines.Count != section.MeshRows + section.MeshColumns)
            return; // shape changed since the overlay was built - the pending re-render rebuilds it

        var mesh = BuildPreviewSpaceMesh(section);
        if (mesh is null)
            return;

        var line = 0;
        for (var r = 0; r < section.MeshRows; r++, line++)
            _meshLines[line].Points = SampleMeshLine(mesh, section.MeshColumns, horizontal: true, r / (double)(section.MeshRows - 1));
        for (var c = 0; c < section.MeshColumns; c++, line++)
            _meshLines[line].Points = SampleMeshLine(mesh, section.MeshRows, horizontal: false, c / (double)(section.MeshColumns - 1));

        for (var i = 0; i < _meshHandles.Count; i++)
        {
            var p = mesh.Points[i];
            Canvas.SetLeft(_meshHandles[i], p.X - _meshHandles[i].Width / 2);
            Canvas.SetTop(_meshHandles[i], p.Y - _meshHandles[i].Height / 2);
        }
    }

    /// <summary>The section's mesh with control points in preview-canvas coordinates (output pixels
    /// × scale, dest-rect placement and rotation applied) - lets the overlay sample the exact
    /// surface the GL pass renders.</summary>
    private WarpMesh? BuildPreviewSpaceMesh(MappingSectionViewModel section)
    {
        var points = section.MeshPoints;
        if (points.Count != section.MeshColumns * section.MeshRows
            || section.MeshColumns < 2 || section.MeshRows < 2)
            return null;

        var absolute = new System.Numerics.Vector2[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            var (x, y) = MeshPointToCanvas(section, points[i]);
            absolute[i] = new System.Numerics.Vector2((float)x, (float)y);
        }

        return new WarpMesh(section.MeshColumns, section.MeshRows, absolute);
    }

    private static Points SampleMeshLine(WarpMesh mesh, int controlPointsAlong, bool horizontal, double fixedParam)
    {
        var samples = (controlPointsAlong - 1) * MeshOverlaySamplesPerCell;
        var points = new Points();
        for (var i = 0; i <= samples; i++)
        {
            var t = (float)i / samples;
            var p = horizontal
                ? WarpMeshTessellator.Evaluate(mesh, t, (float)fixedParam)
                : WarpMeshTessellator.Evaluate(mesh, (float)fixedParam, t);
            points.Add(new Point(p.X, p.Y));
        }

        return points;
    }

    /// <summary>Normalized dest-rect point → preview-canvas coordinates (same placement math as
    /// <c>OutputMappingResolver.TryResolveMesh</c>: unnormalize over the dest rect, rotate about
    /// its center, then scale to the preview).</summary>
    private (double X, double Y) MeshPointToCanvas(MappingSectionViewModel section, CuePoint p)
    {
        var (w, h) = SectionDestSize(section);
        var cx = section.DestX + w / 2;
        var cy = section.DestY + h / 2;
        var rad = section.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = section.DestX + p.X * w - cx;
        var dy = section.DestY + p.Y * h - cy;
        return ((cx + dx * cos - dy * sin) * _scale, (cy + dx * sin + dy * cos) * _scale);
    }

    /// <summary>Inverse of <see cref="MeshPointToCanvas"/> for handle drags.</summary>
    private (double U, double V) CanvasToMeshPoint(MappingSectionViewModel section, Point canvasPos)
    {
        var (w, h) = SectionDestSize(section);
        var cx = section.DestX + w / 2;
        var cy = section.DestY + h / 2;
        var rad = -section.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);
        var dx = canvasPos.X / _scale - cx;
        var dy = canvasPos.Y / _scale - cy;
        var x = cx + dx * cos - dy * sin;
        var y = cy + dx * sin + dy * cos;
        return ((x - section.DestX) / w, (y - section.DestY) / h);
    }

    private void OnMeshHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: int index } visual || _vm?.SelectedSection is not { MeshEnabled: true })
            return;

        _dragMeshIndex = index;
        _dragMeshVisual = visual;
        e.Pointer.Capture(visual);
        e.Handled = true;
    }

    private void OnMeshHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragMeshVisual is not { } visual || _dragMeshIndex < 0 || _scale <= 0)
            return;
        if (!ReferenceEquals(e.Pointer.Captured, visual))
            return;
        if (_vm?.SelectedSection is not { MeshEnabled: true } section)
            return;

        var (u, v) = CanvasToMeshPoint(section, e.GetPosition(PreviewCanvas));
        section.SetMeshPoint(_dragMeshIndex, u, v);
        UpdateMeshOverlay(section);
        e.Handled = true;
    }

    private void OnMeshHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragMeshVisual is null)
            return;
        e.Pointer.Capture(null);
        _dragMeshIndex = -1;
        _dragMeshVisual = null;
        RenderPreview();
        e.Handled = true;
    }
}
