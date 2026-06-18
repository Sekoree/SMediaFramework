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
        }
    }

    private void OnAudioMatrixLayoutChanged(object? sender, System.EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RebuildAudioMatrixSource();
        });

    protected override void OnClosed(EventArgs e)
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.AudioMatrixLayoutChanged -= OnAudioMatrixLayoutChanged;
            _subscribedVm = null;
        }

        MatrixTreeGrid?.Source = null;
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
            "Output channel", x => x.Label, width: new GridLength(240)));

        for (var i = 0; i < inputCount; i++)
        {
            var inputChannel = i;
            var header = inputCount == 2 ? $"In {(inputChannel == 0 ? "L" : "R")}" : $"In {inputChannel + 1}";
            source.Columns.Add(new TemplateColumn<AudioMatrixRow>(
                header,
                new FuncDataTemplate<AudioMatrixRow>((_, _) => BuildCellEditor(inputChannel), supportsRecycling: true),
                width: new GridLength(82)));
        }

        MatrixTreeGrid.Source = source;
    }

    private static Control BuildCellEditor(int inputChannel)
    {
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 4,
        };

        var placeholder = new TextBlock { Text = "—", Opacity = 0.45 };

        var spinner = new NumericUpDown
        {
            Minimum = -60,
            Maximum = 12,
            Increment = 1.0M,
            FormatString = "0.#",
            ShowButtonSpinner = false,
            ClipValueToMinMax = true,
            Width = 58,
            Padding = new Thickness(4, 2),
        };
        AotBinding.TwoWayFromDataContext<AudioMatrixRow, AudioMatrixCellViewModel>(
            spinner,
            NumericUpDown.ValueProperty,
            row => row?.GetCell(inputChannel),
            nameof(AudioMatrixCellViewModel.GainDb),
            c => (decimal?)c.GainDb,
            (c, v) => c.GainDb = v is decimal d ? (double)d : 0.0);

        var mute = new CheckBox
        {
            Content = "M",
            FontSize = 10,
            Padding = new Thickness(2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        ToolTip.SetTip(mute, "Mute this cell.");
        AotBinding.TwoWayFromDataContext<AudioMatrixRow, AudioMatrixCellViewModel>(
            mute,
            CheckBox.IsCheckedProperty,
            row => row?.GetCell(inputChannel),
            nameof(AudioMatrixCellViewModel.Muted),
            c => (bool?)c.Muted,
            (c, v) => c.Muted = v is bool b && b);

        void SyncVisibility()
        {
            var row = panel.DataContext as AudioMatrixRow;
            var hasCell = row?.GetCell(inputChannel) is not null;
            placeholder.IsVisible = !hasCell;
            spinner.IsVisible = hasCell;
            mute.IsVisible = hasCell;
        }

        panel.DataContextChanged += (_, _) => SyncVisibility();
        panel.Children.Add(placeholder);
        panel.Children.Add(spinner);
        panel.Children.Add(mute);
        SyncVisibility();
        return panel;
    }
}
