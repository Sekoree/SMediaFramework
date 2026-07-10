using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Resources;

namespace HaPlay.ViewModels;

/// <summary>
/// One on-disk media cache location on the Project workspace (YouTube downloads, MMD physics bakes).
/// Shows the current size, opens the folder in the OS file manager and clears the contents on demand.
/// Sizes are computed off the UI thread; a clear skips files that are currently open (e.g. an asset
/// that is playing right now) instead of failing, and reports how much space was actually freed.
/// </summary>
public sealed partial class MediaCacheViewModel : ObservableObject
{
    private readonly Func<Task>? _beforeClearAsync;

    public MediaCacheViewModel(string name, string root, string hint, Func<Task>? beforeClearAsync = null)
    {
        Name = name;
        Root = root;
        Hint = hint;
        _beforeClearAsync = beforeClearAsync;
    }

    public string Name { get; }

    /// <summary>Absolute cache directory. May not exist yet (nothing cached so far).</summary>
    public string Root { get; }

    /// <summary>One-line description of what lives here and what clearing costs (re-download / re-bake).</summary>
    public string Hint { get; }

    [ObservableProperty]
    private string _sizeText = "-";

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Recomputes <see cref="SizeText"/> from disk (off the UI thread).</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            SizeText = Strings.CacheSizeCalculatingLabel;
            var (bytes, files) = await Task.Run(() => MeasureDirectory(Root));
            SizeText = files == 0
                ? Strings.CacheEmptyLabel
                : Strings.Format(nameof(Strings.CacheSizeFormat), Dialogs.MediaPropertiesDialogViewModel.FormatSize(bytes), files);
        }
        catch (Exception ex)
        {
            SizeText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Opens the cache directory in the platform file manager (creating it first so the
    /// launcher never fails on a cold cache).</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Root);
            Process.Start(new ProcessStartInfo(Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>Deletes every file under the cache root. Files the OS refuses to delete (in use on
    /// Windows) are kept and counted; the summary reports freed space and skips. Never throws.</summary>
    [RelayCommand]
    private async Task ClearAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            if (_beforeClearAsync is not null)
                await _beforeClearAsync();

            var (freed, skipped) = await Task.Run(() => ClearDirectory(Root));
            StatusText = skipped == 0
                ? Strings.Format(nameof(Strings.CacheClearedStatusFormat), Dialogs.MediaPropertiesDialogViewModel.FormatSize(freed))
                : Strings.Format(nameof(Strings.CacheClearedWithSkipsStatusFormat),
                    Dialogs.MediaPropertiesDialogViewModel.FormatSize(freed), skipped);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
    }

    private static (long Bytes, int Files) MeasureDirectory(string root)
    {
        if (!Directory.Exists(root))
            return (0, 0);
        long bytes = 0;
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                files++;
            }
            catch
            {
                // racing a concurrent delete/rename - the entry just doesn't count
            }
        }
        return (bytes, files);
    }

    private static (long Freed, int Skipped) ClearDirectory(string root)
    {
        if (!Directory.Exists(root))
            return (0, 0);
        long freed = 0;
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var length = new FileInfo(file).Length;
                File.Delete(file);
                freed += length;
            }
            catch
            {
                skipped++; // in use (playing right now) or permission - keep it, the user sees the count
            }
        }
        // Prune now-empty subdirectories; the root itself stays for the next download.
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { Directory.Delete(dir); }
            catch { /* non-empty because a file was skipped */ }
        }
        return (freed, skipped);
    }
}
