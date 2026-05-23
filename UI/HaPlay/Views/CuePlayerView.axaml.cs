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
        // Phase 5.2 — F2 on the tree opens the rename popup. Full keyboard map lands in 5.6.
        CueTreeGrid.KeyDown += OnCueTreeKeyDown;
    }

    private void OnCueTreeKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        _ = sender;
        if (e.Key != Avalonia.Input.Key.F2) return;
        if (DataContext is not CuePlayerViewModel vm) return;
        if (vm.RenameSelectedCueCommand.CanExecute(null))
            vm.RenameSelectedCueCommand.Execute(null);
        e.Handled = true;
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
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildReadOnlyText(row, nameof(CueNodeViewModel.Number)), supportsRecycling: true),
            width: new GridLength(80)));
        _source.Columns.Add(new HierarchicalExpanderColumn<CueNodeViewModel>(
            new TemplateColumn<CueNodeViewModel>(
                Strings.CueTreeNameColumnHeader,
                new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildReadOnlyText(row, nameof(CueNodeViewModel.Label)), supportsRecycling: true),
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
            // Multi-select so operators can select N media cues + add the same route / placement
            // to them all at once via the drawer commands.
            _source.RowSelection.SingleSelect = false;
            _source.RowSelection.SelectionChanged += (_, _) =>
            {
                if (DataContext is not CuePlayerViewModel activeVm) return;
                var selected = _source.RowSelection.SelectedItems
                    .OfType<CueNodeViewModel>()
                    .ToList();
                activeVm.UpdateSelection(selected);
            };
        }

        CueTreeGrid.Source = _source;
    }

    /// <summary>Status indicator dot. Re-subscribes to the row's <c>RowStatus</c> notifications
    /// when the cell is recycled to a different row (which happens routinely under
    /// <c>supportsRecycling: true</c>) — the previous design captured the row reference at
    /// construction and stayed bound to the old row forever.</summary>
    private static Control BuildStatusBadge(CueNodeViewModel _)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        CueNodeViewModel? current = null;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName == nameof(CueNodeViewModel.RowStatus))
                Sync();
        };

        void Sync()
        {
            var status = current?.RowStatus ?? CueRowStatus.Idle;
            dot.Fill = status switch
            {
                CueRowStatus.Current => Avalonia.Media.Brushes.OrangeRed,
                CueRowStatus.Standby => Avalonia.Media.Brushes.Goldenrod,
                _ => Avalonia.Media.Brushes.Transparent,
            };
            dot.Stroke = status == CueRowStatus.Idle
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(96, 255, 255, 255))
                : null;
            dot.StrokeThickness = status == CueRowStatus.Idle ? 1 : 0;
        }

        void Rebind(CueNodeViewModel? next)
        {
            if (ReferenceEquals(current, next)) return;
            if (current is not null) current.PropertyChanged -= handler;
            current = next;
            if (current is not null) current.PropertyChanged += handler;
            Sync();
        }

        dot.DataContextChanged += (_, _) => Rebind(dot.DataContext as CueNodeViewModel);
        dot.DetachedFromVisualTree += (_, _) => Rebind(null);
        // The grid sets DataContext after attachment; initial sync runs against whatever the
        // current DataContext is right now (may be null until the cell is bound).
        Rebind(dot.DataContext as CueNodeViewModel);
        return dot;
    }

    // NOTE: do NOT set DataContext on these controls. TreeDataGrid recycles cell controls when
    // supportsRecycling=true, and an explicit DataContext sticks past the recycle — the visual
    // ends up bound to the OLD row's properties. Letting DataContext inherit from the grid lets
    // the binding re-resolve against the new row each time the control is reused.
    private static Control BuildReadOnlyText(CueNodeViewModel row, string propertyName)
    {
        _ = row;
        var tb = new TextBlock
        {
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        tb.Bind(TextBlock.TextProperty, new Binding(propertyName));
        return tb;
    }

}
