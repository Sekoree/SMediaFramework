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
/// Fallback MIDI device resolution (ambiguous/missing port picks).
/// Partial of <see cref="ControlWorkspaceViewModel"/> - split from the original single file purely
/// for navigability; no behavior differences.
/// </summary>
public partial class ControlWorkspaceViewModel
{
    // ----- Fallback MIDI device resolution ----------------------------------------------------
    // When a configured MIDI device cannot be confidently matched to a current port (ambiguous or
    // missing), let the user pick the live port and persist that choice into the device binding.

    [RelayCommand(CanExecute = nameof(CanResolveMIDIDevices))]
    private async Task ResolveMIDIDevicesAsync()
    {
        if (await ResolveMIDIDevicesCoreAsync(announceWhenResolvedOrEmpty: true).ConfigureAwait(true))
            StatusMessage = "MIDI device bindings resolved." + (IsArmed ? " Re-arm to apply." : string.Empty);
    }

    private bool CanResolveMIDIDevices() => IsMIDIAvailable;

    /// <summary>
    /// Enumerates current MIDI ports, prompts the user to resolve any ambiguous/missing bindings, and writes
    /// the chosen ports back into the config. Returns true when at least one binding was updated.
    /// </summary>
    private async Task<bool> ResolveMIDIDevicesCoreAsync(bool announceWhenResolvedOrEmpty)
    {
        if (!IsMIDIAvailable)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = MIDIUnavailableStatus;
            return false;
        }

        var catalog = MIDICatalogProvider();
        if (catalog is null)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "MIDI device catalog is unavailable.";
            return false;
        }

        var requests = ControlMIDIDeviceResolver.BuildRequests(_config, catalog.Inputs, catalog.Outputs);
        if (requests.Count == 0)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "All enabled MIDI devices resolve to a current port.";
            return false;
        }

        var selections = await MIDIResolutionPrompt(requests).ConfigureAwait(true);
        if (selections is null || selections.Count == 0)
        {
            if (announceWhenResolvedOrEmpty)
                StatusMessage = "MIDI device resolution cancelled.";
            return false;
        }

        _config = ControlMIDIDeviceResolver.ApplySelections(_config, selections);
        RebuildStructureRows();
        RebuildProfileWarnings();
        RebuildProfileRows();
        RebuildX32CommandRows(_session?.ScriptSession.OSCCache);
        NotifySummary();
        return true;
    }

    private static ControlMIDIPortCatalog? EnumerateMIDIPorts() =>
        ControlMIDIPortCatalogProvider.TryEnumerate();

    private static async Task<IReadOnlyDictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>?> DefaultPromptAsync(
        IReadOnlyList<ControlMIDIResolutionRequest> requests)
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var dialog = new RebindMissingControlMIDIDevicesDialog
        {
            DataContext = new RebindMissingControlMIDIDevicesDialogViewModel(requests),
        };
        return await dialog.ShowDialog<IReadOnlyDictionary<ControlMIDIResolutionKey, ControlMIDIPortInfo>?>(owner)
            .ConfigureAwait(true);
    }

    private static async Task<string?> DefaultProfileImportPathPromptAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var picks = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import control profile",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Control profile JSON") { Patterns = ["*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);
        return picks.FirstOrDefault()?.TryGetLocalPath();
    }

    private static async Task<string?> DefaultProfileExportDirectoryPromptAsync()
    {
        var owner = TryGetOwnerWindow();
        if (owner is null)
            return null;

        var picks = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export control profiles",
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return picks.FirstOrDefault()?.TryGetLocalPath();
    }

    private static Window? TryGetOwnerWindow() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

    private static async Task RunOnUiThreadAsync(Action action)
    {
        if (Application.Current?.ApplicationLifetime is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    private async Task ArmInternalAsync()
    {
        // Give the user a chance to bind ambiguous/missing MIDI devices to live ports before opening
        // sessions. No-op in tests/headless (no owner window, or no enabled MIDI bindings to resolve).
        await ResolveMIDIDevicesCoreAsync(announceWhenResolvedOrEmpty: false).ConfigureAwait(true);

        ControlSystemRuntimeSession? pendingSession = null;
        UdpControlOSCSender? pendingOSC = null;
        try
        {
            var armedConfig = _config with { IsArmed = true };
            var monitor = new ControlMonitorBuffer(Math.Max(1, _config.Monitor.MaxVisibleMessages));
            var osc = new UdpControlOSCSender(armedConfig);
            var midi = new ControlSystemMIDIDeviceSessionManager(armedConfig, monitor);
            var session = new ControlSystemRuntimeSession(
                armedConfig,
                CreateSourceProvider(),
                osc,
                midi,
                monitor: monitor,
                midiSessions: midi);
            pendingSession = session;
            pendingOSC = osc;
            await session.StartAsync().ConfigureAwait(true);

            _monitorBuffer = monitor;
            _oscSender = osc;
            _midiSender = midi;
            _session = session;
            pendingSession = null;
            pendingOSC = null;
            _lastRenderedVersion = -1;
            _lastX32CacheVersion = -1;
            StatusMessage = $"Armed - {ListenerCount} listener(s), {DeviceCount} device(s), {ScriptCount} script(s).";
        }
        catch (Exception ex)
        {
            if (pendingSession is not null)
            {
                try
                {
                    await pendingSession.DisposeAsync().ConfigureAwait(true);
                }
                catch
                {
                    // best effort cleanup after failed arm
                }
            }

            pendingOSC?.Dispose();
            await DisarmInternalAsync().ConfigureAwait(true);
            StatusMessage = $"Failed to arm: {ex.Message}";
        }
    }

    private async Task DisarmInternalAsync()
    {
        var session = _session;
        var osc = _oscSender;
        _session = null;
        _monitorBuffer = null;
        _oscSender = null;
        _midiSender = null;

        if (session is not null)
        {
            try
            {
                await session.StopAsync().ConfigureAwait(true);
                await session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // Disarming must never throw; the session is being torn down regardless.
            }
        }

        osc?.Dispose();
    }

    private void StopSessionFireAndForget()
    {
        var session = _session;
        var osc = _oscSender;
        _session = null;
        _monitorBuffer = null;
        _oscSender = null;
        _midiSender = null;
        if (session is null && osc is null)
            return;

        _ = Task.Run(async () =>
        {
            if (session is not null)
            {
                try
                {
                    await session.StopAsync().ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best effort teardown on project switch
                }
            }

            osc?.Dispose();
        });
    }

    private IControlScriptSourceProvider CreateSourceProvider() =>
        // Resolve helper scripts (imports) against the project folder, or the scratch cache while unsaved,
        // so a script's `require` of a sibling file works before the project has ever been saved.
        Directory.Exists(EffectiveScriptRoot)
            ? new FileSystemControlScriptSourceProvider(EffectiveScriptRoot)
            : new InMemoryControlScriptSourceProvider(new Dictionary<string, string>());

    [RelayCommand]
    private void ClearMonitor()
    {
        _monitorBuffer?.Clear();
        MonitorEntries.Clear();
        _lastRenderedVersion = -1;
    }

    [RelayCommand]
    private async Task SendTestOSCAsync()
    {
        var osc = _oscSender;
        var monitor = _monitorBuffer;
        if (osc is null || monitor is null)
        {
            StatusMessage = "Arm the control system before sending test OSC.";
            return;
        }

        var host = TestOSCHost.Trim();
        var address = TestOSCAddress.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            StatusMessage = "OSC test host is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            StatusMessage = "OSC test address is required.";
            return;
        }

        if (!int.TryParse(TestOSCPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            StatusMessage = "Invalid OSC port.";
            return;
        }

        var args = ParseOSCArgs(TestOSCArgs);
        try
        {
            await osc.SendAsync(host, port, address, args).ConfigureAwait(true);
            monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Output,
                Protocol = ControlMonitorProtocol.OSC,
                Result = ControlMonitorResult.Sent,
                RemoteHost = host,
                RemotePort = port,
                Endpoint = $"{host}:{port}",
                Address = address,
                OSCArguments = args.Select(ControlMonitorOSCArgumentRecord.FromOSCArgument).ToList(),
                Message = "test send",
            });
        }
        catch (Exception ex)
        {
            monitor.Record(new ControlMonitorRecord
            {
                Direction = ControlMonitorDirection.Error,
                Protocol = ControlMonitorProtocol.OSC,
                Result = ControlMonitorResult.Failed,
                RemoteHost = host,
                RemotePort = port,
                Address = address,
                Message = "test send",
                ErrorMessage = ex.Message,
            });
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedX32CommandRow))]
    private void UseSelectedX32CommandForTestSend()
    {
        var row = SelectedX32CommandRow;
        if (row is null)
            return;

        TestOSCHost = row.Host;
        TestOSCPort = row.Port?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        TestOSCAddress = row.Address;
        TestOSCArgs = string.Empty;
        StatusMessage = $"Prepared '{row.CommandName}' for test send.";
    }

    [RelayCommand(CanExecute = nameof(CanRequestSelectedX32Command))]
    private async Task RequestSelectedX32CommandAsync()
    {
        UseSelectedX32CommandForTestSend();
        await SendTestOSCAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunManualScriptsAsync()
    {
        var session = _session;
        if (session is null)
        {
            StatusMessage = "Arm the control system first.";
            return;
        }

        try
        {
            await session.EventQueue.DispatchManualAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Manual run error: {ex.Message}";
        }
    }
}
