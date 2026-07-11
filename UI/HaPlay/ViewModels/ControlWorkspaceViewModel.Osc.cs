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
/// OSC listeners, periodic OSC sends, and OSC device add/edit/remove.
/// Partial of <see cref="ControlWorkspaceViewModel"/> - split from the original single file purely
/// for navigability; no behavior differences.
/// </summary>
public partial class ControlWorkspaceViewModel
{
    // ----- OSC listener add/edit/remove -------------------------------------------------------
    // App-level inbound OSC ports for external control sources (device replies use the client socket and
    // need none). Structural, so add/edit/remove ask for a re-arm while armed. Display is injectable for tests.

    internal Func<OSCListenerDialogViewModel, Task<bool>> OSCListenerPrompt { get; set; } = DefaultOSCListenerPromptAsync;

    [RelayCommand]
    private async Task AddOSCListenerAsync()
    {
        var nextPort = _config.OSCListeners.Count == 0 ? 10020 : _config.OSCListeners.Max(l => l.LocalPort) + 1;
        var dialog = new OSCListenerDialogViewModel(
            "Add OSC listener",
            name: $"OSC Listener {(_config.OSCListeners.Count + 1).ToString(CultureInfo.InvariantCulture)}",
            localPort: nextPort,
            isEnabled: true);
        if (!await OSCListenerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var listeners = _config.OSCListeners.ToList();
        listeners.Add(new ControlOSCListenerConfig
        {
            Name = values.Name,
            LocalPort = values.LocalPort,
            IsEnabled = values.IsEnabled,
        });
        _config = _config with { OSCListeners = listeners };
        RefreshAfterListenerChange();
        StatusMessage = $"Added OSC listener '{values.Name}' on port {values.LocalPort.ToString(CultureInfo.InvariantCulture)}."
                        + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private async Task EditOSCListenerAsync(ControlStructureRowViewModel row)
    {
        if (row.OSCListenerId is not { } listenerId)
            return;

        var existing = _config.OSCListeners.FirstOrDefault(l => l.Id == listenerId);
        if (existing is null)
            return;

        var dialog = new OSCListenerDialogViewModel("Edit OSC listener", existing.Name, existing.LocalPort, existing.IsEnabled);
        if (!await OSCListenerPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        _config = _config with
        {
            OSCListeners = _config.OSCListeners
                .Select(l => l.Id == listenerId
                    ? l with { Name = values.Name, LocalPort = values.LocalPort, IsEnabled = values.IsEnabled }
                    : l)
                .ToList(),
        };
        RefreshAfterListenerChange();
        StatusMessage = $"Updated OSC listener '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RemoveOSCListener(ControlStructureRowViewModel row)
    {
        if (row.OSCListenerId is not { } listenerId)
            return;

        var existing = _config.OSCListeners.FirstOrDefault(l => l.Id == listenerId);
        if (existing is null)
            return;

        // A dangling endpoint id simply makes any endpoint-scoped script inert (no event will match it),
        // so there's nothing unsafe to clean up here - just drop the listener.
        _config = _config with { OSCListeners = _config.OSCListeners.Where(l => l.Id != listenerId).ToList() };
        RefreshAfterListenerChange();
        StatusMessage = $"Removed OSC listener '{existing.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RefreshAfterListenerChange()
    {
        RebuildStructureRows();
        RebuildProfileWarnings();
        NotifySummary();
    }

    private static async Task<bool> DefaultOSCListenerPromptAsync(OSCListenerDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new OSCListenerDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    // ----- Periodic OSC send add/edit/remove --------------------------------------------------

    internal Func<PeriodicSendDialogViewModel, Task<bool>> PeriodicSendPrompt { get; set; } = DefaultPeriodicSendPromptAsync;

    private async Task AddPeriodicSendAsync(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId)
            return;

        var dialog = new PeriodicSendDialogViewModel("Add periodic OSC send", "/xremote", "/xremote", 8000, isEnabled: true);
        if (!await PeriodicSendPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        if (!TryUpdateDevice(deviceId, device => device with
        {
            PeriodicOSCSends =
            [
                .. device.PeriodicOSCSends,
                new ControlPeriodicOSCSendConfig
                {
                    Name = values.Name,
                    Address = values.Address,
                    IntervalMs = values.IntervalMs,
                    IsEnabled = values.IsEnabled,
                },
            ],
        }))
            return;

        StatusMessage = $"Added periodic send '{values.Name}' every {values.IntervalMs} ms." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private async Task EditPeriodicSendAsync(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId || row.PeriodicSendId is not { } sendId)
            return;

        var existing = _config.Devices.FirstOrDefault(d => d.Id == deviceId)?
            .PeriodicOSCSends.FirstOrDefault(s => s.Id == sendId);
        if (existing is null)
            return;

        var dialog = new PeriodicSendDialogViewModel(
            "Edit periodic OSC send", existing.Name, existing.Address, existing.IntervalMs, existing.IsEnabled);
        if (!await PeriodicSendPrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        if (!TryUpdateDevice(deviceId, device => device with
        {
            PeriodicOSCSends = device.PeriodicOSCSends
                .Select(s => s.Id == sendId
                    ? s with { Name = values.Name, Address = values.Address, IntervalMs = values.IntervalMs, IsEnabled = values.IsEnabled }
                    : s)
                .ToList(),
        }))
            return;

        StatusMessage = $"Updated periodic send '{values.Name}' every {values.IntervalMs} ms." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RemovePeriodicSend(ControlStructureRowViewModel row)
    {
        if (row.DeviceInstanceId is not { } deviceId || row.PeriodicSendId is not { } sendId)
            return;

        if (TryUpdateDevice(deviceId, device => device with
        {
            PeriodicOSCSends = device.PeriodicOSCSends.Where(s => s.Id != sendId).ToList(),
        }))
        {
            StatusMessage = "Removed periodic send." + (IsArmed ? " Re-arm to apply." : string.Empty);
        }
    }

    private bool TryUpdateDevice(Guid deviceId, Func<ControlDeviceInstanceConfig, ControlDeviceInstanceConfig> update)
    {
        var devices = _config.Devices.ToList();
        var index = devices.FindIndex(d => d.Id == deviceId);
        if (index < 0)
            return false;

        devices[index] = update(devices[index]);
        _config = _config with { Devices = devices };
        RefreshAfterDeviceChange();
        return true;
    }

    private static async Task<bool> DefaultPeriodicSendPromptAsync(PeriodicSendDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new PeriodicSendDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    private ControlStructureRowCommands BuildStructureRowCommands() => new(
        AddScript,
        AddHelperScript,
        AddDeviceScript,
        AddEndpointScript,
        AddLayerScript,
        ActivateLayer,
        () => _ = AddLayerAsync(),
        row => _ = EditLayerAsync(row),
        RemoveLayer,
        row => _ = AddPeriodicSendAsync(row),
        row => _ = EditPeriodicSendAsync(row),
        RemovePeriodicSend,
        row => _ = EditOSCDeviceInternalAsync(FindOSCDevice(row)),
        RemoveOSCDevice,
        row => _ = TestOSCDeviceAsync(row),
        row => _ = TestMIDIDeviceAsync(row),
        () => _ = AddOSCListenerAsync(),
        row => _ = EditOSCListenerAsync(row),
        RemoveOSCListener,
        row => _ = EditMIDIDeviceInternalAsync(FindMIDIDevice(row)),
        row => _ = ExportLayerAsync(row));

    // ----- OSC device add/edit/remove ---------------------------------------------------------
    // The dialog display is injectable so the add/edit logic is unit-testable without a window.

    internal Func<OSCDeviceDialogViewModel, Task<bool>> OSCDevicePrompt { get; set; } = DefaultOSCDevicePromptAsync;

    // Injectable so the MIDI device alias/profile edit logic is unit-testable without a window.
    internal Func<MIDIDeviceDialogViewModel, Task<bool>> MIDIDevicePrompt { get; set; } = DefaultMIDIDevicePromptAsync;

    [RelayCommand]
    private Task AddOSCDeviceAsync() => EditOSCDeviceInternalAsync(existing: null);

    private ControlDeviceInstanceConfig? FindOSCDevice(ControlStructureRowViewModel row) =>
        row.DeviceInstanceId is { } id
            ? _config.Devices.FirstOrDefault(d => d.Id == id && d.Protocol == ControlDeviceProtocol.OSC)
            : null;

    private async Task EditOSCDeviceInternalAsync(ControlDeviceInstanceConfig? existing)
    {
        var isAdd = existing is null;
        var profileRepository = CompositeControlDeviceProfileRepository.ForProject(_config);
        var profiles = profileRepository.Profiles
            .Where(p => p.Protocol == ControlDeviceProtocol.OSC)
            .ToList();
        var defaultProfile = profiles.FirstOrDefault(p => p.Id == DefaultX32ProfileId) ?? profiles.FirstOrDefault();
        var defaultProfileId = defaultProfile?.Id;

        var dialog = new OSCDeviceDialogViewModel(
            isAdd ? "Add OSC device" : "Edit OSC device",
            name: existing?.Name ?? "X32",
            profileId: existing?.ProfileId is { Length: > 0 } pid ? pid : defaultProfileId,
            host: string.IsNullOrWhiteSpace(existing?.Binding.OSCHost) ? "192.168.2.76" : existing!.Binding.OSCHost!,
            port: existing?.Binding.OSCPort ?? defaultProfile?.DefaultOSCPort ?? 10023,
            alias: existing?.Binding.Alias ?? (isAdd ? "x32" : null),
            localPort: existing?.Binding.OSCLocalPort,
            isEnabled: existing?.IsEnabled ?? true,
            oscProfiles: profiles);

        if (!await OSCDevicePrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var selectedProfile = profileRepository.FindById(values.ProfileId ?? string.Empty);
        var devices = _config.Devices.ToList();
        if (existing is null)
        {
            devices.Add(new ControlDeviceInstanceConfig
            {
                Name = values.Name,
                ProfileId = values.ProfileId!,
                Protocol = ControlDeviceProtocol.OSC,
                IsEnabled = values.IsEnabled,
                Binding = new ControlDeviceBindingConfig
                {
                    Alias = values.Alias,
                    OSCHost = values.Host,
                    OSCPort = values.Port,
                    OSCLocalPort = values.LocalPort,
                },
                PeriodicOSCSends = ControlDeviceProfileSeeding.CreateDefaultPeriodicOSCSends(selectedProfile),
            });
            StatusMessage = $"Added OSC device '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
        }
        else
        {
            var index = devices.FindIndex(d => d.Id == existing.Id);
            if (index < 0)
                return;

            devices[index] = existing with
            {
                Name = values.Name,
                ProfileId = values.ProfileId!,
                IsEnabled = values.IsEnabled,
                Binding = existing.Binding with
                {
                    Alias = values.Alias,
                    OSCHost = values.Host,
                    OSCPort = values.Port,
                    OSCLocalPort = values.LocalPort,
                },
            };
            StatusMessage = $"Updated OSC device '{values.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
        }

        _config = _config with { Devices = devices };
        RefreshAfterDeviceChange();
    }

    private void RemoveOSCDevice(ControlStructureRowViewModel row)
    {
        var device = FindOSCDevice(row);
        if (device is null)
            return;

        _config = _config with { Devices = _config.Devices.Where(d => d.Id != device.Id).ToList() };
        RefreshAfterDeviceChange();
        StatusMessage = $"Removed OSC device '{device.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private void RefreshAfterDeviceChange()
    {
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);
        NotifySummary();
    }
}
