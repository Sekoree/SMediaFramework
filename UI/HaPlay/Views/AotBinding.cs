using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;

namespace HaPlay.Views;

/// <summary>
/// NativeAOT-safe code-behind bindings. Avalonia's string-path <c>Binding</c> is a reflection binding
/// (<c>RequiresDynamicCode</c>) that cannot run under NativeAOT, so these wire an Avalonia property to an
/// <see cref="INotifyPropertyChanged"/> source by hand using only public APIs
/// (<see cref="AvaloniaObject.PropertyChanged"/> + <see cref="INotifyPropertyChanged.PropertyChanged"/>).
/// No reflection, no dynamic code. Subscriptions are released on <c>DetachedFromVisualTree</c>.
/// </summary>
internal static class AotBinding
{
    /// <summary>
    /// Two-way bind <paramref name="target"/> on <paramref name="control"/> to a fixed
    /// <paramref name="source"/> object's property. <paramref name="get"/>/<paramref name="set"/> convert
    /// to/from the Avalonia property's value type (e.g. <c>double</c> ↔ <c>decimal?</c>).
    /// </summary>
    public static void TwoWay<TSource>(
        Control control,
        AvaloniaProperty target,
        TSource source,
        string propertyName,
        Func<TSource, object?> get,
        Action<TSource, object?> set)
        where TSource : class
    {
        var syncing = false;

        void PushToControl()
        {
            if (syncing) return;
            syncing = true;
            try { control.SetValue(target, get(source)); }
            finally { syncing = false; }
        }

        void OnSourceChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == propertyName || string.IsNullOrEmpty(e.PropertyName))
                PushToControl();
        }

        void OnControlChanged(object? _, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != target || syncing) return;
            syncing = true;
            try { set(source, e.NewValue); }
            finally { syncing = false; }
        }

        PushToControl(); // seed the control before listening so we don't echo a default back to the source
        // Observe source -> control only when the source raises change notifications. Sources without
        // INotifyPropertyChanged are only seeded + driven control -> source (matches the old binding).
        var inpc = source as INotifyPropertyChanged;
        if (inpc is not null) inpc.PropertyChanged += OnSourceChanged;
        control.PropertyChanged += OnControlChanged;
        control.DetachedFromVisualTree += (_, _) =>
        {
            if (inpc is not null) inpc.PropertyChanged -= OnSourceChanged;
            control.PropertyChanged -= OnControlChanged;
        };
    }

    /// <summary>
    /// One-way bind a <see cref="TextBlock"/>'s text to a property of its (possibly recycled)
    /// <c>DataContext</c>. Re-resolves the source on every <c>DataContextChanged</c>, so it is safe for
    /// virtualized/recycled grid cells.
    /// </summary>
    public static void OneWayText<TSource>(TextBlock control, string propertyName, Func<TSource, string?> get)
        where TSource : class, INotifyPropertyChanged
    {
        TSource? source = null;

        void Update() => control.Text = source is null ? null : get(source);

        void OnSourceChanged(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == propertyName || string.IsNullOrEmpty(e.PropertyName))
                Update();
        }

        void Rebind()
        {
            if (source is not null) source.PropertyChanged -= OnSourceChanged;
            source = control.DataContext as TSource;
            if (source is not null) source.PropertyChanged += OnSourceChanged;
            Update();
        }

        control.DataContextChanged += (_, _) => Rebind();
        control.DetachedFromVisualTree += (_, _) =>
        {
            if (source is not null) source.PropertyChanged -= OnSourceChanged;
            source = null;
        };
        Rebind();
    }
}
