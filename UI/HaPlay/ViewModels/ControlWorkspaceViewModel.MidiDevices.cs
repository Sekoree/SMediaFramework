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
/// MIDI device edit (alias + profile).
/// Partial of <see cref="ControlWorkspaceViewModel"/> - split from the original single file purely
/// for navigability; no behavior differences.
/// </summary>
public partial class ControlWorkspaceViewModel
{
    // ----- MIDI device edit (alias + profile) -------------------------------------------------
    // MIDI ports are bound from the MIDI Devices view; this only edits the script alias, the assigned
    // profile (e.g. the BCF2000 profile that enables 14-bit CC pairing), and the enabled state.

    private ControlDeviceInstanceConfig? FindMIDIDevice(ControlStructureRowViewModel row) =>
        row.DeviceInstanceId is { } id
            ? _config.Devices.FirstOrDefault(d => d.Id == id && d.Protocol == ControlDeviceProtocol.MIDI)
            : null;

    private async Task EditMIDIDeviceInternalAsync(ControlDeviceInstanceConfig? existing)
    {
        if (existing is null)
            return;

        var midiProfiles = CompositeControlDeviceProfileRepository.ForProject(_config).Profiles
            .Where(p => p.Protocol == ControlDeviceProtocol.MIDI)
            .ToList();

        var dialog = new MIDIDeviceDialogViewModel(
            "Edit MIDI device",
            deviceName: existing.Binding.MIDIInputDeviceName ?? existing.Binding.MIDIOutputDeviceName ?? existing.Name,
            profileId: existing.ProfileId,
            alias: existing.Binding.Alias,
            isEnabled: existing.IsEnabled,
            midiProfiles: midiProfiles);

        if (!await MIDIDevicePrompt(dialog).ConfigureAwait(true))
            return;

        var values = dialog.BuildValues();
        var devices = _config.Devices.ToList();
        var index = devices.FindIndex(d => d.Id == existing.Id);
        if (index < 0)
            return;

        devices[index] = existing with
        {
            ProfileId = values.ProfileId,
            IsEnabled = values.IsEnabled,
            Binding = existing.Binding with { Alias = values.Alias },
        };

        _config = _config with { Devices = devices };
        RefreshAfterDeviceChange();
        StatusMessage = $"Updated MIDI device '{values.Alias ?? existing.Name}'." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private static async Task<bool> DefaultMIDIDevicePromptAsync(MIDIDeviceDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new MIDIDeviceDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var path = await ProfileImportPathPrompt().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var profile = ImportProfileFromFile(path);
            StatusMessage = $"Imported project profile '{FormatProfileName(profile)}'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"Profile import failed: {ex.Message}";
        }
    }

    internal ControlDeviceProfile ImportProfileFromFile(string path)
    {
        var profile = DirectoryControlDeviceProfileRepository.LoadProfileFile(path);
        UpsertProjectProfile(profile);
        return profile;
    }

    [RelayCommand]
    private void SaveMIDIProfileBuilder()
    {
        try
        {
            var profile = BuildMIDIProfileFromBuilder();
            UpsertProjectProfile(profile);
            ProfileBuilderStatus = $"Saved project profile '{FormatProfileName(profile)}'.";
            StatusMessage = ProfileBuilderStatus;
        }
        catch (InvalidOperationException ex)
        {
            ProfileBuilderStatus = ex.Message;
            StatusMessage = $"Profile builder: {ex.Message}";
        }
    }

    internal ControlDeviceProfile BuildMIDIProfileFromBuilder()
    {
        var displayName = ProfileBuilderDisplayName.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            throw new InvalidOperationException("Profile name is required.");

        var profileId = string.IsNullOrWhiteSpace(ProfileBuilderId)
            ? $"custom.midi.{SanitizeIdPart(displayName)}"
            : NormalizeProfileId(ProfileBuilderId);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException("Profile id is required.");

        var controlName = ProfileBuilderControlName.Trim();
        if (string.IsNullOrWhiteSpace(controlName))
            throw new InvalidOperationException("Control name is required.");

        var channel = ParseRequiredInt(ProfileBuilderMIDIChannelText, "MIDI channel");
        if (channel is < 1 or > 16)
            throw new InvalidOperationException("MIDI channel must be between 1 and 16.");

        var controller = ParseRequiredInt(ProfileBuilderMIDIControllerText, "CC");
        if (controller is < 0 or > 127)
            throw new InvalidOperationException("CC must be between 0 and 127.");

        var minValue = ParseOptionalIntForProfile(ProfileBuilderMinValueText, "Minimum value");
        var maxValue = ParseOptionalIntForProfile(ProfileBuilderMaxValueText, "Maximum value");
        if (minValue.HasValue && maxValue.HasValue && minValue.Value > maxValue.Value)
            throw new InvalidOperationException("Minimum value must be less than or equal to maximum value.");

        var controlId = SanitizeIdPart(controlName);
        if (string.IsNullOrWhiteSpace(controlId))
            controlId = $"cc{controller.ToString(CultureInfo.InvariantCulture)}";

        var profile = new ControlDeviceProfile
        {
            Id = profileId,
            DisplayName = displayName,
            Protocol = ControlDeviceProtocol.MIDI,
            Ports =
            [
                new ControlDevicePortProfile
                {
                    Id = "midi.in",
                    DisplayName = "MIDI In",
                    Kind = ControlDevicePortKind.MIDIInput,
                },
                new ControlDevicePortProfile
                {
                    Id = "midi.out",
                    DisplayName = "MIDI Out",
                    Kind = ControlDevicePortKind.MIDIOutput,
                },
            ],
            Controls =
            [
                new ControlControlProfile
                {
                    Id = controlId,
                    DisplayName = controlName,
                    Kind = ControlProfileControlKind.Fader,
                    MIDIChannel = channel,
                    MIDIController = controller,
                    ValueMode = ProfileBuilderHighResolution14Bit
                        ? ControlProfileValueMode.Absolute14Bit
                        : ControlProfileValueMode.Absolute7Bit,
                    MIDIHighResolution14Bit = ProfileBuilderHighResolution14Bit,
                    MIDIValueMin = minValue,
                    MIDIValueMax = maxValue,
                },
            ],
        };

        var issues = ControlDeviceProfileValidator.Validate(profile);
        if (issues.Count > 0)
            throw new InvalidOperationException(string.Join("; ", issues.Select(issue => issue.Message)));

        ProfileBuilderId = profile.Id;
        return profile;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProfileRow))]
    private async Task ExportSelectedProfileAsync()
    {
        var row = SelectedProfileRow;
        if (row is null)
            return;

        var directory = await ProfileExportDirectoryPrompt().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            var path = ExportProfileToDirectory(row, directory);
            StatusMessage = $"Exported profile '{row.DisplayName}' to {path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"Profile export failed: {ex.Message}";
        }
    }

    internal string ExportProfileToDirectory(ControlProfileRowViewModel row, string directory)
    {
        ArgumentNullException.ThrowIfNull(row);

        var repository = CompositeControlDeviceProfileRepository.ForProject(_config);
        var profile = repository.FindById(row.Id)
            ?? throw new InvalidOperationException($"Profile '{row.Id}' is not available.");
        return DirectoryControlDeviceProfileRepository.SaveProfile(directory, profile);
    }

    [RelayCommand]
    private async Task ExportBuiltInProfilesAsync()
    {
        var directory = await ProfileExportDirectoryPrompt().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        try
        {
            var paths = DirectoryControlDeviceProfileRepository.ExportBuiltInProfiles(directory);
            StatusMessage = $"Exported {paths.Count.ToString(CultureInfo.InvariantCulture)} built-in profile(s).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"Profile export failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedProjectProfile))]
    private void RemoveSelectedProjectProfile()
    {
        var row = SelectedProfileRow;
        if (row is not { IsProjectOverride: true })
            return;

        _config = _config with
        {
            DeviceProfileOverrides = _config.DeviceProfileOverrides
                .Where(profile => !string.Equals(profile.Id, row.Id, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);
        StatusMessage = $"Removed project profile '{row.DisplayName}'.";
    }

    private void UpsertProjectProfile(ControlDeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var overrides = _config.DeviceProfileOverrides
            .Where(existing => !string.Equals(existing.Id, profile.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        overrides.Add(profile);
        _config = _config with { DeviceProfileOverrides = overrides };
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);
        SelectedProfileRow = ProfileRows.FirstOrDefault(row =>
            string.Equals(row.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatProfileName(ControlDeviceProfile profile) =>
        string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName;

    private static int ParseRequiredInt(string? text, string label) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{label} must be a whole number.");

    private static int? ParseOptionalIntForProfile(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{label} must be a whole number.");
    }

    private static string NormalizeProfileId(string text)
    {
        var parts = text
            .Trim()
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeIdPart)
            .Where(part => part.Length > 0);
        return string.Join('.', parts);
    }

    private static string SanitizeIdPart(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var previousSeparator = false;
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousSeparator = false;
            }
            else if (!previousSeparator && builder.Length > 0)
            {
                builder.Append('-');
                previousSeparator = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static async Task<bool> DefaultOSCDevicePromptAsync(OSCDeviceDialogViewModel dialogViewModel)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return false;

        var dialog = new OSCDeviceDialog { DataContext = dialogViewModel };
        return await dialog.ShowDialog<bool>(owner).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedScript))]
    private void RemoveSelectedScript()
    {
        var target = SelectedScriptRow;
        if (target is null)
            return;

        _config = _config with { Scripts = _config.Scripts.Where(s => s.Id != target.Script.Id).ToList() };
        RebuildScriptRows();
        RebuildStructureRows();
        NotifySummary();
        if (IsArmed)
            StatusMessage = "Script removed. Re-arm control to apply script changes.";
    }

    private void RebuildScriptRows()
    {
        var selectedId = SelectedScriptRow?.Script.Id;
        ScriptRows.Clear();
        foreach (var script in _config.Scripts)
            ScriptRows.Add(new ControlScriptRowViewModel(script, OnScriptRowChanged));
        SelectedScriptRow = selectedId is null
            ? ScriptRows.FirstOrDefault()
            : ScriptRows.FirstOrDefault(row => row.Script.Id == selectedId) ?? ScriptRows.FirstOrDefault();
        ApplyLayerOptionsToScriptRows();
    }

    private void RebuildStructureRows()
    {
        StructureRows.Clear();
        foreach (var row in BuildStructureRows(_config, BuildStructureRowCommands()))
            StructureRows.Add(row);
    }

    private void RebuildProfileWarnings()
    {
        ProfileWarnings.Clear();
        foreach (var warning in BuildProfileWarnings(_config, CompositeControlDeviceProfileRepository.ForProject(_config)))
            ProfileWarnings.Add(warning);
    }

    private void RebuildProfileRows()
    {
        var selectedId = SelectedProfileRow?.Id;
        ProfileRows.Clear();
        foreach (var row in BuildProfileRows(_config))
            ProfileRows.Add(row);
        SelectedProfileRow = !string.IsNullOrWhiteSpace(selectedId)
            ? ProfileRows.FirstOrDefault(row => string.Equals(row.Id, selectedId, StringComparison.OrdinalIgnoreCase))
            : SelectedProfileRow;
    }

    private void RebuildX32CommandRows(ControlValueCache? cache)
    {
        var selected = SelectedX32CommandRow;
        X32CommandRows.Clear();
        foreach (var row in BuildX32CommandRows(_config, CompositeControlDeviceProfileRepository.ForProject(_config), cache, X32CommandFilterText))
            X32CommandRows.Add(row);
        SelectedX32CommandRow = selected is not null
            ? X32CommandRows.FirstOrDefault(row =>
                row.DeviceInstanceId == selected.DeviceInstanceId
                && string.Equals(row.Address, selected.Address, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    internal static IReadOnlyList<ControlX32CommandRowViewModel> BuildX32CommandRows(
        ControlSystemConfig config,
        IControlDeviceProfileRepository repository,
        ControlValueCache? cache,
        string? filterText = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(repository);

        var rows = new List<ControlX32CommandRowViewModel>();
        foreach (var device in config.Devices
                     .Where(d => d.Protocol == ControlDeviceProtocol.OSC)
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var profile = repository.FindById(device.ProfileId);
            if (profile is null || profile.Protocol != ControlDeviceProtocol.OSC || profile.Commands.Count == 0)
                continue;

            foreach (var commandInfo in profile.Commands
                         .Select(command => new { Command = command, Group = GetX32CommandGroup(command) })
                         .OrderBy(info => info.Group, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(info => info.Command.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var command = commandInfo.Command;
                var cacheText = TryGetCommandCacheText(device, command, cache) ?? "(uncached)";
                var row = new ControlX32CommandRowViewModel(
                    DeviceInstanceId: device.Id,
                    DeviceName: string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name,
                    DeviceKey: GetPreferredDeviceKey(device),
                    Host: device.Binding.OSCHost?.Trim() ?? string.Empty,
                    Port: device.Binding.OSCPort,
                    Group: commandInfo.Group,
                    CommandName: string.IsNullOrWhiteSpace(command.DisplayName) ? command.Id : command.DisplayName,
                    Address: command.Address,
                    ValueKind: command.ValueKind.ToString(),
                    Access: command.Access.ToString(),
                    CacheValue: cacheText,
                    CanRequest: command.Access != ControlCommandAccess.WriteOnly
                                && !string.IsNullOrWhiteSpace(device.Binding.OSCHost)
                                && device.Binding.OSCPort is > 0);
                if (MatchesX32CommandFilter(row, filterText))
                    rows.Add(row);
            }
        }

        return rows;
    }

    private static bool MatchesX32CommandFilter(ControlX32CommandRowViewModel row, string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
            return true;

        var terms = filterText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term =>
            Contains(row.DeviceName, term)
            || Contains(row.DeviceKey, term)
            || Contains(row.Group, term)
            || Contains(row.CommandName, term)
            || Contains(row.Address, term)
            || Contains(row.ValueKind, term)
            || Contains(row.Access, term)
            || Contains(row.CacheValue, term));
    }

    private static string GetX32CommandGroup(ControlCommandProfile command)
    {
        if (!string.IsNullOrWhiteSpace(command.Id))
        {
            var idParts = command.Id.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (idParts.Length >= 3 && (string.Equals(idParts[0], "x32", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(idParts[0], "xair", StringComparison.OrdinalIgnoreCase)))
                return FormatX32CommandGroup(idParts[1], idParts[2]);
            if (idParts.Length >= 2)
                return FormatX32CommandGroup(idParts[0], idParts[1]);
            if (idParts.Length == 1)
                return ToDisplayGroup(idParts[0]);
        }

        var address = command.Address.Trim();
        var parts = address.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return FormatX32CommandGroup(parts[0], number.ToString(CultureInfo.InvariantCulture));

        if (parts.Length > 0)
            return ToDisplayGroup(parts[0]);

        return "Other";
    }

    private static string FormatX32CommandGroup(string group, string? numberText)
    {
        if (int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return group.ToLowerInvariant() switch
            {
                "ch" or "channel" => $"Channel {number:00}",
                "bus" => $"Bus {number:00}",
                "mtx" or "matrix" => $"Matrix {number:00}",
                "dca" => $"DCA {number}",
                "auxin" => $"Aux In {number:00}",
                _ => ToDisplayGroup(group),
            };
        }

        return ToDisplayGroup(group);
    }

    private static string ToDisplayGroup(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Other";

        return value.Trim().ToLowerInvariant() switch
        {
            "ch" or "channel" => "Channels",
            "bus" => "Buses",
            "mtx" or "matrix" => "Matrix",
            "dca" => "DCA",
            "main" or "lr" => "Main",
            "auxin" => "Aux In",
            "config" => "Config",
            "meters" or "meter" => "Meters",
            var text => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.Replace('-', ' ').Replace('_', ' ')),
        };
    }

    internal static IReadOnlyList<string> BuildProfileWarnings(
        ControlSystemConfig config,
        IControlDeviceProfileRepository repository)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(repository);

        var warnings = new List<string>();
        foreach (var device in config.Devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed device)" : device.Name;
            if (string.IsNullOrWhiteSpace(device.ProfileId))
            {
                if (device.ProfileMode == ControlDeviceProfileMode.Required)
                    warnings.Add($"{name}: required profile is not set.");
                continue;
            }

            var profile = repository.FindById(device.ProfileId);
            if (profile is null)
            {
                warnings.Add($"{name}: profile '{device.ProfileId}' is not installed; raw {device.Protocol} scripting is still available.");
                continue;
            }

            if (profile.Protocol != device.Protocol)
            {
                warnings.Add($"{name}: profile '{profile.DisplayName}' is {profile.Protocol}, but device is {device.Protocol}.");
            }
        }

        var listenerPorts = config.OSCListeners
            .Where(l => l.IsEnabled)
            .Select(l => l.LocalPort)
            .ToHashSet();
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.OSC && d.Binding.OSCLocalPort is > 0))
        {
            var localPort = device.Binding.OSCLocalPort!.Value;
            if (!listenerPorts.Contains(localPort))
                continue;

            var name = string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name;
            warnings.Add($"{name}: client source port {localPort} matches an enabled OSC listener; use blank/automatic or another port.");
        }

        return warnings;
    }

    internal static IReadOnlyList<ControlProfileRowViewModel> BuildProfileRows(
        IControlDeviceProfileRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return BuildProfileRowsFromProfiles(repository.Profiles.Select(profile =>
            new ProfileSource(profile, "Installed", IsProjectOverride: false)));
    }

    internal static IReadOnlyList<ControlProfileRowViewModel> BuildProfileRows(
        ControlSystemConfig config,
        IControlDeviceProfileRepository? appRepository = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var profiles = new Dictionary<string, ProfileSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in BuiltInControlDeviceProfileRepository.Instance.Profiles)
            AddProfileSource(profiles, profile, "Built-in", isProjectOverride: false);
        if (appRepository is not null)
        {
            foreach (var profile in appRepository.Profiles)
                AddProfileSource(profiles, profile, "App", isProjectOverride: false);
        }

        foreach (var profile in config.DeviceProfileOverrides)
            AddProfileSource(profiles, profile, "Project", isProjectOverride: true);

        return BuildProfileRowsFromProfiles(profiles.Values);
    }

    private static void AddProfileSource(
        Dictionary<string, ProfileSource> profiles,
        ControlDeviceProfile profile,
        string source,
        bool isProjectOverride)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
            return;

        profiles[profile.Id] = new ProfileSource(profile, source, isProjectOverride);
    }

    private static IReadOnlyList<ControlProfileRowViewModel> BuildProfileRowsFromProfiles(
        IEnumerable<ProfileSource> profiles) =>
        profiles
            .OrderBy(p => p.Profile.Protocol)
            .ThenBy(p => p.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(source =>
            {
                var profile = source.Profile;
                var summary = string.Join(
                    ", ",
                    new[]
                    {
                        FormatProfileCount(profile.Ports.Count, "port"),
                        FormatProfileCount(profile.Controls.Count, "control"),
                        FormatProfileCount(profile.Commands.Count, "command"),
                        FormatProfileCount(profile.Tasks.Count, "task"),
                    }.OfType<string>());
                return new ControlProfileRowViewModel(
                    string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Id : profile.DisplayName,
                    profile.Id,
                    profile.Protocol.ToString(),
                    source.Source,
                    string.IsNullOrWhiteSpace(summary) ? "No mapped controls, commands, or tasks." : summary,
                    source.IsProjectOverride);
            })
            .ToArray();

    private sealed record ProfileSource(ControlDeviceProfile Profile, string Source, bool IsProjectOverride);

    private static string? FormatProfileCount(int count, string label) =>
        count == 0 ? null : $"{count.ToString(CultureInfo.InvariantCulture)} {label}{(count == 1 ? string.Empty : "s")}";

    internal static IReadOnlyList<ControlStructureRowViewModel> BuildStructureRows(
        ControlSystemConfig config,
        ControlStructureRowCommands? commands = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rows = new List<ControlStructureRowViewModel>();

        AddGroup(rows, "MIDI devices", config.Devices.Count(d => d.Protocol == ControlDeviceProtocol.MIDI), commands);
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.MIDI).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "MIDI",
                string.IsNullOrWhiteSpace(device.Name) ? "(unnamed MIDI)" : device.Name,
                FormatMIDIBinding(device.Binding),
                FormatEnabled(device.IsEnabled),
                Level: 1,
                deviceInstanceId: device.Id,
                protocol: ControlDeviceProtocol.MIDI,
                commands: commands));
        }

        AddGroup(rows, "OSC listeners", config.OSCListeners.Count, commands);
        foreach (var listener in config.OSCListeners.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "Listen",
                string.IsNullOrWhiteSpace(listener.Name) ? "(unnamed listener)" : listener.Name,
                $"port {listener.LocalPort.ToString(CultureInfo.InvariantCulture)} - {listener.SocketMode}",
                FormatEnabled(listener.IsEnabled),
                Level: 1,
                oscListenerId: listener.Id,
                commands: commands));
        }

        AddGroup(rows, "OSC devices", config.Devices.Count(d => d.Protocol == ControlDeviceProtocol.OSC), commands);
        foreach (var device in config.Devices.Where(d => d.Protocol == ControlDeviceProtocol.OSC).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "OSC",
                string.IsNullOrWhiteSpace(device.Name) ? "(unnamed OSC)" : device.Name,
                FormatOSCBinding(device.Binding),
                FormatEnabled(device.IsEnabled),
                Level: 1,
                deviceInstanceId: device.Id,
                protocol: ControlDeviceProtocol.OSC,
                commands: commands));
        }

        AddGroup(rows, "Layers", config.Layers.Count, commands);
        foreach (var layer in config.Layers.OrderBy(l => l.Priority).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var layerScriptCount = config.Scripts.Count(s => s.Scope == ControlScriptScope.Layer && s.LayerId == layer.Id);
            rows.Add(new ControlStructureRowViewModel(
                "Layer",
                string.IsNullOrWhiteSpace(layer.Name) ? "(unnamed layer)" : layer.Name,
                $"priority {layer.Priority.ToString(CultureInfo.InvariantCulture)} - {layerScriptCount.ToString(CultureInfo.InvariantCulture)} script(s)",
                layer.IsEnabled ? "active" : "inactive",
                Level: 1,
                layerId: layer.Id,
                commands: commands));
        }

        AddGroup(rows, "Scripts", config.Scripts.Count, commands);
        foreach (var script in config.Scripts.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new ControlStructureRowViewModel(
                "Script",
                string.IsNullOrWhiteSpace(script.Name) ? "(unnamed script)" : script.Name,
                $"{script.Scope} - {FormatScriptPath(script.ScriptPath)} - {script.Triggers.Count.ToString(CultureInfo.InvariantCulture)} trigger(s)",
                FormatEnabled(script.IsEnabled),
                Level: 1,
                commands: commands));
        }

        var periodic = config.Devices
            .Where(d => d.Protocol == ControlDeviceProtocol.OSC)
            .SelectMany(d => d.PeriodicOSCSends.Select(s => (Device: d, Send: s)))
            .OrderBy(x => x.Device.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Send.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AddGroup(rows, "Periodic sends", periodic.Count, commands);
        foreach (var item in periodic)
        {
            rows.Add(new ControlStructureRowViewModel(
                "Periodic",
                string.IsNullOrWhiteSpace(item.Send.Name) ? item.Send.Address : item.Send.Name,
                $"{item.Device.Name}: {item.Send.Address} every {item.Send.IntervalMs.ToString(CultureInfo.InvariantCulture)} ms",
                FormatEnabled(item.Send.IsEnabled && item.Device.IsEnabled),
                Level: 1,
                deviceInstanceId: item.Device.Id,
                periodicSendId: item.Send.Id,
                protocol: ControlDeviceProtocol.OSC,
                commands: commands));
        }

        return rows;
    }

    private static string? TryGetCommandCacheText(
        ControlDeviceInstanceConfig device,
        ControlCommandProfile command,
        ControlValueCache? cache)
    {
        if (cache is null || string.IsNullOrWhiteSpace(command.Address))
            return null;

        foreach (var key in GetDeviceCacheKeys(device))
        {
            if (!cache.TryGet(new ControlValueCacheKey(key, command.Address), out var entry) || entry.IsStale)
                continue;

            return FormatCachedValue(entry);
        }

        return null;
    }

    private static IEnumerable<string> GetDeviceCacheKeys(ControlDeviceInstanceConfig device)
    {
        yield return device.Id.ToString();
        if (!string.IsNullOrWhiteSpace(device.Name))
            yield return device.Name;
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            yield return device.Binding.Alias;
        if (!string.IsNullOrWhiteSpace(device.ProfileId))
            yield return device.ProfileId;
    }

    private static string GetPreferredDeviceKey(ControlDeviceInstanceConfig device)
    {
        if (!string.IsNullOrWhiteSpace(device.Binding.Alias))
            return device.Binding.Alias;
        if (!string.IsNullOrWhiteSpace(device.Name))
            return device.Name;
        return device.Id.ToString();
    }

    private static string FormatCachedValue(ControlValueCacheEntry entry)
    {
        var value = entry.Value.Kind switch
        {
            ControlCachedValueKind.Number => entry.Value.NumberValue.ToString("0.###", CultureInfo.InvariantCulture),
            ControlCachedValueKind.String => entry.Value.StringValue ?? string.Empty,
            ControlCachedValueKind.Boolean => entry.Value.BooleanValue ? "true" : "false",
            _ => string.Empty,
        };
        return $"{value} ({entry.Source}, {entry.Timestamp.ToLocalTime():HH:mm:ss})";
    }

    private static void AddGroup(
        List<ControlStructureRowViewModel> rows,
        string name,
        int count,
        ControlStructureRowCommands? commands) =>
        rows.Add(new ControlStructureRowViewModel(
            "Group",
            name,
            $"{count.ToString(CultureInfo.InvariantCulture)} configured",
            string.Empty,
            Level: 0,
            IsGroup: true,
            commands: commands));

    private static string FormatMIDIBinding(ControlDeviceBindingConfig binding)
    {
        var parts = new List<string>();
        if (binding.MIDIInputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MIDIInputDeviceName))
            parts.Add($"in: {FormatDeviceBinding(binding.MIDIInputDeviceId, binding.MIDIInputDeviceName)}");
        if (binding.MIDIOutputDeviceId is not null || !string.IsNullOrWhiteSpace(binding.MIDIOutputDeviceName))
            parts.Add($"out: {FormatDeviceBinding(binding.MIDIOutputDeviceId, binding.MIDIOutputDeviceName)}");
        return parts.Count == 0 ? "(unbound)" : string.Join(" / ", parts);
    }

    private static string FormatOSCBinding(ControlDeviceBindingConfig binding)
    {
        var endpoint = !string.IsNullOrWhiteSpace(binding.OSCHost) && binding.OSCPort is { } port
            ? $"{binding.OSCHost}:{port.ToString(CultureInfo.InvariantCulture)}"
            : "(unbound)";
        return binding.OSCListenerId is { } listenerId
            ? $"{endpoint} - listener {listenerId}"
            : endpoint;
    }

    private static string FormatDeviceBinding(int? deviceId, string? deviceName)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? "(unnamed)" : deviceName.Trim();
        return deviceId is { } id
            ? $"{name} #{id.ToString(CultureInfo.InvariantCulture)}"
            : name;
    }

    private static string FormatScriptPath(string scriptPath) =>
        string.IsNullOrWhiteSpace(scriptPath) ? "(no file)" : scriptPath;

    private static string FormatEnabled(bool isEnabled) =>
        isEnabled ? "enabled" : "disabled";

    private void OnScriptRowChanged(ControlScriptRowViewModel row, ControlScriptConfig script)
    {
        var scripts = _config.Scripts.ToList();
        var index = scripts.FindIndex(s => s.Id == script.Id);
        if (index < 0)
            return;

        scripts[index] = script;
        _config = _config with { Scripts = scripts };
        RebuildStructureRows();

        if (ReferenceEquals(row, SelectedScriptRow))
        {
            SaveSelectedScriptCommand.NotifyCanExecuteChanged();
            if (string.IsNullOrWhiteSpace(script.ScriptPath))
                ScriptEditorStatus = "Script has no file path.";
            else if (!string.IsNullOrWhiteSpace(_projectRoot))
                ScriptEditorStatus = script.ScriptPath;
        }

        if (IsArmed)
            StatusMessage = "Script settings changed. Re-arm control to apply script changes.";
    }

    private void LoadSelectedScriptText(ControlScriptRowViewModel? row)
    {
        // _savedScriptBaseline mirrors whatever the buffer is loaded to, so IsSelectedScriptDirty starts false
        // after a (re)load and only flips once the user edits. Set it BEFORE assigning the buffer so the
        // SelectedScriptText change handler sees the fresh baseline.
        if (row is null)
        {
            _savedScriptBaseline = string.Empty;
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = "No script selected.";
            ExportedFunctionsSummary = "(no exports)";
            ScriptDiagnostics.Clear();
            OnPropertyChanged(nameof(IsSelectedScriptDirty));
            return;
        }

        var path = ResolveScriptPath(row.Script.ScriptPath);
        if (path is null)
        {
            _savedScriptBaseline = string.Empty;
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = string.IsNullOrWhiteSpace(row.Script.ScriptPath)
                ? "Script has no file path."
                : "Open or save the project before editing project-relative scripts.";
            ExportedFunctionsSummary = "(no exports)";
            ScriptDiagnostics.Clear();
            if (string.IsNullOrWhiteSpace(row.Script.ScriptPath))
                ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel("Compile", "Script path is required.", isError: true));
            OnPropertyChanged(nameof(IsSelectedScriptDirty));
            return;
        }

        if (!File.Exists(path))
        {
            _savedScriptBaseline = string.Empty;
            SelectedScriptText = string.Empty;
            ScriptEditorStatus = $"New file: {row.Script.ScriptPath}.";
            RefreshScriptAnalysis(row);
            OnPropertyChanged(nameof(IsSelectedScriptDirty));
            return;
        }

        var text = File.ReadAllText(path);
        _savedScriptBaseline = text;
        SelectedScriptText = text;
        ScriptEditorStatus = row.Script.ScriptPath;
        RefreshScriptAnalysis(row);
        OnPropertyChanged(nameof(IsSelectedScriptDirty));
    }

    private void RefreshScriptAnalysis(ControlScriptRowViewModel? row)
    {
        ScriptDiagnostics.Clear();
        if (row is null || string.IsNullOrWhiteSpace(row.Script.ScriptPath))
        {
            ExportedFunctionsSummary = "(no exports)";
            return;
        }

        try
        {
            var host = new ControlScriptFileHost(new OverlayControlScriptSourceProvider(
                CreateSourceProvider(),
                row.Script.ScriptPath,
                SelectedScriptText));
            var module = host.LoadModule(row.Script.ScriptPath);
            var exports = module.ExportedFunctionNames;
            ExportedFunctionsSummary = exports.Count == 0
                ? "(no exports)"
                : string.Join(", ", exports);
            ValidateTriggerExports(row.Script, exports);
        }
        catch (Exception ex)
        {
            ExportedFunctionsSummary = $"scan failed: {ex.Message}";
            ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel("Compile", ex.Message, isError: true));
        }
    }

    private void ValidateTriggerExports(ControlScriptConfig script, IReadOnlyList<string> exports)
    {
        var exportedFunctions = exports.ToHashSet(StringComparer.Ordinal);
        foreach (var trigger in script.Triggers)
        {
            if (string.IsNullOrWhiteSpace(trigger.FunctionName))
            {
                ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel(
                    "Compile",
                    $"{trigger.Kind} trigger has no function name.",
                    isError: true));
                continue;
            }

            if (!exportedFunctions.Contains(trigger.FunctionName))
            {
                ScriptDiagnostics.Add(new ControlScriptDiagnosticRowViewModel(
                    "Compile",
                    $"{trigger.Kind} trigger references missing export '{trigger.FunctionName}'.",
                    isError: true));
            }
        }
    }

    private string? ResolveScriptPath(string scriptPath)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
            return null;

        // EffectiveScriptRoot is the project folder, or the scratch cache while the project is unsaved.
        var root = Path.GetFullPath(EffectiveScriptRoot);
        var path = Path.GetFullPath(Path.Combine(root, scriptPath));
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : path;
    }

    [RelayCommand]
    private async Task ToggleArmAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            if (IsArmed)
            {
                await DisarmInternalAsync().ConfigureAwait(true);
                StatusMessage = "Disarmed.";
            }
            else
            {
                await ArmInternalAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _busy = false;
            NotifyArmState();
        }
    }
}
