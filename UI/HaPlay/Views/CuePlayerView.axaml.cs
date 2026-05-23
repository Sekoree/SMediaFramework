using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using HaPlay.Resources;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class CuePlayerView : UserControl
{
    private CuePlayerViewModel? _subscribedVm;
    private HierarchicalTreeDataGridSource<CueNodeViewModel>? _source;

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
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName is nameof(CuePlayerViewModel.VisibleNodes) or nameof(CuePlayerViewModel.SelectedCueList))
            RebuildCueSource();
    }

    private void RebuildCueSource()
    {
        if (DataContext is not CuePlayerViewModel vm)
            return;

        _source = new HierarchicalTreeDataGridSource<CueNodeViewModel>(vm.VisibleNodes);
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeStatusColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildStatusBadge(row), supportsRecycling: true),
            width: new GridLength(28)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeNumberColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildCompactTextEditor(row, nameof(CueNodeViewModel.Number)), supportsRecycling: true),
            width: new GridLength(80)));
        _source.Columns.Add(new HierarchicalExpanderColumn<CueNodeViewModel>(
            new TemplateColumn<CueNodeViewModel>(
                Strings.CueTreeNameColumnHeader,
                new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildTextEditor(row, nameof(CueNodeViewModel.Label)), supportsRecycling: true),
                width: new GridLength(1, GridUnitType.Star)),
            x => x.Children,
            x => x.HasChildren,
            x => x.IsExpanded));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeDurationColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildReadOnlyText(row, nameof(CueNodeViewModel.DurationDisplay)), supportsRecycling: true),
            width: new GridLength(96)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeKindColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildReadOnlyText(row, nameof(CueNodeViewModel.KindLabel)), supportsRecycling: true),
            width: new GridLength(90)));

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

    private static Control BuildStatusBadge(CueNodeViewModel row)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            DataContext = row,
            Width = 10,
            Height = 10,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        void Sync()
        {
            dot.Fill = row.RowStatus switch
            {
                CueRowStatus.Current => Avalonia.Media.Brushes.OrangeRed,
                CueRowStatus.Standby => Avalonia.Media.Brushes.Goldenrod,
                _ => Avalonia.Media.Brushes.Transparent,
            };
            dot.Stroke = row.RowStatus == CueRowStatus.Idle
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(96, 255, 255, 255))
                : null;
            dot.StrokeThickness = row.RowStatus == CueRowStatus.Idle ? 1 : 0;
        }

        Sync();
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(CueNodeViewModel.RowStatus))
                Sync();
        };
        row.PropertyChanged += handler;
        dot.DetachedFromVisualTree += (_, _) => row.PropertyChanged -= handler;
        return dot;
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

    /// <summary>Fixed-width text editor for the narrow Number column — no MinWidth, so the cell
    /// shrinks with its column instead of bleeding into the status badge on its left.</summary>
    private static Control BuildCompactTextEditor(CueNodeViewModel row, string propertyName)
    {
        var box = new TextBox
        {
            DataContext = row,
            Padding = new Avalonia.Thickness(4, 2),
            Margin = new Avalonia.Thickness(2, 0, 0, 0),
        };
        box.Bind(TextBox.TextProperty, new Binding(propertyName) { Mode = BindingMode.TwoWay });
        return box;
    }
}
