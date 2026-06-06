using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HaPlay.ViewModels;

namespace HaPlay.Views.Controls;

/// <summary>
/// Visual editor for a cue's video placements. Draws the composition canvas (aspect-correct) with each
/// placement as a draggable, resizable box positioned from its normalized destination rectangle. Dragging
/// the body moves it; the bottom-right handle resizes it; clicking selects it. All state lives on the
/// bound <see cref="CueVideoPlacementViewModel"/>s — this control only edits their DestX/Y/Width/Height.
/// </summary>
public sealed class CompositionPlacementCanvas : Control
{
    private const double Pad = 8;
    private const double HandleSize = 12;

    public static readonly StyledProperty<IEnumerable?> PlacementsProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, IEnumerable?>(nameof(Placements));

    public static readonly StyledProperty<CueVideoPlacementViewModel?> SelectedPlacementProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, CueVideoPlacementViewModel?>(
            nameof(SelectedPlacement), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Canvas aspect ratio (width / height). Defaults to 16:9.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<CompositionPlacementCanvas, double>(nameof(AspectRatio), 16.0 / 9.0);

    private readonly List<CueVideoPlacementViewModel> _watched = new();
    private CueVideoPlacementViewModel? _drag;
    private bool _resizing;
    private Point _dragGrabNorm; // pointer offset within the box, normalized, at drag start
    private double _dragAspect = 1; // normalized DestWidth/DestHeight captured at resize start (aspect lock)

    static CompositionPlacementCanvas()
    {
        AffectsRender<CompositionPlacementCanvas>(SelectedPlacementProperty, AspectRatioProperty);
    }

    public IEnumerable? Placements
    {
        get => GetValue(PlacementsProperty);
        set => SetValue(PlacementsProperty, value);
    }

    public CueVideoPlacementViewModel? SelectedPlacement
    {
        get => GetValue(SelectedPlacementProperty);
        set => SetValue(SelectedPlacementProperty, value);
    }

    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlacementsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCol)
                oldCol.CollectionChanged -= OnPlacementsCollectionChanged;
            if (change.NewValue is INotifyCollectionChanged newCol)
                newCol.CollectionChanged += OnPlacementsCollectionChanged;
            ResubscribeItems();
            InvalidateVisual();
        }
    }

    private void OnPlacementsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResubscribeItems();
        InvalidateVisual();
    }

    private void ResubscribeItems()
    {
        foreach (var p in _watched)
            p.PropertyChanged -= OnPlacementPropertyChanged;
        _watched.Clear();
        if (Placements is not null)
        {
            foreach (var p in Placements.OfType<CueVideoPlacementViewModel>())
            {
                p.PropertyChanged += OnPlacementPropertyChanged;
                _watched.Add(p);
            }
        }
    }

    private void OnPlacementPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private Rect CanvasRect()
    {
        var aspect = AspectRatio <= 0 ? 16.0 / 9.0 : AspectRatio;
        var availW = Math.Max(1, Bounds.Width - Pad * 2);
        var availH = Math.Max(1, Bounds.Height - Pad * 2);
        var cw = Math.Min(availW, availH * aspect);
        var ch = cw / aspect;
        if (ch > availH) { ch = availH; cw = ch * aspect; }
        var cx = (Bounds.Width - cw) / 2;
        var cy = (Bounds.Height - ch) / 2;
        return new Rect(cx, cy, cw, ch);
    }

    private static Rect BoxRect(Rect canvas, CueVideoPlacementViewModel p) => new(
        canvas.X + p.DestX * canvas.Width,
        canvas.Y + p.DestY * canvas.Height,
        Math.Max(1, p.DestWidth * canvas.Width),
        Math.Max(1, p.DestHeight * canvas.Height));

    public override void Render(DrawingContext ctx)
    {
        var canvas = CanvasRect();
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 18)), canvas);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80))), canvas);

        if (Placements is null) return;
        foreach (var p in Placements.OfType<CueVideoPlacementViewModel>().OrderBy(p => p.LayerIndex))
        {
            var box = BoxRect(canvas, p);
            var selected = ReferenceEquals(p, SelectedPlacement);
            var fill = selected
                ? new SolidColorBrush(Color.FromArgb(0x44, 0x4F, 0x9C, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x22, 0xC0, 0xC0, 0xC0));
            var stroke = selected
                ? new Pen(new SolidColorBrush(Color.FromRgb(0x4F, 0x9C, 0xFF)), 2)
                : new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0xC0, 0xC0, 0xC0)), 1);
            ctx.FillRectangle(fill, box);
            ctx.DrawRectangle(null, stroke, box);

            var label = $"L{p.LayerIndex.ToString(CultureInfo.InvariantCulture)}";
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, Brushes.White);
            if (box.Width > text.Width + 6 && box.Height > text.Height + 4)
                ctx.DrawText(text, new Point(box.X + 4, box.Y + 3));

            if (selected)
            {
                var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
                ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x4F, 0x9C, 0xFF)), handle);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var canvas = CanvasRect();
        if (canvas.Width <= 0 || Placements is null) return;
        var pt = e.GetPosition(this);

        // Topmost (highest layer index) wins for both resize-handle and body hits.
        foreach (var p in Placements.OfType<CueVideoPlacementViewModel>().OrderByDescending(p => p.LayerIndex))
        {
            var box = BoxRect(canvas, p);
            var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
            var onHandle = ReferenceEquals(p, SelectedPlacement) && handle.Contains(pt);
            if (onHandle || box.Contains(pt))
            {
                SelectedPlacement = p;
                _drag = p;
                _resizing = onHandle;
                _dragAspect = p.DestHeight > 0 ? p.DestWidth / p.DestHeight : 1;
                _dragGrabNorm = new Point(
                    (pt.X - box.X) / canvas.Width,
                    (pt.Y - box.Y) / canvas.Height);
                e.Pointer.Capture(this);
                e.Handled = true;
                InvalidateVisual();
                return;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null) return;
        var canvas = CanvasRect();
        if (canvas.Width <= 0 || canvas.Height <= 0) return;
        var pt = e.GetPosition(this);
        var nx = (pt.X - canvas.X) / canvas.Width;
        var ny = (pt.Y - canvas.Y) / canvas.Height;

        if (_resizing)
        {
            var w = Math.Clamp(nx - _drag.DestX, 0.02, 1.0 - _drag.DestX);
            var h = Math.Clamp(ny - _drag.DestY, 0.02, 1.0 - _drag.DestY);

            // Aspect-locked by default: keep the box's start-of-drag proportions (so a source-sized
            // box stays at the video's aspect). Hold Shift to resize width/height freely.
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _dragAspect > 0)
            {
                h = w / _dragAspect;
                if (_drag.DestY + h > 1.0) { h = 1.0 - _drag.DestY; w = h * _dragAspect; }
                if (w < 0.02) { w = 0.02; h = w / _dragAspect; }
                if (h < 0.02) { h = 0.02; w = h * _dragAspect; }
                w = Math.Min(w, 1.0 - _drag.DestX);
            }
            _drag.SetDestRect(_drag.DestX, _drag.DestY, w, h);
        }
        else
        {
            var x = nx - _dragGrabNorm.X;
            var y = ny - _dragGrabNorm.Y;
            _drag.SetDestRect(x, y, _drag.DestWidth, _drag.DestHeight);
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not null)
        {
            _drag = null;
            _resizing = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }
}
