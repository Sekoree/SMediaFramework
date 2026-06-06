using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using HaPlay.ViewModels;
using HaPlay.Views;

namespace HaPlay.Views.Dialogs;

public partial class AudioMatrixDialog : Window
{
    private MediaPlayerViewModel? _subscribedVm;

    public AudioMatrixDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_subscribedVm is not null)
            _subscribedVm.AudioMatrixLayoutChanged -= OnAudioMatrixLayoutChanged;
        _subscribedVm = DataContext as MediaPlayerViewModel;
        if (_subscribedVm is not null)
        {
            _subscribedVm.AudioMatrixLayoutChanged += OnAudioMatrixLayoutChanged;
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

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.AudioMatrixLayoutChanged -= OnAudioMatrixLayoutChanged;
            _subscribedVm = null;
        }

        MatrixTreeGrid?.Source = null;
        MatrixRoutesTreeGrid?.Source = null;
        base.OnClosed(e);
    }

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

        var source = new FlatTreeDataGridSource<AudioMatrixRow>(vm.AudioMatrixRows);

        source.Columns.Add(new TextColumn<AudioMatrixRow, string>(
            "Output", x => x.Label, width: new GridLength(220)));

        for (var i = 0; i < inputCount; i++)
        {
            var inputChannel = i;
            var header = inputCount == 2 ? $"In {(inputChannel == 0 ? "L" : "R")}" : $"In {inputChannel + 1}";
            source.Columns.Add(new TemplateColumn<AudioMatrixRow>(
                header,
                new FuncDataTemplate<AudioMatrixRow>((row, _) => BuildCellEditor(row, inputChannel), supportsRecycling: false),
                width: new GridLength(110)));
        }

        MatrixTreeGrid.Source = source;
    }

    private void RebuildAudioRouteSource()
    {
        if (DataContext is not MediaPlayerViewModel vm) return;
        if (MatrixRoutesTreeGrid is null) return;
        if (vm.AudioMatrixRouteRows.Count == 0)
        {
            MatrixRoutesTreeGrid.Source = null;
            return;
        }

        var source = new FlatTreeDataGridSource<AudioMatrixRouteRow>(vm.AudioMatrixRouteRows);
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("VOut", x => x.VirtualOutputLabel, width: new GridLength(92)));
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("Output", x => x.OutputLabel, width: new GridLength(240)));
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("Input", x => x.InputLabel, width: new GridLength(88)));
        source.Columns.Add(new TemplateColumn<AudioMatrixRouteRow>(
            "Gain dB",
            new FuncDataTemplate<AudioMatrixRouteRow>((row, _) => BuildRouteGainEditor(row), supportsRecycling: false),
            width: new GridLength(104)));
        source.Columns.Add(new TemplateColumn<AudioMatrixRouteRow>(
            "Mute",
            new FuncDataTemplate<AudioMatrixRouteRow>((row, _) => BuildRouteMuteEditor(row), supportsRecycling: false),
            width: new GridLength(72)));
        source.Columns.Add(new TextColumn<AudioMatrixRouteRow, string>("Effective", x => x.EffectiveGainText, width: new GridLength(96)));
        MatrixRoutesTreeGrid.Source = source;
    }

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
            Padding = new Thickness(4, 2),
            DataContext = cell,
        };
        AotBinding.TwoWay(spinner, NumericUpDown.ValueProperty, cell, nameof(AudioMatrixCellViewModel.GainDb),
            c => (decimal?)c.GainDb,
            (c, v) => c.GainDb = v is decimal d ? (double)d : 0.0);

        var mute = new CheckBox
        {
            Content = "M",
            FontSize = 10,
            Padding = new Thickness(2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            DataContext = cell,
        };
        ToolTip.SetTip(mute, "Mute this cell.");
        AotBinding.TwoWay(mute, CheckBox.IsCheckedProperty, cell, nameof(AudioMatrixCellViewModel.Muted),
            c => (bool?)c.Muted,
            (c, v) => c.Muted = v is bool b && b);

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
            Padding = new Thickness(4, 2),
            DataContext = row,
        };
        AotBinding.TwoWay(spinner, NumericUpDown.ValueProperty, row, nameof(AudioMatrixRouteRow.GainDb),
            r => (decimal?)r.GainDb,
            (r, v) => r.GainDb = v is decimal d ? (double)d : 0.0);
        return spinner;
    }

    private static Control BuildRouteMuteEditor(AudioMatrixRouteRow row)
    {
        var mute = new CheckBox
        {
            Content = "M",
            FontSize = 10,
            Padding = new Thickness(2, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            DataContext = row,
        };
        ToolTip.SetTip(mute, "Mute this route connection.");
        AotBinding.TwoWay(mute, CheckBox.IsCheckedProperty, row, nameof(AudioMatrixRouteRow.Muted),
            r => (bool?)r.Muted,
            (r, v) => r.Muted = v is bool b && b);
        return mute;
    }
}
