using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml.Templates;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class MediaPlayerView : UserControl
{
    private MediaPlayerViewModel? _matrixSubscribedVm;

    public MediaPlayerView()
    {
        InitializeComponent();
        // PointerReleased on the slider — TwoWay binding tracks live, but we only commit the seek
        // once the user lets go. Avoids decoding 1000 intermediate positions while they drag.
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekSliderPointerReleased,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        SeekSlider.AddHandler(KeyUpEvent, OnSeekSliderKeyUp,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        // Phase C — keyboard-first transport. Tunnel handler so we see keys before the focused child;
        // we still bail on text-editing sources so playlist-tab renames and NumericUpDowns stay typeable.
        AddHandler(KeyDownEvent, OnUserControlKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        DataContextChanged += OnDataContextChanged;
        DragDrop.SetAllowDrop(PlaylistListBox, true);
        PlaylistListBox.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver, RoutingStrategies.Bubble);
        PlaylistListBox.AddHandler(DragDrop.DropEvent, OnPlaylistDrop, RoutingStrategies.Bubble);
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (DataContext is not MediaPlayerViewModel vm)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null || !files.Any())
            return;

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (paths.Count > 0)
            vm.AddDroppedFilesToPlaylist(paths);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_matrixSubscribedVm is not null)
            _matrixSubscribedVm.AudioMatrixLayoutChanged -= OnAudioMatrixLayoutChanged;
        _matrixSubscribedVm = DataContext as MediaPlayerViewModel;
        if (_matrixSubscribedVm is not null)
        {
            _matrixSubscribedVm.AudioMatrixLayoutChanged += OnAudioMatrixLayoutChanged;
            RebuildAudioMatrixSource();
            RebuildAudioRouteSource();
        }
    }

    private void OnAudioMatrixLayoutChanged(object? sender, System.EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RebuildAudioMatrixSource();
            RebuildAudioRouteSource();
        });

    /// <summary>
    /// Phase C (§4.3.4) — build the TreeDataGrid source from <see cref="MediaPlayerViewModel.AudioMatrixRows"/>
    /// and <see cref="MediaPlayerViewModel.AudioMatrixInputChannelCount"/>. Columns are constructed dynamically
    /// — one for the row label, then one NumericUpDown column per input channel.
    /// </summary>
    private void RebuildAudioMatrixSource()
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (MatrixTreeGrid is null) return;

        var inputCount = vm.AudioMatrixInputChannelCount;
        if (vm.AudioMatrixRows.Count == 0 || inputCount <= 0)
        {
            MatrixTreeGrid.Source = null;
            return;
        }

        var source = new Avalonia.Controls.FlatTreeDataGridSource<AudioMatrixRow>(vm.AudioMatrixRows);

        source.Columns.Add(new TextColumn<AudioMatrixRow, string>(
            "Output", x => x.Label, width: new GridLength(220)));

        for (var i = 0; i < inputCount; i++)
        {
            var inputChannel = i;
            var header = inputCount == 2 ? $"In {(inputChannel == 0 ? "L" : "R")}" : $"In {inputChannel + 1}";
            source.Columns.Add(new TemplateColumn<AudioMatrixRow>(
                header,
                new FuncDataTemplate<AudioMatrixRow>((row, _) => BuildCellEditor(row, inputChannel), supportsRecycling: true),
                width: new GridLength(110)));
        }

        MatrixTreeGrid.Source = source;
    }

    /// <summary>
    /// Build the active-route TreeDataGrid source from <see cref="MediaPlayerViewModel.AudioMatrixRouteRows"/>.
    /// This is a second view over the same matrix cells (one row per audible connection), so edits stay
    /// in sync with the matrix grid automatically.
    /// </summary>
    private void RebuildAudioRouteSource()
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (MatrixRoutesTreeGrid is null) return;
        if (vm.AudioMatrixRouteRows.Count == 0)
        {
            MatrixRoutesTreeGrid.Source = null;
            return;
        }

        var source = new Avalonia.Controls.FlatTreeDataGridSource<AudioMatrixRouteRow>(vm.AudioMatrixRouteRows);
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("VOut", x => x.VirtualOutputLabel, width: new GridLength(92)));
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("Output", x => x.OutputLabel, width: new GridLength(240)));
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("Input", x => x.InputLabel, width: new GridLength(88)));
        source.Columns.Add(new TemplateColumn<AudioMatrixRouteRow>(
            "Gain dB",
            new FuncDataTemplate<AudioMatrixRouteRow>((row, _) => BuildRouteGainEditor(row), supportsRecycling: true),
            width: new GridLength(104)));
        source.Columns.Add(new TemplateColumn<AudioMatrixRouteRow>(
            "Mute",
            new FuncDataTemplate<AudioMatrixRouteRow>((row, _) => BuildRouteMuteEditor(row), supportsRecycling: true),
            width: new GridLength(72)));
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("Effective", x => x.EffectiveGainText, width: new GridLength(96)));
        MatrixRoutesTreeGrid.Source = source;
    }

    /// <summary>
    /// Phase C (§4.3.4) — one cell editor: a NumericUpDown for gain dB plus a tiny mute toggle. We can't
    /// rely on data templates with x:Bind here since the cell lookup is dynamic per (row, input channel);
    /// build the visuals once and let Avalonia recycle them per row.
    /// </summary>
    private static Control BuildCellEditor(AudioMatrixRow row, int inputChannel)
    {
        var cell = row.GetCell(inputChannel);
        if (cell is null)
            return new TextBlock { Text = "—", Opacity = 0.45 };

        var spinner = new NumericUpDown
        {
            Minimum = -60,
            Maximum = 12,
            Increment = 1.0M,
            FormatString = "0.#",
            ShowButtonSpinner = false,
            ClipValueToMinMax = true,
            Width = 78,
            Padding = new Avalonia.Thickness(4, 2),
            DataContext = cell,
        };
        spinner.Bind(NumericUpDown.ValueProperty, new Binding(nameof(AudioMatrixCellViewModel.GainDb))
        {
            Mode = BindingMode.TwoWay,
        });

        var mute = new CheckBox
        {
            Content = "M",
            FontSize = 10,
            Padding = new Avalonia.Thickness(2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            DataContext = cell,
        };
        ToolTip.SetTip(mute, "Mute this cell.");
        mute.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(AudioMatrixCellViewModel.Muted))
        {
            Mode = BindingMode.TwoWay,
        });

        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
        };
        panel.Children.Add(spinner);
        panel.Children.Add(mute);
        return panel;
    }

    private static Control BuildRouteGainEditor(AudioMatrixRouteRow row)
    {
        var spinner = new NumericUpDown
        {
            Minimum = -60,
            Maximum = 12,
            Increment = 1.0M,
            FormatString = "0.#",
            ShowButtonSpinner = false,
            ClipValueToMinMax = true,
            Width = 82,
            Padding = new Avalonia.Thickness(4, 2),
            DataContext = row,
        };
        spinner.Bind(NumericUpDown.ValueProperty, new Binding(nameof(AudioMatrixRouteRow.GainDb))
        {
            Mode = BindingMode.TwoWay,
        });
        return spinner;
    }

    private static Control BuildRouteMuteEditor(AudioMatrixRouteRow row)
    {
        var mute = new CheckBox
        {
            Content = "M",
            FontSize = 10,
            Padding = new Avalonia.Thickness(2, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            DataContext = row,
        };
        ToolTip.SetTip(mute, "Mute this route connection.");
        mute.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(AudioMatrixRouteRow.Muted))
        {
            Mode = BindingMode.TwoWay,
        });
        return mute;
    }

    private void OnPlaylistItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (sender is not ListBox lb) return;
        if (lb.SelectedItem is not HaPlay.Models.PlaylistItem item) return;
        _ = vm.PlayPlaylistItemAsync(item);
    }

    private void OnSeekSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (vm.SeekToSliderCommand.CanExecute(null))
            vm.SeekToSliderCommand.Execute(null);
    }

    private void OnSeekSliderKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            if (vm.SeekToSliderCommand.CanExecute(null))
                vm.SeekToSliderCommand.Execute(null);
        }
    }

    private void OnUserControlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (DataContext is not MediaPlayerViewModel vm) return;

        // Don't steal keystrokes from text editors — playlist-tab rename TextBox, NumericUpDown
        // (transition duration ms), idle-image path TextBox. Comma/period/space are literal input.
        if (e.Source is TextBox || e.Source is NumericUpDown)
            return;
        // SeekSlider has its own keyboard scrubbing (Left/Right etc.); the OnSeekSliderKeyUp handler
        // commits those edits. Don't intercept while the slider is focused.
        if (e.Source is Slider)
            return;
        // Modifier-chorded shortcuts (Ctrl+S etc.) belong to the parent MainView KeyBindings.
        if (e.KeyModifiers != KeyModifiers.None)
            return;

        switch (e.Key)
        {
            case Key.Space:
                if (vm.TogglePlayPauseCommand.CanExecute(null))
                {
                    vm.TogglePlayPauseCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemOpenBrackets:
                if (vm.PreviousTrackCommand.CanExecute(null))
                {
                    vm.PreviousTrackCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemCloseBrackets:
                if (vm.NextTrackCommand.CanExecute(null))
                {
                    vm.NextTrackCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemComma:
                if (vm.JogBackCommand.CanExecute(null))
                {
                    vm.JogBackCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.OemPeriod:
                if (vm.JogForwardCommand.CanExecute(null))
                {
                    vm.JogForwardCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }
}
