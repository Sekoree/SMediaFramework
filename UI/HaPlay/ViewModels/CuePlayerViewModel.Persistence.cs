using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

public partial class CuePlayerViewModel
{
    [RelayCommand]
    private async Task LoadCueListAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null) return;

        var opts = new FilePickerOpenOptions
        {
            Title = Strings.OpenCueListDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayCueListFileTypeLabel) { Patterns = ["*." + CueListIO.FileExtension] },
                new FilePickerFileType(Strings.JsonFileTypeLabel) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };

        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var picked = picks.FirstOrDefault();
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var list = await CueListIO.LoadAsync(path);
            var vm = CueListEditorViewModel.FromModel(list, path, ResolveOutputLine);
            ClearCueListsCollectionPath();
            CueLists.Add(vm);
            SelectedCueList = vm;
            SelectedCueNode = null;
            StatusMessage = Strings.Format(nameof(Strings.LoadedCueListStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.CueListLoadFailedStatusFormat), ex.Message);
        }
    }

    /// <summary>Save/load rework - export the selected cue list's compositions (virtual canvases)
    /// as a standalone shareable file.</summary>
    [RelayCommand]
    private async Task ExportCompositionsAsync()
    {
        if (SelectedCueList is null || VisibleCompositions.Count == 0)
        {
            StatusMessage = Strings.NoCompositionsToExportStatus;
            return;
        }

        var owner = TryGetMainWindow();
        if (owner is null) return;
        var opts = new FilePickerSaveOptions
        {
            Title = Strings.ExportCompositionsDialogTitle,
            DefaultExtension = CueCompositionsIO.FileExtension,
            SuggestedFileName = $"compositions.{CueCompositionsIO.FileExtension}",
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.CompositionsFileTypeLabel)
                    { Patterns = ["*." + CueCompositionsIO.FileExtension] },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            await CueCompositionsIO.SaveAsync(VisibleCompositions.Select(c => c.ToModel()).ToList(), path);
            StatusMessage = Strings.Format(nameof(Strings.CompositionsExportedStatusFormat),
                VisibleCompositions.Count, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Save/load rework - merge a compositions file into the selected cue list. Same-named
    /// compositions update their size/fps in place (keeping the Id, so cue placements bound to them
    /// stay valid); new names append (fresh Id on collision).</summary>
    [RelayCommand]
    private async Task ImportCompositionsAsync()
    {
        if (SelectedCueList is null)
            return;
        var owner = TryGetMainWindow();
        if (owner is null) return;
        var opts = new FilePickerOpenOptions
        {
            Title = Strings.ImportCompositionsDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.CompositionsFileTypeLabel)
                    { Patterns = ["*." + CueCompositionsIO.FileExtension] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var document = await CueCompositionsIO.LoadAsync(path);
            var (updated, added) = MergeCompositions(document.Compositions);
            OnPropertyChanged(nameof(VisibleCompositions));
            StatusMessage = Strings.Format(nameof(Strings.CompositionsImportedStatusFormat), updated, added);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Name-keyed composition merge (see <see cref="ImportCompositionsAsync"/>). Exposed for tests.</summary>
    internal (int Updated, int Added) MergeCompositions(IReadOnlyList<CueComposition> incoming)
    {
        var target = VisibleCompositions;
        var updated = 0;
        var added = 0;
        foreach (var comp in incoming)
        {
            var existing = target.FirstOrDefault(c =>
                string.Equals(c.Name, comp.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // Keep the Id: cue video placements reference compositions by id.
                existing.Width = comp.Width;
                existing.Height = comp.Height;
                existing.FrameRateNum = comp.FrameRateNum;
                existing.FrameRateDen = comp.FrameRateDen;
                updated++;
                continue;
            }

            var model = target.Any(c => c.Id == comp.Id) ? comp with { Id = Guid.NewGuid() } : comp;
            target.Add(CueCompositionViewModel.FromModel(model));
            added++;
        }

        return (updated, added);
    }

    [RelayCommand]
    private Task SaveCueListAsync() =>
        SelectedCueList is { Path: { } path } ? SaveCueListToPathAsync(path) : SaveCueListAsAsync();

    [RelayCommand]
    private async Task SaveCueListAsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null || SelectedCueList is null) return;

        var opts = new FilePickerSaveOptions
        {
            Title = Strings.SaveCueListDialogTitle,
            DefaultExtension = CueListIO.FileExtension,
            SuggestedFileName = string.IsNullOrWhiteSpace(SelectedCueList.Path)
                ? Strings.Format(nameof(Strings.CueListDefaultFileNameFormat), SanitizeFileName(SelectedCueList.Name), CueListIO.FileExtension)
                : Path.GetFileName(SelectedCueList.Path),
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayCueListFileTypeLabel) { Patterns = ["*." + CueListIO.FileExtension] },
            ],
        };
        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        if (picked is null) return;
        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        await SaveCueListToPathAsync(path);
    }

    private async Task SaveCueListToPathAsync(string path)
    {
        if (SelectedCueList is null)
            return;
        try
        {
            await CueListIO.SaveAsync(SelectedCueList.ToModel(), path);
            SelectedCueList.Path = path;
            OnPropertyChanged(nameof(DisplayedCueFilePath));
            StatusMessage = Strings.Format(nameof(Strings.SavedCueListStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.CueListSaveFailedStatusFormat), ex.Message);
        }
    }

    [RelayCommand]
    private async Task LoadAllCueListsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var opts = new FilePickerOpenOptions
        {
            Title = Strings.OpenAllCueListsDialogTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Strings.HaPlayCueListsFileTypeLabel)
                {
                    Patterns = ["*." + CueListsIO.FileExtension],
                },
                new FilePickerFileType(Strings.JsonFileTypeLabel) { Patterns = ["*.json"] },
                new FilePickerFileType(Strings.AllFilesFileTypeLabel) { Patterns = ["*"] },
            ],
        };

        var picks = await owner.StorageProvider.OpenFilePickerAsync(opts);
        var picked = picks.FirstOrDefault();
        if (picked is null)
            return;

        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            var lists = await CueListsIO.LoadAsync(path);
            ApplyCueLists(lists, path);
            StatusMessage = Strings.Format(nameof(Strings.LoadedAllCueListsStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.AllCueListsLoadFailedStatusFormat), ex.Message);
        }
    }

    [RelayCommand]
    private Task SaveAllCueListsAsync() =>
        !string.IsNullOrEmpty(_cueListsCollectionPath)
            ? SaveAllCueListsToPathAsync(_cueListsCollectionPath)
            : SaveAllCueListsAsAsync();

    [RelayCommand]
    private async Task SaveAllCueListsAsAsync()
    {
        var owner = TryGetMainWindow();
        if (owner is null)
            return;

        var opts = new FilePickerSaveOptions
        {
            Title = Strings.SaveAllCueListsDialogTitle,
            DefaultExtension = CueListsIO.FileExtension,
            SuggestedFileName = string.IsNullOrEmpty(_cueListsCollectionPath)
                ? Strings.Format(
                    nameof(Strings.CueListsCollectionDefaultFileNameFormat),
                    Strings.CueListsCollectionFileNameFallback,
                    CueListsIO.FileExtension)
                : Path.GetFileName(_cueListsCollectionPath),
            FileTypeChoices =
            [
                new FilePickerFileType(Strings.HaPlayCueListsFileTypeLabel)
                {
                    Patterns = ["*." + CueListsIO.FileExtension],
                },
            ],
        };

        var picked = await owner.StorageProvider.SaveFilePickerAsync(opts);
        if (picked is null)
            return;

        var path = picked.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        await SaveAllCueListsToPathAsync(path);
    }

    private async Task SaveAllCueListsToPathAsync(string path)
    {
        try
        {
            await CueListsIO.SaveAsync(BuildCueListsSnapshot(), path, "HaPlay");
            _cueListsCollectionPath = path;
            OnPropertyChanged(nameof(CueListsCollectionPath));
            OnPropertyChanged(nameof(DisplayedCueFilePath));
            StatusMessage = Strings.Format(nameof(Strings.SavedAllCueListsStatusFormat), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusMessage = Strings.Format(nameof(Strings.AllCueListsSaveFailedStatusFormat), ex.Message);
        }
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Strings.CueListFileNameFallback;
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
