using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using S.Control;
using HaPlay.Resources;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using OSCLib;

namespace HaPlay.ViewModels;

public partial class ControlWorkspaceViewModel
{
    [RelayCommand]
    private async Task LoadControlConfigAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var opts = new FilePickerOpenOptions
        {
            Title = Strings.OpenControlConfigDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayControlFileTypeLabel)
                {
                    Patterns = ["*." + ControlSystemIO.FileExtension],
                },
                new FilePickerFileType(Strings.JsonFileTypeLabel) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };

        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts).ConfigureAwait(true);
        var picked = picks.FirstOrDefault();
        if (picked is null)
            return;

        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        await LoadControlConfigFromPathAsync(path).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task SaveControlConfigAsync() =>
        !string.IsNullOrEmpty(_configFilePath)
            ? SaveControlConfigToPathAsync(_configFilePath)
            : SaveControlConfigAsAsync();

    /// <summary>Save/load rework - export one layer (+ its scripts) as a standalone control-config
    /// slice (same file format as a full config; see <see cref="ControlConfigSlices"/>).</summary>
    private async Task ExportLayerAsync(ControlStructureRowViewModel row)
    {
        if (row.LayerId is not { } layerId)
            return;
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var slice = ControlConfigSlices.ExtractLayers(BuildSnapshot(), [layerId]);
        if (slice.Layers.Count == 0)
            return;

        var opts = new FilePickerSaveOptions
        {
            Title = Strings.ExportLayerDialogTitle,
            DefaultExtension = ControlSystemIO.FileExtension,
            SuggestedFileName = Strings.Format(
                nameof(Strings.ControlConfigDefaultFileNameFormat),
                SanitizeControlConfigFileName(slice.Layers[0].Name),
                ControlSystemIO.FileExtension),
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayControlFileTypeLabel)
                {
                    Patterns = ["*." + ControlSystemIO.FileExtension],
                },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            await ControlSystemIO.SaveConfigAsync(slice, path);
            StatusMessage = Strings.Format(nameof(Strings.LayerExportedStatusFormat), slice.Layers[0].Name, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Save/load rework - merge a layer slice (or any control config's layers) into the
    /// current system. Layers replace by name; everything else in the running config is kept.</summary>
    [RelayCommand]
    private async Task ImportLayerAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.ImportLayerDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayControlFileTypeLabel)
                {
                    Patterns = ["*." + ControlSystemIO.FileExtension],
                },
            ],
        };
        var files = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            var slice = await ControlSystemIO.LoadConfigAsync(path);
            if (slice.Layers.Count == 0)
            {
                StatusMessage = Strings.ImportLayerNoLayersStatus;
                return;
            }

            LoadConfig(ControlConfigSlices.MergeLayers(BuildSnapshot(), slice), _configFilePath);
            StatusMessage = Strings.Format(nameof(Strings.LayerImportedStatusFormat),
                string.Join(", ", slice.Layers.Select(l => l.Name)));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveControlConfigAsAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return;

        var opts = new FilePickerSaveOptions
        {
            Title = Strings.SaveControlConfigDialogTitle,
            DefaultExtension = ControlSystemIO.FileExtension,
            SuggestedFileName = string.IsNullOrEmpty(_configFilePath)
                ? Strings.Format(
                    nameof(Strings.ControlConfigDefaultFileNameFormat),
                    SanitizeControlConfigFileName(Strings.ControlConfigFileNameFallback),
                    ControlSystemIO.FileExtension)
                : Path.GetFileName(_configFilePath),
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayControlFileTypeLabel)
                {
                    Patterns = ["*." + ControlSystemIO.FileExtension],
                },
            ],
        };

        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts).ConfigureAwait(true);
        if (picked is null)
            return;

        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        await SaveControlConfigToPathAsync(path).ConfigureAwait(true);
    }
}
