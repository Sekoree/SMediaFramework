using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HaPlay.ViewModels;

namespace HaPlay.Views.Controls;

/// <summary>
/// Fixed-height, always-present one-line status row (UI rewrite P1, plan §1). The reserved height
/// is the point: persistent state ("playing X", "2 outputs degraded") may change text or severity
/// but never appears/disappears as a layout element, so the controls around it never move.
/// Transient events belong in the toast overlay instead.
/// </summary>
public sealed class StatusLineControl : ContentControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusLineControl, string?>(nameof(Text));

    public static readonly StyledProperty<ToastSeverity> SeverityProperty =
        AvaloniaProperty.Register<StatusLineControl, ToastSeverity>(nameof(Severity));

    // Dark severity shades - the status line sits on the Classic theme's light-gray chrome
    // (must match the StatusInfoFg/StatusWarnFg/StatusErrorFg tokens in Styles/Tokens.axaml).
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#37474F"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#8A5A00"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#B71C1C"));

    private readonly TextBlock _text;

    public StatusLineControl()
    {
        // Reserved height even when Text is empty - never collapses out of layout.
        MinHeight = 22;
        VerticalContentAlignment = VerticalAlignment.Center;
        _text = new TextBlock
        {
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Content = _text;
        UpdateText();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ToastSeverity Severity
    {
        get => GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == SeverityProperty)
            UpdateText();
    }

    private void UpdateText()
    {
        _text.Text = Text;
        _text.Opacity = string.IsNullOrEmpty(Text) ? 0 : Severity == ToastSeverity.Info ? 0.75 : 1.0;
        _text.Foreground = Severity switch
        {
            ToastSeverity.Warning => WarnBrush,
            ToastSeverity.Error => ErrorBrush,
            _ => InfoBrush,
        };
    }
}
