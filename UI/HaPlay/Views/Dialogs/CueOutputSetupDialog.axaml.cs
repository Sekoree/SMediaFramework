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
}
