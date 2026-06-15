using Avalonia.Controls;
using Avalonia.Interactivity;
using HaPlay.ViewModels;
using HaPlay.ViewModels.Dialogs;

namespace HaPlay.Views.Dialogs;

public partial class CueOutputSetupDialog : Window
{
    public CueOutputSetupDialog()
    {
        InitializeComponent();
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
        var vm = new MappingEditorViewModel(
            outputName,
            composition.Width,
            composition.Height,
            binding.Mapping,
            apply: mapping =>
            {
                binding.Mapping = mapping;
                cuePlayer.UpdateOutputMappingCallback?.Invoke(binding.CompositionId, binding.OutputLineId, mapping);
            },
            setTestPattern: show =>
                cuePlayer.SetCompositionTestPatternCallback?.Invoke(binding.CompositionId, show) ?? false);

        var dialog = new MappingEditorDialog { DataContext = vm };
        dialog.Show(this);
    }

    /// <summary>Fits the clicked composition's canvas to an output's resolution. With one candidate output it
    /// applies directly; with several it offers a picker so the operator chooses the specific output.</summary>
    private void FitCompositionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CueCompositionViewModel composition } control
            || DataContext is not CuePlayerViewModel cuePlayer)
            return;

        var candidates = cuePlayer.OutputsForFit(composition);
        if (candidates.Count == 0)
        {
            ToastCenter.Info("No video output with a known resolution to fit to (set a window size or NDI resolution lock).");
            return;
        }

        if (candidates.Count == 1)
        {
            cuePlayer.FitCompositionToOutput(composition, candidates[0]);
            return;
        }

        var menu = new MenuFlyout();
        foreach (var line in candidates)
        {
            var target = line;
            var item = new MenuItem { Header = line.EffectiveName };
            item.Click += (_, _) => cuePlayer.FitCompositionToOutput(composition, target);
            menu.Items.Add(item);
        }
        menu.ShowAt(control);
    }

    /// <summary>Opens the multi-output layout editor for the clicked row's composition: every output bound to
    /// that composition is arranged together over the canvas. On Save, each output's source slice is written
    /// back to its binding mapping and live-applied to a running composition.</summary>
    private async void LayoutClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CueVideoOutputBindingViewModel clicked }
            || DataContext is not CuePlayerViewModel cuePlayer)
            return;

        var composition = cuePlayer.VisibleCompositions.FirstOrDefault(c => c.Id == clicked.CompositionId);
        if (composition is null)
        {
            ToastCenter.Info(HaPlay.Resources.Strings.MappingNeedsCompositionToast);
            return;
        }

        // All outputs bound to the same composition form one multi-output layout (video wall / stitched surface).
        var siblings = cuePlayer.VisibleVideoOutputs
            .Where(b => b.CompositionId == clicked.CompositionId && b.OutputLineId != System.Guid.Empty)
            .ToList();
        if (siblings.Count == 0)
        {
            ToastCenter.Info(HaPlay.Resources.Strings.MappingNeedsCompositionToast);
            return;
        }

        var vm = CompositionOutputLayoutViewModel.Build(
            composition.Width,
            composition.Height,
            siblings.Select(b => (
                b.OutputLineId,
                b.LineRef?.Definition.DisplayName ?? HaPlay.Resources.Strings.UnsetLabel,
                b.Mapping)));

        var dialog = new CompositionOutputLayoutDialog { DataContext = vm };
        var saved = await dialog.ShowDialog<bool>(this);
        if (!saved)
            return;

        foreach (var item in vm.Items)
        {
            var binding = siblings.FirstOrDefault(b => b.OutputLineId == item.OutputLineId);
            if (binding is null)
                continue;
            var mapping = vm.ToMapping(item);
            binding.Mapping = mapping;
            cuePlayer.UpdateOutputMappingCallback?.Invoke(binding.CompositionId, binding.OutputLineId, mapping);
        }
    }
}
