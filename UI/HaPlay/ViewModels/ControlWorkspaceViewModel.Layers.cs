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
using HaPlay.Models;
using HaPlay.Resources;
using HaPlay.Services;
using HaPlay.ViewModels.Dialogs;
using HaPlay.Views.Dialogs;
using OSCLib;

namespace HaPlay.ViewModels;

/// <summary>
/// Layer activation and layer add/edit/remove.
/// Partial of <see cref="ControlWorkspaceViewModel"/> - split from the original single file purely
/// for navigability; no behavior differences.
/// </summary>
public partial class ControlWorkspaceViewModel
{
    // ----- Layer activation -------------------------------------------------------------------
    // Layers are mutually exclusive: activating one deactivates the rest. The config flag drives the
    // structure view; the live session also switches so LayerEnabled/LayerDisabled triggers fire.

    private void ActivateLayer(ControlStructureRowViewModel row)
    {
        if (row.LayerId is not { } layerId)
            return;

        _config = WithActiveLayer(_config, layerId);
        RebuildStructureRows();
        NotifySummary();

        if (_session is not null)
        {
            _ = ActivateLayerLiveAsync(layerId);
        }
        else
        {
            var layer = _config.Layers.FirstOrDefault(l => l.Id == layerId);
            StatusMessage = $"Activated layer '{layer?.Name}'. Arm to run its scripts.";
        }
    }

    private async Task ActivateLayerLiveAsync(Guid layerId)
    {
        var session = _session;
        if (session is null)
            return;

        try
        {
            await session.EventQueue.SetActiveLayerAsync(layerId).ConfigureAwait(true);
            var layer = _config.Layers.FirstOrDefault(l => l.Id == layerId);
            StatusMessage = $"Activated layer '{layer?.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Layer activate error: {ex.Message}";
        }
    }

    // ----- Layer add/edit/remove --------------------------------------------------------------
    // Layers are structural: the live runtime snapshots them at arm time, so add/edit/remove ask for a
    // re-arm. The dialog display is injectable so the logic is unit-testable without a real window.

    internal Func<LayerDialogViewModel, Task<bool>> LayerPrompt { get; set; } = DefaultLayerPromptAsync;

    [RelayCommand]
    private async Task AddLayerAsync()
    {
        var hasActive = _config.Layers.Any(l => l.IsEnabled);
        var nextPriority = _config.Layers.Count == 0 ? 0 : _config.Layers.Max(l => l.Priority) + 1;
        var dialog = new LayerDialogViewModel(
            "Add layer",
            name: $"Layer {(_config.Layers.Count + 1).ToString(CultureInfo.InvariantCulture)}",
            priority: nextPriority,
            isActive: !hasActive);
        if (!await LayerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var layer = new ControlLayerConfig
        {
            Name = values.Name,
            Priority = values.Priority,
            IsEnabled = values.IsActive,
        };

        var layers = _config.Layers.ToList();
        layers.Add(layer);
        if (values.IsActive)
            layers = ApplyExclusiveActive(layers, layer.Id);

        _config = _config with { Layers = layers };
        RefreshAfterLayerChange();
        StatusMessage = $"Added layer '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private async Task EditLayerAsync(ControlStructureRowViewModel row)
    {
        if (row.LayerId is not { } layerId)
            return;

        var existing = _config.Layers.FirstOrDefault(l => l.Id == layerId);
        if (existing is null)
            return;

        var dialog = new LayerDialogViewModel("Edit layer", existing.Name, existing.Priority, existing.IsEnabled);
        if (!await LayerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var layers = _config.Layers
            .Select(l => l.Id == layerId
                ? l with { Name = values.Name, Priority = values.Priority, IsEnabled = values.IsActive }
                : l)
            .ToList();
        if (values.IsActive)
            layers = ApplyExclusiveActive(layers, layerId);

        _config = _config with { Layers = layers };
        RefreshAfterLayerChange();
        StatusMessage = $"Updated layer '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RemoveLayer(ControlStructureRowViewModel row)
    {
        if (row.LayerId is not { } layerId)
            return;

        var existing = _config.Layers.FirstOrDefault(l => l.Id == layerId);
        if (existing is null)
            return;

        var affected = _config.Scripts.Count(s => s.LayerId == layerId);
        _config = _config with { Layers = _config.Layers.Where(l => l.Id != layerId).ToList() };

        // Clear references on layer-scoped scripts so a dangling layer id doesn't silently disable them.
        // Going through the row propagates the change back into the config via the row-changed callback.
        foreach (var scriptRow in ScriptRows.ToList())
            scriptRow.OnLayerRemoved(layerId);

        RefreshAfterLayerChange();
        var suffix = affected > 0
            ? $" {affected.ToString(CultureInfo.InvariantCulture)} script(s) unbound from it."
            : string.Empty;
        StatusMessage = $"Removed layer '{existing.Name}'.{suffix}" + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    // Layers are mutually exclusive: exactly the layer with <paramref name="activeId"/> stays enabled.
    private static List<ControlLayerConfig> ApplyExclusiveActive(IEnumerable<ControlLayerConfig> layers, Guid activeId) =>
        layers.Select(l => l with { IsEnabled = l.Id == activeId }).ToList();

    /// <summary>
    /// Returns a config copy whose layer <see cref="ControlLayerConfig.IsEnabled"/> flags reflect the
    /// active layer. Returns <paramref name="config"/> unchanged when already in sync.
    /// </summary>
    internal static ControlSystemConfig WithActiveLayer(ControlSystemConfig config, Guid? activeLayerId)
    {
        ArgumentNullException.ThrowIfNull(config);

        var currentActiveId = config.Layers.FirstOrDefault(l => l.IsEnabled)?.Id;
        if (activeLayerId == currentActiveId)
            return config;

        if (activeLayerId is null)
        {
            if (config.Layers.All(l => !l.IsEnabled))
                return config;

            return config with
            {
                Layers = config.Layers.Select(l => l with { IsEnabled = false }).ToList(),
            };
        }

        return config with { Layers = ApplyExclusiveActive(config.Layers, activeLayerId.Value) };
    }

    /// <summary>
    /// Keeps structure-view layer state aligned with the live runtime when scripts or devices switch layers.
    /// </summary>
    private void SyncActiveLayerFromSession()
    {
        var session = _session;
        if (session is null)
            return;

        var updated = WithActiveLayer(_config, session.ScriptSession.ActiveLayerId);
        if (ReferenceEquals(updated, _config))
            return;

        _config = updated;
        RebuildStructureRows();
    }

    private void RefreshAfterLayerChange()
    {
        RebuildStructureRows();
        ApplyLayerOptionsToScriptRows();
        NotifySummary();
    }

    private IReadOnlyList<ControlLayerOption> BuildLayerOptions() =>
        _config.Layers
            .OrderBy(l => l.Priority)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l => new ControlLayerOption(l.Id, string.IsNullOrWhiteSpace(l.Name) ? "(unnamed layer)" : l.Name))
            .ToArray();

    private void ApplyLayerOptionsToScriptRows()
    {
        var options = BuildLayerOptions();
        foreach (var row in ScriptRows)
            row.SetLayerOptions(options);
    }

    private static async Task<bool> DefaultLayerPromptAsync(LayerDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new LayerDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }
}
