using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using HaPlay.Playback;
using HaPlay.Resources;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class CuePlayerView : UserControl
{
    private CuePlayerViewModel? _subscribedVm;
    private HierarchicalTreeDataGridSource<CueNodeViewModel>? _source;
    private IReadOnlyList<CueNodeViewModel>? _rowDragNodes;

    public CuePlayerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DragDrop.SetAllowDrop(this, true);
        // DragDrop.DragOver/Drop are Bubble-only routed events (no tunnel route). Registering them as
        // Tunnel meant the handlers never fired, so DragEffects was never set and every drop — internal
        // cue reorder AND external file drop — was rejected. Bubble + handledEventsToo catches the drop as
        // it bubbles up from the row/cell under the pointer.
        AddHandler(DragDrop.DragOverEvent, OnExternalFileDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnExternalFileDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        CueTreeGrid.RowDragStarted += OnCueTreeRowDragStarted;
        CueTreeGrid.RowDrop += OnCueTreeRowDrop;
        // Phase 5.2 — F2 on the tree opens the rename popup. Phase 5.6 added Del / Ctrl+D /
        // Ctrl+↑↓ on this same handler. Transport keys live on the UserControl below so they
        // fire whether the tree or anything else inside the cue tab has focus.
        CueTreeGrid.KeyDown += OnCueTreeKeyDown;
        KeyDown += OnUserControlKeyDown;
        CueScrubberSlider.AddHandler(PointerReleasedEvent, OnCueScrubberPointerReleased, RoutingStrategies.Bubble);
        CueScrubberSlider.AddHandler(KeyUpEvent, OnCueScrubberKeyUp, RoutingStrategies.Bubble);
    }

    /// <summary>Transport bindings (Space/Esc/Enter/Backspace) at UserControl level. Skip when
    /// focus is on a widget whose own keyboard handling we'd be fighting with: text input,
    /// the scrubber slider (Space scrolls), check boxes (Space toggles), and the like. Tree-row
    /// focus and unfocused button focus are left untouched so the transport keys still work.</summary>
    private void OnUserControlKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        _ = sender;
        if (DataContext is not CuePlayerViewModel vm) return;

        var focused = Avalonia.Controls.TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox or NumericUpDown or ComboBox or Slider or CheckBox or ToggleSwitch) return;

        switch (e.Key)
        {
            case Avalonia.Input.Key.Space:
                if (vm.GoCommand.CanExecute(null)) vm.GoCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Escape:
                if (vm.PanicCommand.CanExecute(null)) vm.PanicCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Enter:
                if (vm.StandbySelectedCommand.CanExecute(null)) vm.StandbySelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Back:
                if (vm.BackCommand.CanExecute(null)) vm.BackCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.P when e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control):
                if (vm.TogglePreviewCommand.CanExecute(null))
                    vm.TogglePreviewCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnCueScrubberPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not CuePlayerViewModel vm) return;
        if (vm.SeekActiveCueFromScrubberCommand.CanExecute(null))
            vm.SeekActiveCueFromScrubberCommand.Execute(null);
    }

    private void OnCueScrubberKeyUp(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CuePlayerViewModel vm) return;
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            if (vm.SeekActiveCueFromScrubberCommand.CanExecute(null))
                vm.SeekActiveCueFromScrubberCommand.Execute(null);
        }
    }

    private void OnCueTreeKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        _ = sender;
        if (DataContext is not CuePlayerViewModel vm) return;

        // Tree-scoped editing keys — these fire when the tree (or a tree row) has focus.
        // Transport keys (Space/Esc/Enter/Backspace) are bound at the UserControl level via
        // KeyBindings in XAML so they fire from anywhere except text-edit focus.
        switch (e.Key)
        {
            case Avalonia.Input.Key.F2:
                if (vm.RenameSelectedCueCommand.CanExecute(null))
                    vm.RenameSelectedCueCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Delete:
                if (vm.RemoveNodeCommand.CanExecute(null))
                    vm.RemoveNodeCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.D when e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control):
                if (vm.DuplicateSelectedCueCommand.CanExecute(null))
                    vm.DuplicateSelectedCueCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Up when e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control):
                if (vm.MoveSelectedCueUpCommand.CanExecute(null))
                    vm.MoveSelectedCueUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Avalonia.Input.Key.Down when e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control):
                if (vm.MoveSelectedCueDownCommand.CanExecute(null))
                    vm.MoveSelectedCueDownCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnCueTreeRowDragStarted(object? sender, TreeDataGridRowDragStartedEventArgs e)
    {
        if (DataContext is not CuePlayerViewModel vm || !vm.IsCueEditMode)
        {
            e.AllowedEffects = DragDropEffects.None;
            _rowDragNodes = null;
            return;
        }

        _rowDragNodes = e.Models.OfType<CueNodeViewModel>().ToArray();
        e.AllowedEffects = _rowDragNodes.Count > 0 ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void OnCueTreeRowDrop(object? sender, TreeDataGridRowDragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not CuePlayerViewModel vm || !vm.IsCueEditMode)
            return;
        if (_rowDragNodes is null || _rowDragNodes.Count == 0)
            return;
        if (e.Position == TreeDataGridRowDropPosition.None)
            return;

        var target = e.TargetRow?.DataContext as CueNodeViewModel;
        var placement = MapDropPosition(e.Position);
        foreach (var node in _rowDragNodes)
            vm.MoveCueNode(node, target, placement);
        _rowDragNodes = null;
    }

    private static CueNodeDropPlacement MapDropPosition(TreeDataGridRowDropPosition position) =>
        position switch
        {
            TreeDataGridRowDropPosition.Before => CueNodeDropPlacement.Before,
            TreeDataGridRowDropPosition.Inside => CueNodeDropPlacement.Inside,
            _ => CueNodeDropPlacement.After,
        };

    private bool IsOverCueTree(DragEventArgs e)
    {
        var pos = e.GetPosition(CueTreeGrid);
        return pos.X >= 0 && pos.Y >= 0
            && pos.X <= CueTreeGrid.Bounds.Width
            && pos.Y <= CueTreeGrid.Bounds.Height;
    }

    private void OnExternalFileDragOver(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (!IsOverCueTree(e)) return;
        if (e.DataTransfer.Contains(DataFormat.File))
            e.DragEffects = DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnExternalFileDrop(object? sender, DragEventArgs e)
    {
        _ = sender;
        if (!IsOverCueTree(e)) return;
        if (DataContext is not CuePlayerViewModel vm)
            return;
        e.Handled = true;

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
        else if (e.PropertyName is nameof(CuePlayerViewModel.IsCueEditMode))
            SyncCueTreeDragDropMode();
        else if (e.PropertyName is nameof(CuePlayerViewModel.SelectedCueNode))
            EnsureVisibleDrawerTabSelected();
    }

    /// <summary>
    /// UI rewrite P4 — stale-drawer fix: the property tabs are visibility-filtered per cue type, and
    /// switching e.g. from a media cue (Audio tab selected) to a group cue left the now-hidden tab
    /// selected, showing a blank/stale drawer. After every cue switch, snap selection to the first
    /// visible tab whenever the current one no longer applies. Posted at Loaded priority so the
    /// IsVisible bindings have settled first.
    /// </summary>
    private void EnsureVisibleDrawerTabSelected()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CueDrawerTabs.SelectedItem is TabItem { IsVisible: true })
                return;
            foreach (var item in CueDrawerTabs.Items)
            {
                if (item is TabItem { IsVisible: true } visible)
                {
                    CueDrawerTabs.SelectedItem = visible;
                    return;
                }
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>P4 — tap-to-seek on a group aggregate bar: every child seeks to the same fraction
    /// of its own duration. Same padlock gate as the single rows.</summary>
    private void OnNowPlayingGroupProgressPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ActiveGroupViewModel group } bar)
            return;
        if (DataContext is not CuePlayerViewModel vm || bar.Bounds.Width <= 0)
            return;
        var fraction = e.GetPosition(bar).X / bar.Bounds.Width;
        _ = vm.SeekActiveGroupToFractionAsync(group, fraction);
    }

    /// <summary>P4 — tap-to-seek on a Now Playing progress bar. The VM gates on the padlock
    /// (locked default) and on a known duration.</summary>
    private void OnNowPlayingProgressPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ActiveCueViewModel cue } bar)
            return;
        if (DataContext is not CuePlayerViewModel vm || bar.Bounds.Width <= 0)
            return;
        var fraction = e.GetPosition(bar).X / bar.Bounds.Width;
        _ = vm.SeekActiveCueToFractionAsync(cue, fraction);
    }

    private void SyncCueTreeDragDropMode()
    {
        CueTreeGrid.AutoDragDropRows = DataContext is CuePlayerViewModel { IsCueEditMode: true };
    }

    private void RebuildCueSource()
    {
        if (DataContext is not CuePlayerViewModel vm)
            return;

        _source = new HierarchicalTreeDataGridSource<CueNodeViewModel>(vm.VisibleNodes);
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            string.Empty,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildColorStrip(row), supportsRecycling: true),
            width: new GridLength(6)));
        _source.Columns.Add(new TemplateColumn<CueNodeViewModel>(
            Strings.CueTreeStatusColumnHeader,
            new FuncDataTemplate<CueNodeViewModel>((row, _) => BuildStatusBadge(row), supportsRecycling: true),
            width: new GridLength(28)));
        _source.Columns.Add(AotBinding.ReadOnlyTextColumn<CueNodeViewModel>(
            Strings.CueTreeNumberColumnHeader,
            nameof(CueNodeViewModel.Number),
            static row => row.Number,
            new GridLength(80)));
        _source.Columns.Add(new HierarchicalExpanderColumn<CueNodeViewModel>(
            AotBinding.ReadOnlyTextColumn<CueNodeViewModel>(
                Strings.CueTreeNameColumnHeader,
                nameof(CueNodeViewModel.Label),
                static row => row.Label,
                new GridLength(1, GridUnitType.Star)),
            x => x.Children,
            x => x.HasChildren,
            x => x.IsExpanded));
        _source.Columns.Add(AotBinding.ReadOnlyTextColumn<CueNodeViewModel>(
            Strings.CueTreeDurationColumnHeader,
            nameof(CueNodeViewModel.DurationDisplay),
            static row => row.DurationDisplay,
            new GridLength(96)));
        _source.Columns.Add(AotBinding.ReadOnlyTextColumn<CueNodeViewModel>(
            Strings.CueTreeKindColumnHeader,
            nameof(CueNodeViewModel.KindLabel),
            static row => row.KindLabel,
            new GridLength(90)));

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
        SyncCueTreeDragDropMode();
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
            if (args.PropertyName is nameof(CueNodeViewModel.RowStatus)
                or nameof(CueNodeViewModel.IsPreRollWarm)
                or nameof(CueNodeViewModel.PreRollState)
                or nameof(CueNodeViewModel.PreRollError))
                Sync();
        };

        void Sync()
        {
            var status = current?.RowStatus ?? CueRowStatus.Idle;
            var preRoll = current?.PreRollState ?? PreparedCueState.Idle;
            dot.Fill = status switch
            {
                CueRowStatus.Current => Avalonia.Media.Brushes.OrangeRed,
                CueRowStatus.Standby => Avalonia.Media.Brushes.Goldenrod,
                _ => Avalonia.Media.Brushes.Transparent,
            };
            // Idle rows show standby-preparation state via the outline so the operator can see, at a
            // glance, which upcoming cues are ready / still preparing / failed to prepare. Current and
            // Standby rows keep their solid color (the warming hint would be redundant while lit).
            if (status == CueRowStatus.Idle)
            {
                var (stroke, thickness) = preRoll switch
                {
                    // ready — light blue
                    PreparedCueState.Ready => (Avalonia.Media.Color.FromArgb(220, 80, 170, 255), 2.0),
                    // preparing — amber
                    PreparedCueState.Preparing => (Avalonia.Media.Color.FromArgb(200, 230, 170, 60), 1.5),
                    // stale — desaturated grey-blue (was ready, cue config changed; re-preparing)
                    PreparedCueState.Stale => (Avalonia.Media.Color.FromArgb(200, 150, 150, 165), 1.5),
                    // failed — red
                    PreparedCueState.Failed => (Avalonia.Media.Color.FromArgb(230, 229, 57, 53), 2.0),
                    // idle — faint white
                    _ => (Avalonia.Media.Color.FromArgb(96, 255, 255, 255), 1.0),
                };
                dot.Stroke = new Avalonia.Media.SolidColorBrush(stroke);
                dot.StrokeThickness = thickness;
            }
            else
            {
                dot.Stroke = null;
                dot.StrokeThickness = 0;
            }

            Avalonia.Controls.ToolTip.SetTip(dot, current?.PreRollStateTooltip);
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

    /// <summary>Thin vertical strip showing the cue's color tag (Phase 5.8.1). Rebinds to the
    /// row's <c>ColorTagBrush</c> when the cell is recycled. Hidden (transparent) when no tag
    /// is set.</summary>
    private static Control BuildColorStrip(CueNodeViewModel _)
    {
        var rect = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 4,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };

        CueNodeViewModel? current = null;
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is nameof(CueNodeViewModel.ColorTagBrush) or nameof(CueNodeViewModel.ColorTag))
                Sync();
        };

        void Sync()
        {
            var hex = current?.ColorTagBrush ?? "Transparent";
            try
            {
                rect.Fill = Avalonia.Media.Brush.Parse(hex);
            }
            catch
            {
                rect.Fill = Avalonia.Media.Brushes.Transparent;
            }
        }

        void Rebind(CueNodeViewModel? next)
        {
            if (ReferenceEquals(current, next)) return;
            if (current is not null) current.PropertyChanged -= handler;
            current = next;
            if (current is not null) current.PropertyChanged += handler;
            Sync();
        }

        rect.DataContextChanged += (_, _) => Rebind(rect.DataContext as CueNodeViewModel);
        rect.DetachedFromVisualTree += (_, _) => Rebind(null);
        Rebind(rect.DataContext as CueNodeViewModel);
        return rect;
    }
}
