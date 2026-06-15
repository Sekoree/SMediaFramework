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
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Controls;

/// <summary>
/// Visual editor for a composition's multi-output layout. Draws the composition canvas (aspect-correct) with
/// each bound physical output as a draggable, resizable box positioned from its normalized <em>source
/// slice</em> (the part of the canvas that output displays). Dragging the body moves it; the bottom-right
/// handle resizes it; clicking selects it. Overlapping boxes are blend zones (their translucent fills add up);
/// canvas not covered by any box is a gap (the dark background shows through). All state lives on the bound
/// <see cref="OutputLayoutItemViewModel"/>s — this control only edits their Src rectangle.
/// </summary>
public sealed class OutputLayoutCanvas : Control
{
    private const double Pad = 8;
    private const double HandleSize = 12;
    private const double MinNorm = 0.02;

    // Distinct, theme-readable hues keyed by OutputLayoutItemViewModel.ColorIndex (wraps).
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4F, 0x9C, 0xFF), Color.FromRgb(0x57, 0xC7, 0x85), Color.FromRgb(0xFF, 0xB3, 0x4D),
        Color.FromRgb(0xE0, 0x6C, 0x9C), Color.FromRgb(0x9B, 0x8C, 0xFF), Color.FromRgb(0x52, 0xC4, 0xC9),
        Color.FromRgb(0xD9, 0x6A, 0x6A), Color.FromRgb(0xA9, 0xC4, 0x4F),
    ];

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<OutputLayoutItemViewModel?> SelectedItemProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, OutputLayoutItemViewModel?>(
            nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Canvas aspect ratio (width / height). Defaults to 16:9.</summary>
    public static readonly StyledProperty<double> AspectRatioProperty =
        AvaloniaProperty.Register<OutputLayoutCanvas, double>(nameof(AspectRatio), 16.0 / 9.0);

    private readonly List<OutputLayoutItemViewModel> _watched = new();
    private OutputLayoutItemViewModel? _drag;
    private bool _resizing;
    private Point _grabNorm; // pointer offset within the box, normalized, at drag start

    static OutputLayoutCanvas()
    {
        AffectsRender<OutputLayoutCanvas>(SelectedItemProperty, AspectRatioProperty);
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public OutputLayoutItemViewModel? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public double AspectRatio
    {
        get => GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldCol)
                oldCol.CollectionChanged -= OnItemsCollectionChanged;
            if (change.NewValue is INotifyCollectionChanged newCol)
                newCol.CollectionChanged += OnItemsCollectionChanged;
            ResubscribeItems();
            InvalidateVisual();
        }
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResubscribeItems();
        InvalidateVisual();
    }

    private void ResubscribeItems()
    {
        foreach (var i in _watched)
            i.PropertyChanged -= OnItemPropertyChanged;
        _watched.Clear();
        if (Items is not null)
        {
            foreach (var i in Items.OfType<OutputLayoutItemViewModel>())
            {
                i.PropertyChanged += OnItemPropertyChanged;
                _watched.Add(i);
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

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

    private static Rect BoxRect(Rect canvas, OutputLayoutItemViewModel i) => new(
        canvas.X + i.SrcX * canvas.Width,
        canvas.Y + i.SrcY * canvas.Height,
        Math.Max(1, i.SrcWidth * canvas.Width),
        Math.Max(1, i.SrcHeight * canvas.Height));

    private static Color ColorFor(OutputLayoutItemViewModel i) =>
        Palette[((i.ColorIndex % Palette.Length) + Palette.Length) % Palette.Length];

    public override void Render(DrawingContext ctx)
    {
        var canvas = CanvasRect();
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 18)), canvas);
        ctx.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0x80, 0x80, 0x80))), canvas);

        if (Items is null) return;
        foreach (var i in Items.OfType<OutputLayoutItemViewModel>())
        {
            var box = BoxRect(canvas, i);
            var selected = ReferenceEquals(i, SelectedItem);
            var hue = ColorFor(i);
            // Translucent fill so overlapping outputs visibly blend; opaque-ish border for the edge.
            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(selected ? (byte)0x66 : (byte)0x3A, hue.R, hue.G, hue.B)), box);
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(hue), selected ? 2 : 1), box);

            var label = i.DisplayName;
            if (!string.IsNullOrEmpty(label))
            {
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    Typeface.Default, 12, Brushes.White);
                if (box.Width > text.Width + 6 && box.Height > text.Height + 4)
                    ctx.DrawText(text, new Point(box.X + 4, box.Y + 3));
            }

            if (selected)
            {
                var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
                ctx.FillRectangle(new SolidColorBrush(hue), handle);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var canvas = CanvasRect();
        if (canvas.Width <= 0 || Items is null) return;
        var pt = e.GetPosition(this);

        // Last item drawn is visually on top; hit-test in reverse so the topmost wins.
        foreach (var i in Items.OfType<OutputLayoutItemViewModel>().Reverse())
        {
            var box = BoxRect(canvas, i);
            var handle = new Rect(box.Right - HandleSize, box.Bottom - HandleSize, HandleSize, HandleSize);
            var onHandle = ReferenceEquals(i, SelectedItem) && handle.Contains(pt);
            if (onHandle || box.Contains(pt))
            {
                SelectedItem = i;
                _drag = i;
                _resizing = onHandle;
                _grabNorm = new Point((pt.X - box.X) / canvas.Width, (pt.Y - box.Y) / canvas.Height);
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
            var w = Math.Clamp(nx - _drag.SrcX, MinNorm, 1.0 - _drag.SrcX);
            var h = Math.Clamp(ny - _drag.SrcY, MinNorm, 1.0 - _drag.SrcY);
            _drag.SetSrcRect(_drag.SrcX, _drag.SrcY, w, h);
        }
        else
        {
            _drag.SetSrcRect(nx - _grabNorm.X, ny - _grabNorm.Y, _drag.SrcWidth, _drag.SrcHeight);
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
