using System.ComponentModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using HaPlay.Models;
using HaPlay.Resources;
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
        DragDrop.SetAllowDrop(CueTreeGrid, true);
        CueTreeGrid.AddHandler(DragDrop.DragOverEvent, OnCueTreeDragOver, RoutingStrategies.Bubble);
        CueTreeGrid.AddHandler(DragDrop.DropEvent, OnCueTreeDrop, RoutingStrategies.Bubble);
    }

    private void OnCueTreeDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnCueTreeDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (DataContext is not CuePlayerViewModel vm)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null || !files.Any())
            return;

        var paths = files
            .Select(f => f.Path.LocalPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (paths.Count > 0)
            vm.AddMediaFilesFromDrop(paths);
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
                Strings.CueTreeCueColumnHeader,
                new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.Label)), supportsRecycling: true),
                width: new GridLength(250)),
            x => x.Children,
            x => x.HasChildren,
            x => x.IsExpanded));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeNumberColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.Number)), supportsRecycling: true),
            width: new GridLength(88)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeKindColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildReadOnlyText(row, nameof(CueNodeViewModel.KindLabel)), supportsRecycling: true),
            width: new GridLength(84)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeTriggerColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTriggerEditor(row), supportsRecycling: true),
            width: new GridLength(130)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreePreMsColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildPreWaitEditor(row), supportsRecycling: true),
            width: new GridLength(90)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeSourceActionColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildSourceOrActionEditor(row), supportsRecycling: true),
            width: new GridLength(240)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeEndpointIdColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildEndpointEditor(vm, row), supportsRecycling: true),
            width: new GridLength(220)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeExtraColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildExtraEditor(row), supportsRecycling: true),
            width: new GridLength(120)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeNotesColumnHeader,
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
            Strings.CueVirtualOutputColumnHeader,
            new FuncDataTemplate<CueVirtualOutputChannelViewModel>((row, _) => BuildVirtualOutputChannelEditor(row), supportsRecycling: true),
            width: new GridLength(84)));
        _virtualOutputSource.Columns.Add(new TemplateColumn<CueVirtualOutputChannelViewModel>(
            Strings.CueLabelColumnHeader,
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
            Strings.CueRouteInputColumnHeader,
            new FuncDataTemplate<CueRouteConnectionViewModel>((row, _) => BuildRouteChannelEditor(row, nameof(CueRouteConnectionViewModel.InputChannel), 0, 63), supportsRecycling: true),
            width: new GridLength(76)));
        _routeSource.Columns.Add(new TemplateColumn<CueRouteConnectionViewModel>(
            Strings.CueRouteVoutColumnHeader,
            new FuncDataTemplate<CueRouteConnectionViewModel>((row, _) => BuildRouteVirtualOutputEditor(vm, row), supportsRecycling: true),
            width: new GridLength(92)));
        _routeSource.Columns.Add(new TemplateColumn<CueRouteConnectionViewModel>(
            Strings.CueRouteGainDbColumnHeader,
            new FuncDataTemplate<CueRouteConnectionViewModel>((row, _) => BuildRouteGainEditor(row), supportsRecycling: true),
            width: new GridLength(104)));
        _routeSource.Columns.Add(new TemplateColumn<CueRouteConnectionViewModel>(
            Strings.CueRouteMuteColumnHeader,
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

    /// <summary>"Not applicable for this kind" placeholder — dimmed em-dash so the row visually
    /// signals that the column has no meaningful value for the current cue kind (comments don't
    /// fire, groups don't carry endpoints, etc.).</summary>
    private static Control BuildInapplicablePlaceholder() => new TextBlock
    {
        Text = Strings.EmDash,
        Opacity = 0.35,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    };

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
        // Comments are descriptive-only — they never fire, so Trigger is meaningless.
        if (row.Kind == CueNodeKind.Comment)
            return BuildInapplicablePlaceholder();

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
        // Comments don't fire, so a pre-wait delay has nothing to gate.
        if (row.Kind == CueNodeKind.Comment)
            return BuildInapplicablePlaceholder();

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

        // Comments have no Extra payload (only label + notes).
        if (row.Kind == CueNodeKind.Comment)
            return BuildInapplicablePlaceholder();

        return BuildTextEditor(row, nameof(CueNodeViewModel.Extra));
    }

    private static Control BuildSourceOrActionEditor(CueNodeViewModel row)
    {
        return row.Kind switch
        {
            // Group rows do not use Source/Action payload text.
            CueNodeKind.Group => BuildInapplicablePlaceholder(),
            // Comments have no media/action payload either.
            CueNodeKind.Comment => BuildInapplicablePlaceholder(),
            _ => BuildTextEditor(row, nameof(CueNodeViewModel.SourceOrAction)),
        };
    }

    private static Control BuildEndpointEditor(CuePlayerViewModel vm, CueNodeViewModel row)
    {
        if (row.Kind != CueNodeKind.Action)
            return BuildInapplicablePlaceholder();

        var options = BuildEndpointOptions(vm, row.EndpointIdText);
        var combo = new ComboBox
        {
            DataContext = row,
            ItemsSource = options,
            MinWidth = 170,
        };

        void SyncSelectionFromRow()
        {
            var selectedId = (row.EndpointIdText ?? string.Empty).Trim();
            var selected = options.FirstOrDefault(o =>
                string.Equals(o.IdText, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault();
            combo.SelectedItem = selected;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is EndpointOption selected)
                row.EndpointIdText = selected.IdText;
        };

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(CueNodeViewModel.EndpointIdText))
                SyncSelectionFromRow();
        };
        NotifyCollectionChangedEventHandler endpointCollectionHandler = (_, _) =>
        {
            options = BuildEndpointOptions(vm, row.EndpointIdText);
            combo.ItemsSource = options;
            SyncSelectionFromRow();
        };
        row.PropertyChanged += handler;
        vm.ActionEndpoints.CollectionChanged += endpointCollectionHandler;
        combo.DetachedFromVisualTree += (_, _) =>
        {
            row.PropertyChanged -= handler;
            vm.ActionEndpoints.CollectionChanged -= endpointCollectionHandler;
        };

        SyncSelectionFromRow();
        return combo;
    }

    private static List<EndpointOption> BuildEndpointOptions(CuePlayerViewModel vm, string endpointIdText)
    {
        var options = new List<EndpointOption>
        {
            new(string.Empty, Strings.EndpointNoneOptionLabel),
        };

        foreach (var endpoint in vm.ActionEndpoints.OrderBy(e => e.KindLabel).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var summary = string.IsNullOrWhiteSpace(endpoint.Summary)
                ? endpoint.Name
                : $"{endpoint.Name} · {endpoint.Summary}";
            options.Add(new EndpointOption(endpoint.Id.ToString(), $"{endpoint.KindLabel}: {summary}"));
        }

        var trimmed = (endpointIdText ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmed)
            && options.All(o => !string.Equals(o.IdText, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new EndpointOption(trimmed, Strings.Format(nameof(Strings.EndpointMissingOptionFormat), trimmed)));
        }

        return options;
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
            Content = Strings.CueRouteMuteShortLabel,
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

    private static Control BuildRouteVirtualOutputEditor(CuePlayerViewModel vm, CueRouteConnectionViewModel row)
    {
        var channels = vm.VisibleVirtualOutputs
            .Select(v => v.Channel)
            .Distinct()
            .OrderBy(v => v)
            .ToList();
        if (channels.Count == 0)
            return BuildRouteChannelEditor(row, nameof(CueRouteConnectionViewModel.VirtualOutputChannel), 1, 64);

        var combo = new ComboBox
        {
            DataContext = row,
            ItemsSource = channels,
            MinWidth = 80,
        };
        combo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(CueRouteConnectionViewModel.VirtualOutputChannel))
        {
            Mode = BindingMode.TwoWay,
        });
        return combo;
    }

    private sealed record EndpointOption(string IdText, string Display)
    {
        public override string ToString() => Display;
    }
}
