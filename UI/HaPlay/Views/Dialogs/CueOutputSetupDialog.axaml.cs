using Avalonia.Controls;
using Avalonia.Interactivity;
using System.ComponentModel;
using HaPlay.Playback;
using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class CueOutputSetupDialog : Window
{
    public CueOutputSetupDialog()
    {
        InitializeComponent();
        DialogTopmostPin.Attach(this); // modal: keep above the owner (see helper docs)
        DialogStatePersister.Attach(this, nameof(CueOutputSetupDialog), MinWidth, MinHeight);
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Opens the warp-section editor for the row's binding. Changes persist into the
    /// binding VM immediately and live-apply when the composition is running.</summary>
    private void MappingClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CueVideoOutputBindingViewModel binding }
            || DataContext is not CuePlayerViewModel cuePlayer)
            return;

        var composition = cuePlayer.VisibleCompositions.FirstOrDefault(c => c.Id == binding.CompositionId);
        if (composition is null)
        {
            ToastCenter.Info(HaPlay.Resources.Strings.MappingNeedsCompositionToast);
            return;
        }

        var outputName = binding.LineRef?.Definition.DisplayName ?? HaPlay.Resources.Strings.UnsetLabel;
        var disabledSeed = binding.Mapping is null
            ? BuildLayoutSeed(cuePlayer, composition, binding)
            : null;
        MappingEditorViewModel? vm = null;
        vm = new MappingEditorViewModel(
            outputName,
            composition.Width,
            composition.Height,
            binding.Mapping,
            apply: (mapping, enabled) =>
            {
                // Retain the edited geometry even when disabled so re-enabling restores the exact slice;
                // only feed the live composition a mapping while enabled (null = raw canvas).
                binding.Mapping = mapping;
                binding.MappingEnabled = enabled;
                cuePlayer.UpdateOutputMappingCallback?.Invoke(
                    binding.CompositionId, binding.OutputLineId, enabled ? mapping : null);
            },
            setTestPattern: show =>
                cuePlayer.SetCompositionTestPatternCallback?.Invoke(
                    binding.CompositionId,
                    binding.OutputLineId,
                    show ? vm?.ToMapping() ?? binding.Mapping : null,
                    show) ?? false,
            disabledSeed: disabledSeed,
            initialEnabled: binding.Mapping is not null && binding.MappingEnabled);

        var dialog = new MappingEditorDialog { DataContext = vm };
        dialog.Show(this);
    }

    private void CompositionFxClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CueCompositionViewModel composition }
            || DataContext is not CuePlayerViewModel cuePlayer)
            return;

        static CueOutputMapping? CanvasSized(CueOutputMapping? mapping) =>
            mapping is null ? null : mapping with { OutputWidth = null, OutputHeight = null };

        var initial = CanvasSized(composition.VideoFx);
        var targetName = composition.DisplayName;
        var vm = new MappingEditorViewModel(
            targetName,
            composition.Width,
            composition.Height,
            initial,
            apply: (mapping, enabled) =>
            {
                var fx = CanvasSized(mapping);
                composition.VideoFx = fx;
                composition.VideoFxEnabled = enabled;
                cuePlayer.UpdateCompositionVideoFxCallback?.Invoke(composition.Id, enabled ? fx : null);
            },
            initialEnabled: initial is not null && composition.VideoFxEnabled,
            dialogTitlePrefix: "Composition FX",
            enableLabel: "Enable composition FX",
            sizeLabel: "FX size",
            canEditOutputSize: false);

        var dialog = new MappingEditorDialog { DataContext = vm };
        dialog.Show(this);
    }

    private static CueOutputMapping? BuildLayoutSeed(
        CuePlayerViewModel cuePlayer,
        CueCompositionViewModel composition,
        CueVideoOutputBindingViewModel target)
    {
        if (target.OutputLineId == Guid.Empty)
            return null;

        var siblings = cuePlayer.VisibleVideoOutputs
            .Where(b => b.CompositionId == composition.Id && b.OutputLineId != Guid.Empty)
            .ToList();
        if (siblings.Count == 0)
            return null;

        var layout = CompositionOutputLayoutViewModel.Build(
            composition.Width,
            composition.Height,
            siblings.Select(b =>
            {
                var (width, height) = ResolveOutputResolution(b.LineRef);
                return (
                    b.OutputLineId,
                    b.LineRef?.Definition.DisplayName ?? HaPlay.Resources.Strings.UnsetLabel,
                    width,
                    height,
                    b.Mapping);
            }));

        var item = layout.Items.FirstOrDefault(i => i.OutputLineId == target.OutputLineId);
        return item is null ? null : layout.ToMapping(item);
    }

    /// <summary>Opens the multi-output layout editor for the clicked composition: every output bound to it is
    /// arranged together over the canvas. On Save, each output's source slice is written back to its binding
    /// mapping and live-applied to a running composition.</summary>
    private async void LayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CueCompositionViewModel composition }
            || DataContext is not CuePlayerViewModel cuePlayer)
            return;

        // All outputs bound to this composition form one multi-output layout (video wall / stitched surface).
        var siblings = cuePlayer.VisibleVideoOutputs
            .Where(b => b.CompositionId == composition.Id && b.OutputLineId != System.Guid.Empty)
            .ToList();
        if (siblings.Count == 0)
        {
            ToastCenter.Info("Bind one or more outputs to this composition first, then arrange them here.");
            return;
        }

        var vm = CompositionOutputLayoutViewModel.Build(
            composition.Width,
            composition.Height,
            siblings.Select(b =>
            {
                var (width, height) = ResolveOutputResolution(b.LineRef);
                return (
                    b.OutputLineId,
                    b.LineRef?.Definition.DisplayName ?? HaPlay.Resources.Strings.UnsetLabel,
                    width,
                    height,
                    b.Mapping);
            }));

        // A saved mapping retains its last render raster, but a local window is itself the physical output.
        // Its current client/fullscreen size therefore wins when the editor opens and continues to update
        // while this modal editor is visible.
        foreach (var binding in siblings)
            RefreshLocalOutputResolution(vm, binding);

        var dialog = new CompositionOutputLayoutDialog { DataContext = vm };
        var watchedLines = siblings
            .Select(binding => binding.LineRef)
            .Where(line => line?.Definition is LocalVideoOutputDefinition)
            .OfType<OutputLineViewModel>()
            .Distinct()
            .ToList();
        PropertyChangedEventHandler onOutputChanged = (changed, args) =>
        {
            if (changed is not OutputLineViewModel line
                || args.PropertyName is not (nameof(OutputLineViewModel.Definition)
                    or nameof(OutputLineViewModel.LiveVideoWidth)
                    or nameof(OutputLineViewModel.LiveVideoHeight)))
                return;
            foreach (var binding in siblings.Where(candidate => ReferenceEquals(candidate.LineRef, line)))
                RefreshLocalOutputResolution(vm, binding);
        };
        foreach (var line in watchedLines)
            line.PropertyChanged += onOutputChanged;

        bool saved;
        try
        {
            saved = await dialog.ShowDialog<bool>(this);
        }
        finally
        {
            foreach (var line in watchedLines)
                line.PropertyChanged -= onOutputChanged;
        }
        if (!saved)
            return;

        foreach (var item in vm.Items)
        {
            var binding = siblings.FirstOrDefault(b => b.OutputLineId == item.OutputLineId);
            if (binding is null)
                continue;
            var mapping = vm.ToMapping(item);
            binding.Mapping = mapping;
            binding.MappingEnabled = true; // arranging an output in the layout enables its mapping
            cuePlayer.UpdateOutputMappingCallback?.Invoke(binding.CompositionId, binding.OutputLineId, mapping);
        }
    }

    private static void RefreshLocalOutputResolution(
        CompositionOutputLayoutViewModel layout,
        CueVideoOutputBindingViewModel binding)
    {
        if (binding.LineRef?.Definition is not LocalVideoOutputDefinition)
            return;
        var (width, height) = ResolveOutputResolution(binding.LineRef);
        if (width is { } w && height is { } h)
            layout.UpdateOutputResolution(binding.OutputLineId, w, h);
    }

    private static (int? Width, int? Height) ResolveOutputResolution(OutputLineViewModel? line) =>
        line is { LiveVideoWidth: > 0, LiveVideoHeight: > 0 }
            ? (line.LiveVideoWidth, line.LiveVideoHeight)
            : line?.Definition is { } definition
              && HaPlayPlaybackHelpers.TryGetOutputResolution(definition, out var width, out var height)
            ? (width, height)
            : (null, null);
}
