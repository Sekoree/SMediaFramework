using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using HaPlay.Models;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class CuePlayerView : UserControl
{
    private CuePlayerViewModel? _subscribedVm;
    private HierarchicalTreeDataGridSource<CueNodeViewModel>? _source;
    private FlatTreeDataGridSource<CueRouteConnectionViewModel>? _routeSource;
    private FlatTreeDataGridSource<CueVirtualOutputChannelViewModel>? _virtualOutputSource;

    public CuePlayerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (_subscribedVm is not null)
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribedVm = DataContext as CuePlayerViewModel;
        if (_subscribedVm is not null)
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
        RebuildCueSource();
        RebuildVirtualOutputSource();
        RebuildCueRouteSource();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(CuePlayerViewModel.VisibleNodes) or nameof(CuePlayerViewModel.SelectedCueList))
        {
            RebuildCueSource();
            RebuildVirtualOutputSource();
        }
        if (e.PropertyName is nameof(CuePlayerViewModel.VisibleRouteConnections)
            or nameof(CuePlayerViewModel.SelectedCueNode)
            or nameof(CuePlayerViewModel.VisibleVirtualOutputs))
        {
            if (e.PropertyName is nameof(CuePlayerViewModel.VisibleVirtualOutputs))
                RebuildVirtualOutputSource();
            if (e.PropertyName is nameof(CuePlayerViewModel.VisibleRouteConnections)
                or nameof(CuePlayerViewModel.SelectedCueNode))
                RebuildCueRouteSource();
        }
    }

    private void RebuildCueSource()
    {
        if (DataContext is not CuePlayerViewModel vm)
            return;

        _source = new HierarchicalTreeDataGridSource<CueNodeViewModel>(vm.VisibleNodes);
        _source.Columns.Add(new HierarchicalExpanderColumn<CueNodeViewModel>(
            new TemplateColumn<CueNodeViewModel>(
                "Cue",
                new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.Label)), supportsRecycling: true),
                width: new GridLength(250)),
            x => x.Children,
            x => x.HasChildren,
            x => x.IsExpanded));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "No.",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.Number)), supportsRecycling: true),
            width: new GridLength(88)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "Kind",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildReadOnlyText(row, nameof(CueNodeViewModel.KindLabel)), supportsRecycling: true),
            width: new GridLength(84)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "Trigger",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTriggerEditor(row), supportsRecycling: true),
            width: new GridLength(130)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "Pre(ms)",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildPreWaitEditor(row), supportsRecycling: true),
            width: new GridLength(90)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "Source/Action",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.SourceOrAction)), supportsRecycling: true),
            width: new GridLength(240)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "Endpoint Id",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.EndpointIdText)), supportsRecycling: true),
            width: new GridLength(220)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "Extra",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildExtraEditor(row), supportsRecycling: true),
            width: new GridLength(120)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            "Notes",
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.Notes)), supportsRecycling: true),
            width: new GridLength(260)));

        if (_source.RowSelection is not null)
        {
            _source.RowSelection.SelectionChanged += (_, _) =>
            {
                if (DataContext is CuePlayerViewModel activeVm)
                    activeVm.SelectedCueNode = _source.RowSelection.SelectedItem as CueNodeViewModel;
            };
        }

        CueTreeGrid.Source = _source;
    }

    private void RebuildVirtualOutputSource()
    {
        if (DataContext is not CuePlayerViewModel vm)
            return;

        _virtualOutputSource = new FlatTreeDataGridSource<CueVirtualOutputChannelViewModel>(vm.VisibleVirtualOutputs);
        _virtualOutputSource.Columns.Add(new TemplateColumn<CueVirtualOutputChannelViewModel>(
            "VOut",
            new FuncDataTemplate<CueVirtualOutputChannelViewModel>((row, _) => BuildVirtualOutputChannelEditor(row), supportsRecycling: true),
            width: new GridLength(84)));
        _virtualOutputSource.Columns.Add(new TemplateColumn<CueVirtualOutputChannelViewModel>(
            "Label",
            new FuncDataTemplate<CueVirtualOutputChannelViewModel>((row, _) => BuildTextEditor(row, nameof(CueVirtualOutputChannelViewModel.Label)), supportsRecycling: true),
            width: new GridLength(210)));

        if (_virtualOutputSource.RowSelection is not null)
        {
            _virtualOutputSource.RowSelection.SelectionChanged += (_, _) =>
            {
                if (DataContext is CuePlayerViewModel activeVm)
                    activeVm.SelectedVirtualOutput = _virtualOutputSource.RowSelection.SelectedItem as CueVirtualOutputChannelViewModel;
            };
        }

        VirtualOutputsTreeGrid.Source = _virtualOutputSource;
    }

    private void RebuildCueRouteSource()
    {
        if (DataContext is not CuePlayerViewModel vm)
            return;

        _routeSource = new FlatTreeDataGridSource<CueRouteConnectionViewModel>(vm.VisibleRouteConnections);
        _routeSource.Columns.Add(new TemplateColumn<CueRouteConnectionViewModel>(
            "In",
            new FuncDataTemplate<CueRouteConnectionViewModel>((row, _) => BuildRouteChannelEditor(row, nameof(CueRouteConnectionViewModel.InputChannel), 0, 63), supportsRecycling: true),
            width: new GridLength(76)));
        _routeSource.Columns.Add(new TemplateColumn<CueRouteConnectionViewModel>(
            "VOut",
            new FuncDataTemplate<CueRouteConnectionViewModel>((row, _) => BuildRouteChannelEditor(row, nameof(CueRouteConnectionViewModel.VirtualOutputChannel), 1, 64), supportsRecycling: true),
            width: new GridLength(92)));
        _routeSource.Columns.Add(new TemplateColumn<CueRouteConnectionViewModel>(
            "Gain dB",
            new FuncDataTemplate<CueRouteConnectionViewModel>((row, _) => BuildRouteGainEditor(row), supportsRecycling: true),
            width: new GridLength(104)));
        _routeSource.Columns.Add(new TemplateColumn<CueRouteConnectionViewModel>(
            "Mute",
            new FuncDataTemplate<CueRouteConnectionViewModel>((row, _) => BuildRouteMuteEditor(row), supportsRecycling: true),
            width: new GridLength(76)));

        if (_routeSource.RowSelection is not null)
        {
            _routeSource.RowSelection.SelectionChanged += (_, _) =>
            {
                if (DataContext is CuePlayerViewModel activeVm)
                    activeVm.SelectedRouteConnection = _routeSource.RowSelection.SelectedItem as CueRouteConnectionViewModel;
            };
        }

        CueRoutesTreeGrid.Source = _routeSource;
    }

    private static Control BuildReadOnlyText(CueNodeViewModel row, string propertyName)
    {
        var tb = new TextBlock
        {
            DataContext = row,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        tb.Bind(TextBlock.TextProperty, new Binding(propertyName));
        return tb;
    }

    private static Control BuildTextEditor(CueNodeViewModel row, string propertyName)
    {
        var box = new TextBox
        {
            DataContext = row,
            MinWidth = 80,
            Padding = new Avalonia.Thickness(4, 2),
        };
        box.Bind(TextBox.TextProperty, new Binding(propertyName) { Mode = BindingMode.TwoWay });
        return box;
    }

    private static Control BuildTextEditor(CueVirtualOutputChannelViewModel row, string propertyName)
    {
        var box = new TextBox
        {
            DataContext = row,
            MinWidth = 80,
            Padding = new Avalonia.Thickness(4, 2),
        };
        box.Bind(TextBox.TextProperty, new Binding(propertyName) { Mode = BindingMode.TwoWay });
        return box;
    }

    private static Control BuildVirtualOutputChannelEditor(CueVirtualOutputChannelViewModel row)
    {
        var n = new NumericUpDown
        {
            DataContext = row,
            Minimum = 1,
            Maximum = 128,
            Increment = 1,
            ShowButtonSpinner = false,
            Width = 72,
            FormatString = "0",
        };
        n.Bind(NumericUpDown.ValueProperty, new Binding(nameof(CueVirtualOutputChannelViewModel.Channel))
        {
            Mode = BindingMode.TwoWay,
        });
        return n;
    }

    private static Control BuildTriggerEditor(CueNodeViewModel row)
    {
        var combo = new ComboBox
        {
            DataContext = row,
            ItemsSource = Enum.GetValues<CueTriggerMode>(),
            MinWidth = 110,
        };
        combo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(CueNodeViewModel.TriggerMode))
        {
            Mode = BindingMode.TwoWay,
        });
        return combo;
    }

    private static Control BuildPreWaitEditor(CueNodeViewModel row)
    {
        var n = new NumericUpDown
        {
            DataContext = row,
            Minimum = 0,
            Maximum = 120_000,
            Increment = 50,
            ShowButtonSpinner = false,
            Width = 84,
            FormatString = "0",
        };
        n.Bind(NumericUpDown.ValueProperty, new Binding(nameof(CueNodeViewModel.PreWaitMs))
        {
            Mode = BindingMode.TwoWay,
        });
        return n;
    }

    private static Control BuildExtraEditor(CueNodeViewModel row)
    {
        if (row.Kind == CueNodeKind.Group)
        {
            var combo = new ComboBox
            {
                DataContext = row,
                ItemsSource = Enum.GetValues<CueGroupFireMode>(),
                MinWidth = 110,
            };
            combo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(CueNodeViewModel.GroupFireMode))
            {
                Mode = BindingMode.TwoWay,
            });
            return combo;
        }

        if (row.Kind == CueNodeKind.Action)
        {
            var combo = new ComboBox
            {
                DataContext = row,
                ItemsSource = Enum.GetValues<CueActionKind>(),
                MinWidth = 110,
            };
            combo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(CueNodeViewModel.ActionKind))
            {
                Mode = BindingMode.TwoWay,
            });
            return combo;
        }

        return BuildTextEditor(row, nameof(CueNodeViewModel.Extra));
    }

    private static Control BuildRouteChannelEditor(
        CueRouteConnectionViewModel row,
        string propertyName,
        int min,
        int max)
    {
        var n = new NumericUpDown
        {
            DataContext = row,
            Minimum = min,
            Maximum = max,
            Increment = 1,
            ShowButtonSpinner = false,
            Width = 72,
            FormatString = "0",
        };
        n.Bind(NumericUpDown.ValueProperty, new Binding(propertyName)
        {
            Mode = BindingMode.TwoWay,
        });
        return n;
    }

    private static Control BuildRouteGainEditor(CueRouteConnectionViewModel row)
    {
        var n = new NumericUpDown
        {
            DataContext = row,
            Minimum = -60,
            Maximum = 12,
            Increment = 1,
            ShowButtonSpinner = false,
            Width = 82,
            FormatString = "0.#",
        };
        n.Bind(NumericUpDown.ValueProperty, new Binding(nameof(CueRouteConnectionViewModel.GainDb))
        {
            Mode = BindingMode.TwoWay,
        });
        return n;
    }

    private static Control BuildRouteMuteEditor(CueRouteConnectionViewModel row)
    {
        var check = new CheckBox
        {
            DataContext = row,
            Content = "M",
            FontSize = 10,
            Padding = new Avalonia.Thickness(2, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(CueRouteConnectionViewModel.Muted))
        {
            Mode = BindingMode.TwoWay,
        });
        return check;
    }
}
