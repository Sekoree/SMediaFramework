using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Models;
using HaPlay.Playback;
using S.Media.Core.Audio;

namespace HaPlay.ViewModels;

public partial class MediaPlayerViewModel : ViewModelBase
{
    private readonly OutputManagementViewModel _outputs;
    private readonly Action<MediaPlayerViewModel>? _requestRemove;
    private HaPlayPlaybackSession? _session;
    private DispatcherTimer? _loopTimer;
    private IdleLogoSlateSession? _idleSlate;
    private string? _idleSlateSig;
    private readonly DispatcherTimer _idleSlateSyncTimer;
    private DispatcherTimer? _holdPumpTimer;

    /// <summary>Serializes load/unload/stop/pause/play/seek and loop-timer Router use so Dispose cannot overlap transport.</summary>
    private readonly SemaphoreSlim _playbackArc = new(1, 1);

    private async Task WithPlaybackArcAsync(Func<Task> action)
    {
        await _playbackArc.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _playbackArc.Release();
        }
    }

    public MediaPlayerViewModel(OutputManagementViewModel outputs, string name,
        Action<MediaPlayerViewModel>? requestRemove = null)
    {
        _outputs = outputs;
        _requestRemove = requestRemove;
        Name = name;
        SyncOutputsCollection();
        _outputs.Outputs.CollectionChanged += OnSharedOutputsCollectionChanged;
        _idleSlateSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _idleSlateSyncTimer.Tick += (_, _) => SyncIdleSlate();
        _idleSlateSyncTimer.Start();
        PlaylistPaths.CollectionChanged += (_, _) => OnPlaylistPathsChanged();
        Dispatcher.UIThread.Post(() => SyncIdleSlate(), DispatcherPriority.Loaded);
    }

    private void OnPlaylistPathsChanged()
    {
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
        MovePlaylistItemUpCommand.NotifyCanExecuteChanged();
        MovePlaylistItemDownCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
    }

    private void OnSharedOutputsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            SyncOutputsCollection();
            SyncIdleSlate();
        });
    }

    public OutputManagementViewModel OutputsRepository => _outputs;

    /// <summary>Per-player checkbox bindings. Each player tracks its own selection so two players can route
    /// to overlapping subsets of outputs (e.g. main → NDI, preview → local SDL).</summary>
    public ObservableCollection<PlayerOutputBinding> Outputs { get; } = new();

    /// <summary>Paths queued for sequential playback after the current file ends.</summary>
    public ObservableCollection<string> PlaylistPaths { get; } = new();

    [ObservableProperty]
    private string _name = "Player";

    [ObservableProperty]
    private string? _mediaFilePath;

    [ObservableProperty]
    private string? _fallbackImagePath;

    [ObservableProperty]
    private string? _selectedPlaylistPath;

    [ObservableProperty]
    private bool _isLooping;

    [ObservableProperty]
    private bool _holdFallbackVideo;

    [ObservableProperty]
    private bool _isMediaLoaded;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _duration;

    [ObservableProperty]
    private TimeSpan _currentPosition;

    [ObservableProperty]
    private double _seekSliderValue;

    [ObservableProperty]
    private string? _statusMessage;

    public TimeSpan RemainingTime =>
        Duration > CurrentPosition ? Duration - CurrentPosition : TimeSpan.Zero;

    /// <summary>Pre-formatted text bound by the view — Avalonia's <c>StringFormat=-{}{0:...}</c> with a leading minus
    /// is fragile (the binding silently fails). Formatting in the VM avoids the trap.</summary>
    public string CurrentPositionText => FormatClock(CurrentPosition);
    public string RemainingTimeText => "-" + FormatClock(RemainingTime);
    public string DurationText => FormatClock(Duration);

    private static string FormatClock(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");

    public bool CanRemove => _requestRemove is not null;

    partial void OnIsMediaLoadedChanged(bool value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        SeekToSliderCommand.NotifyCanExecuteChanged();
        if (value)
            StopIdleSlate();
    }

    partial void OnDurationChanged(TimeSpan value)
    {
        _ = value;
        SeekToSliderCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RemainingTime));
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(DurationText));
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        _ = value;
        OnPropertyChanged(nameof(RemainingTime));
        OnPropertyChanged(nameof(RemainingTimeText));
        OnPropertyChanged(nameof(CurrentPositionText));
    }

    partial void OnSelectedPlaylistPathChanged(string? value)
    {
        _ = value;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
        MovePlaylistItemUpCommand.NotifyCanExecuteChanged();
        MovePlaylistItemDownCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Mirror the shared outputs list into per-player bindings, preserving selection on the survivors.</summary>
    private void SyncOutputsCollection()
    {
        var keep = Outputs.ToDictionary(b => b.Line);
        Outputs.Clear();
        foreach (var line in _outputs.Outputs)
        {
            if (!keep.TryGetValue(line, out var binding))
                binding = new PlayerOutputBinding(line);
            Outputs.Add(binding);
        }
    }

    private IReadOnlyList<OutputLineViewModel> SelectedOutputLines() =>
        Outputs.Where(b => b.IsSelected).Select(b => b.Line).ToList();

    [RelayCommand(CanExecute = nameof(CanRemovePlayer))]
    private void RemovePlayer()
    {
        if (_requestRemove is null) return;
        _ = CloseSessionAsync();
        _requestRemove(this);
    }

    private bool CanRemovePlayer() => _requestRemove is not null;

    partial void OnHoldFallbackVideoChanged(bool value)
    {
        _session?.SetHoldFallback(value);
        if (_session is not null && IsMediaLoaded)
        {
            if (value)
            {
                StartHoldPumpTimer();
            }
            else
            {
                StopHoldPumpTimer();
                // Restore the last real decoded frame at the current playhead so single-frame sources
                // (attached_pic / album cover art) come back instead of leaving receivers stuck on the
                // no-longer-pumped template.
                try
                {
                    var pt = _session.Player.PlayClock.CurrentPosition;
                    _session.ResubmitLastCachedFramesAt(pt);
                }
                catch
                {
                    /* best effort */
                }
            }
        }

        SyncIdleSlate();
    }

    partial void OnFallbackImagePathChanged(string? value)
    {
        if (_session is not null && !string.IsNullOrWhiteSpace(value))
            _session.ApplyFallbackImage(value);
        if (_session is not null && IsMediaLoaded && HoldFallbackVideo && !string.IsNullOrWhiteSpace(value))
        {
            try
            {
                _session.PumpHoldFrames(_session.Player.PlayClock.CurrentPosition);
            }
            catch
            {
                /* best effort */
            }

            StartHoldPumpTimer();
        }

        SyncIdleSlate();
    }

    [RelayCommand]
    private async Task AddFilesToPlaylistAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;
        var opts = new FilePickerOpenOptions { Title = "Add files to playlist", AllowMultiple = true };
        opts.FileTypeFilter =
        [
            new FilePickerFileType("Media") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.webm", "*.m4v", "*.avi", "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a", "*.ogg"] },
            new FilePickerFileType("All files") { Patterns = ["*"] },
        ];
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                continue;
            if (!PlaylistPaths.Contains(path, StringComparer.Ordinal))
                PlaylistPaths.Add(path);
        }

        if (SelectedPlaylistPath is null && PlaylistPaths.Count > 0)
            SelectedPlaylistPath = PlaylistPaths[0];
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlaylistItem))]
    private void RemoveFromPlaylist()
    {
        if (string.IsNullOrEmpty(SelectedPlaylistPath))
            return;
        var i = PlaylistPaths.IndexOf(SelectedPlaylistPath);
        if (i < 0) return;
        PlaylistPaths.RemoveAt(i);
        SelectedPlaylistPath = PlaylistPaths.Count > 0
            ? PlaylistPaths[Math.Min(i, PlaylistPaths.Count - 1)]
            : null;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistItem() =>
        !string.IsNullOrEmpty(SelectedPlaylistPath) && PlaylistPaths.Contains(SelectedPlaylistPath);

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemUp))]
    private void MovePlaylistItemUp()
    {
        var path = SelectedPlaylistPath;
        if (string.IsNullOrEmpty(path)) return;
        var i = PlaylistPaths.IndexOf(path);
        if (i <= 0) return;
        (PlaylistPaths[i - 1], PlaylistPaths[i]) = (PlaylistPaths[i], PlaylistPaths[i - 1]);
        SelectedPlaylistPath = path;
    }

    private bool CanMovePlaylistItemUp() =>
        !string.IsNullOrEmpty(SelectedPlaylistPath) && PlaylistPaths.IndexOf(SelectedPlaylistPath!) > 0;

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemDown))]
    private void MovePlaylistItemDown()
    {
        var path = SelectedPlaylistPath;
        if (string.IsNullOrEmpty(path)) return;
        var i = PlaylistPaths.IndexOf(path);
        if (i < 0 || i >= PlaylistPaths.Count - 1) return;
        (PlaylistPaths[i + 1], PlaylistPaths[i]) = (PlaylistPaths[i], PlaylistPaths[i + 1]);
        SelectedPlaylistPath = path;
    }

    private bool CanMovePlaylistItemDown()
    {
        if (string.IsNullOrEmpty(SelectedPlaylistPath)) return false;
        var idx = PlaylistPaths.IndexOf(SelectedPlaylistPath);
        return idx >= 0 && idx < PlaylistPaths.Count - 1;
    }

    /// <summary>Invoked from the view when the user double-clicks a playlist item — load it and start playing.</summary>
    public async Task PlayPlaylistItemAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        SelectedPlaylistPath = path;
        MediaFilePath = path;
        await OpenOrReloadAsync().ConfigureAwait(false);
        if (_session is not null && !IsPlaying)
            await StartPlaybackAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task BrowseFallbackAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;
        var opts = new FilePickerOpenOptions { Title = "Fallback image (PNG / JPEG)", AllowMultiple = false };
        opts.FileTypeFilter =
        [
            new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] },
            new FilePickerFileType("All files") { Patterns = ["*"] },
        ];
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null) return;
        var path = f.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            FallbackImagePath = path;
    }

    [RelayCommand]
    private async Task SavePlayerConfigAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var opts = new FilePickerSaveOptions
        {
            Title = "Save player configuration",
            DefaultExtension = "haplay.json",
            SuggestedFileName = SanitizeFileName(Name),
            FileTypeChoices =
            [
                new FilePickerFileType("HaPlay player config") { Patterns = ["*.haplay.json", "*.json"] },
            ],
        };
        var file = await top.StorageProvider.SaveFilePickerAsync(opts);
        if (file is null) return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        var config = BuildPlayerConfig();
        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, config, MediaPlayerConfigJsonContext.Default.MediaPlayerConfig)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = null);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Save failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadPlayerConfigAsync()
    {
        var top = TryGetMainWindow();
        if (top is null) return;

        var opts = new FilePickerOpenOptions
        {
            Title = "Load player configuration",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("HaPlay player config") { Patterns = ["*.haplay.json", "*.json"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        };
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null) return;
        var path = f.TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        MediaPlayerConfig? config;
        try
        {
            await using var stream = File.OpenRead(path);
            config = await JsonSerializer
                .DeserializeAsync(stream, MediaPlayerConfigJsonContext.Default.MediaPlayerConfig)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusMessage = $"Load failed: {ex.Message}");
            return;
        }

        if (config is null)
            return;

        await Dispatcher.UIThread.InvokeAsync(() => ApplyPlayerConfig(config));
    }

    private MediaPlayerConfig BuildPlayerConfig() => new()
    {
        Name = Name,
        PlaylistPaths = PlaylistPaths.ToList(),
        MediaFilePath = MediaFilePath,
        SelectedPlaylistPath = SelectedPlaylistPath,
        FallbackImagePath = FallbackImagePath,
        IsLooping = IsLooping,
        HoldFallbackVideo = HoldFallbackVideo,
        SelectedOutputDisplayNames = Outputs
            .Where(b => b.IsSelected)
            .Select(b => b.Line.Definition.DisplayName)
            .ToList(),
    };

    private void ApplyPlayerConfig(MediaPlayerConfig config)
    {
        Name = string.IsNullOrWhiteSpace(config.Name) ? Name : config.Name;

        PlaylistPaths.Clear();
        foreach (var p in config.PlaylistPaths)
            PlaylistPaths.Add(p);

        MediaFilePath = config.MediaFilePath;
        SelectedPlaylistPath = config.SelectedPlaylistPath is { } sp && PlaylistPaths.Contains(sp, StringComparer.Ordinal)
            ? sp
            : PlaylistPaths.Count > 0 ? PlaylistPaths[0] : null;
        FallbackImagePath = config.FallbackImagePath;
        IsLooping = config.IsLooping;
        HoldFallbackVideo = config.HoldFallbackVideo;

        var wanted = new HashSet<string>(config.SelectedOutputDisplayNames, StringComparer.OrdinalIgnoreCase);
        var missing = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        foreach (var binding in Outputs)
        {
            var name = binding.Line.Definition.DisplayName;
            var selected = wanted.Contains(name);
            binding.IsSelected = selected;
            if (selected) missing.Remove(name);
        }

        StatusMessage = missing.Count > 0
            ? $"Loaded. Missing outputs: {string.Join(", ", missing)}."
            : null;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "player.haplay.json";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        return clean + ".haplay.json";
    }

    private async Task CloseSessionCoreInnerAsync(bool deferIdleSync, bool resetPlayingUi = true)
    {
        var snapshot = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StopHoldPumpTimer();
            _loopTimer?.Stop();
            _loopTimer = null;
            var snap = _session;
            if (snap is not null)
            {
                try { snap.Player.PlayClock.PositionChanged -= OnClockPositionChanged; }
                catch { /* best effort */ }
                _session = null;
            }
            return snap;
        });

        if (snapshot is not null)
        {
            // Two-tier wall: 2s inner ct keeps Pause from hanging; 8s outer wall lets Dispose finish even on slow
            // sinks. A previous 50s outer cap would freeze the UI for nearly a minute if a sink blocked.
            await RunBoundedAsync(() =>
            {
                try
                {
                    using var pauseCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { snapshot.Router.PauseSkippingSharedMuxFlush(pauseCts.Token); }
                    catch (OperationCanceledException) { /* bounded */ }
                    catch (ObjectDisposedException) { /* already torn down */ }
                }
                catch { /* best effort */ }

                try { snapshot.Dispose(); }
                catch { /* best effort */ }
            }, TimeSpan.FromSeconds(8));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsMediaLoaded = false;
            if (resetPlayingUi) IsPlaying = false;
            CurrentPosition = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            SeekSliderValue = 0;
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            SeekToSliderCommand.NotifyCanExecuteChanged();
            if (!deferIdleSync) SyncIdleSlate();
        });
    }

    private bool CanLoadMedia() =>
        !string.IsNullOrWhiteSpace(MediaFilePath) && File.Exists(MediaFilePath!);

    private async Task OpenOrReloadAsync()
    {
        if (!CanLoadMedia())
            return;

        var resumeAfterOpen = await Dispatcher.UIThread.InvokeAsync(() => IsPlaying);

        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: !resumeAfterOpen);

            var (path, selected) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopIdleSlate();
                var lines = SelectedOutputLines();
                _outputs.StopPreviewsForPlayback(lines);
                return (MediaFilePath!, lines);
            });

            HaPlayPlaybackSession? created = null;
            string? createErr = null;
            await Task.Run(() =>
            {
                if (!HaPlayPlaybackSession.TryCreate(path, selected, _outputs, out created, out createErr))
                    created = null;
            }).ConfigureAwait(false);

            // Never use InvokeAsync(async () => await Task.Run(...)): the UI dispatcher can deadlock with the
            // threadpool continuation that is waiting for InvokeAsync to complete.
            var holdFbAfterOpen = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (created is null)
                {
                    StatusMessage = createErr ?? "Failed to open media.";
                    SyncIdleSlate();
                    return false;
                }

                _session = created;
                IsMediaLoaded = true;
                StatusMessage = null;
                Duration = created.Player.Decoder.Audio is ISeekableSource a ? a.Duration : TimeSpan.Zero;

                created.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                if (!string.IsNullOrWhiteSpace(FallbackImagePath))
                    created.ApplyFallbackImage(FallbackImagePath);
                created.SetHoldFallback(HoldFallbackVideo);

                if (HoldFallbackVideo)
                {
                    try { created.PumpHoldFrames(created.Player.PlayClock.CurrentPosition); }
                    catch { /* best effort */ }
                }

                EnsureLoopTimerStarted();
                return HoldFallbackVideo;
            });

            if (created is null) return;

            if (resumeAfterOpen)
            {
                var s = created;
                var hf = holdFbAfterOpen;
                var ok = await RunBoundedAsync(() =>
                {
                    s.PrepareOutputsBeforePlay(hf);
                    s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
                }, TimeSpan.FromSeconds(8));

                if (!ok)
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsPlaying = false;
                        StatusMessage = "Playback failed to resume after loading.";
                    });
            }

            if (holdFbAfterOpen)
                await Dispatcher.UIThread.InvokeAsync(StartHoldPumpTimer);
        }).ConfigureAwait(false);
    }

    private void EnsureLoopTimerStarted()
    {
        if (_loopTimer is not null)
            return;
        _loopTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _loopTimer.Tick += OnLoopTimerTick;
        _loopTimer.Start();
    }

    private void StartHoldPumpTimer()
    {
        if (_holdPumpTimer is not null)
            return;
        _holdPumpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 30.0) };
        _holdPumpTimer.Tick += OnHoldPumpTick;
        _holdPumpTimer.Start();
    }

    private void StopHoldPumpTimer()
    {
        if (_holdPumpTimer is null)
            return;
        _holdPumpTimer.Tick -= OnHoldPumpTick;
        _holdPumpTimer.Stop();
        _holdPumpTimer = null;
    }

    private void OnHoldPumpTick(object? sender, EventArgs e)
    {
        if (_session is null || !IsMediaLoaded || !HoldFallbackVideo)
            return;
        var session = _session;
        var t = session.Player.PlayClock.CurrentPosition;
        _ = PumpHoldFramesOnPoolAsync(session, t);
    }

    private async Task PumpHoldFramesOnPoolAsync(HaPlayPlaybackSession session, TimeSpan presentationTime)
    {
        if (!await _playbackArc.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            try
            {
                // Never hold _playbackArc across unbounded native work — load/stop/seek wait on this gate and the
                // Load command stays disabled until OpenOrReloadAsync's WithPlaybackArcAsync completes.
                await Task.Run(() => session.PumpHoldFrames(presentationTime))
                    .WaitAsync(TimeSpan.FromMilliseconds(1200))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                /* avoid wedging transport behind a stuck hold pump */
            }
            catch (ObjectDisposedException)
            {
                /* session replaced */
            }
            catch
            {
                /* best effort */
            }
        }
        finally
        {
            _playbackArc.Release();
        }
    }

    private void OnClockPositionChanged(object? sender, TimeSpan e) =>
        Dispatcher.UIThread.Post(() =>
        {
            CurrentPosition = e;
            if (Duration > TimeSpan.Zero)
                SeekSliderValue = e.Ticks * 1000.0 / Duration.Ticks;
        }, DispatcherPriority.Normal);

    private void OnLoopTimerTick(object? sender, EventArgs e) =>
        _ = ProcessLoopTimerTickAsync();

    private async Task ProcessLoopTimerTickAsync()
    {
        if (_session is null || !IsMediaLoaded || !IsPlaying)
            return;

        var holdFb = HoldFallbackVideo;

        if (!await _playbackArc.WaitAsync(0).ConfigureAwait(false))
            return;

        var advancePlaylist = false;
        var resumePlayForPlaylist = false;
        try
        {
            var session = _session;
            if (session is null) return;

            if (IsLooping)
            {
                // Use audio's natural completion when an audio router is present (covers both audio-only
                // and audio+video sources). Falls back to video for video-only files where audio is null.
                var loopReady = session.Player.Audio?.Router is { } loopAr
                    ? !loopAr.IsRunning && loopAr.CompletedNaturally
                    : session.Player.Video.CompletedNaturally;
                if (!loopReady) return;
                await RunBoundedCancelableAsync(ct =>
                    {
                        session.Router.SeekCoordinatedSkippingSharedMuxFlush(TimeSpan.Zero, ct);
                        // No NDI warmup on loop wrap — receivers are already locked on and a silence gap would
                        // be audible between the last and first samples of the loop.
                        session.PrepareOutputsBeforePlay(holdFb);
                        session.Router.Play(prefillBeforeHardware: null, startHardware: session.StartAllPortAudio);
                    },
                    innerTimeout: TimeSpan.FromSeconds(3),
                    outerTimeout: TimeSpan.FromSeconds(5));

                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
                return;
            }

            var fileEnded = session.Player.Audio?.Router is { } ar
                ? !ar.IsRunning && ar.CompletedNaturally
                : session.Player.Video.CompletedNaturally;
            if (!fileEnded) return;

            resumePlayForPlaylist = IsPlaying;
            advancePlaylist = true;
            await RunBoundedCancelableAsync(session.Router.PauseSkippingSharedMuxFlush,
                innerTimeout: TimeSpan.FromSeconds(1.5),
                outerTimeout: TimeSpan.FromSeconds(2.5));
        }
        finally
        {
            _playbackArc.Release();
        }

        if (!advancePlaylist) return;

        var shouldLoadNext = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_session is null || !IsMediaLoaded) return false;
            IsPlaying = false;
            if (!TryGetNextPlaylistPath(out var nextPath)) return false;
            MediaFilePath = nextPath;
            SelectedPlaylistPath = nextPath;
            return true;
        });
        if (!shouldLoadNext) return;

        await OpenOrReloadAsync();

        if (!resumePlayForPlaylist) return;

        await WithPlaybackArcAsync(async () =>
        {
            var (s, holdForPrime) = await Dispatcher.UIThread.InvokeAsync(() => (_session, HoldFallbackVideo));
            if (s is null) return;

            var ok = await RunBoundedAsync(() =>
            {
                // Playlist advance — receivers may have drained between tracks.
                s.PrepareOutputsBeforePlay(holdForPrime);
                s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
            }, TimeSpan.FromSeconds(6));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ok) return;
                IsPlaying = true;
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        // Auto-load: if nothing's loaded yet but the user has selected a playlist row, load + play in one click.
        if (_session is null && !string.IsNullOrEmpty(SelectedPlaylistPath) && File.Exists(SelectedPlaylistPath))
        {
            MediaFilePath = SelectedPlaylistPath;
            IsPlaying = true; // signals OpenOrReloadAsync to resume after open
            await OpenOrReloadAsync().ConfigureAwait(false);
            return;
        }

        await StartPlaybackAsync().ConfigureAwait(false);
    }

    private async Task StartPlaybackAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            var (s, holdFb) = await Dispatcher.UIThread.InvokeAsync(() => (_session, HoldFallbackVideo));
            if (s is null) return;

            var ok = await RunBoundedAsync(() =>
            {
                // Play from a non-playing state — NDI receivers may have drained their buffers since the last
                // Pause/Stop, so push silence ahead of the first real samples.
                s.PrepareOutputsBeforePlay(holdFb);
                s.Router.Play(prefillBeforeHardware: null, startHardware: s.StartAllPortAudio);
            }, TimeSpan.FromSeconds(6));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ok) return;
                IsPlaying = true;
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    private bool CanPlay() =>
        (_session is not null && IsMediaLoaded) ||
        (_session is null && !string.IsNullOrEmpty(SelectedPlaylistPath));

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private async Task PauseAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            var s = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!HoldFallbackVideo) StopHoldPumpTimer();
                return _session;
            });
            if (s is null) return;

            await RunBoundedCancelableAsync(s.Router.PauseSkippingSharedMuxFlush,
                innerTimeout: TimeSpan.FromSeconds(1.5),
                outerTimeout: TimeSpan.FromSeconds(2.5));

            await Dispatcher.UIThread.InvokeAsync(() => IsPlaying = false);
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private async Task StopAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            var (snap, doPump) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopHoldPumpTimer();
                _loopTimer?.Stop();
                _loopTimer = null;
                IsPlaying = false;
                return (_session, HoldFallbackVideo);
            });
            if (snap is null) return;

            // One coordinated pause+seek so the freeze never exceeds the outer cap. Three nested
            // Task.Run/.WaitAsync blocks (the previous shape) could stack to ~11s on slow codecs.
            await RunBoundedCancelableAsync(ct =>
                {
                    snap.Router.SeekCoordinatedSkippingSharedMuxFlush(TimeSpan.Zero, ct);
                    if (doPump)
                    {
                        try { snap.PumpHoldFrames(TimeSpan.Zero); }
                        catch { /* best effort */ }
                    }
                },
                innerTimeout: TimeSpan.FromSeconds(2),
                outerTimeout: TimeSpan.FromSeconds(3));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_session != snap) return;
                CurrentPosition = TimeSpan.Zero;
                SeekSliderValue = 0;
            });
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the thread pool with a hard <paramref name="outerTimeout"/> wall. Returns
    /// true when the action completed within the budget. Swallows transport teardown noise — the caller decides
    /// what to do based on the result, never on exceptions.
    /// </summary>
    private static async Task<bool> RunBoundedAsync(Action action, TimeSpan outerTimeout)
    {
        try
        {
            await Task.Run(action).WaitAsync(outerTimeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Two-tier timeout: pass <paramref name="innerTimeout"/> as a CancellationToken to <paramref name="action"/> (so framework
    /// joins exit cooperatively), then enforce <paramref name="outerTimeout"/> with <see cref="Task.WaitAsync(TimeSpan)"/> as a
    /// last-resort wall. Without the outer wall, a stuck native call would freeze the UI indefinitely.
    /// </summary>
    private static async Task<bool> RunBoundedCancelableAsync(Action<CancellationToken> action, TimeSpan innerTimeout, TimeSpan outerTimeout)
    {
        try
        {
            await Task.Run(() =>
            {
                using var cts = new CancellationTokenSource(innerTimeout);
                try { action(cts.Token); }
                catch (OperationCanceledException) { /* bounded */ }
            }).WaitAsync(outerTimeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool CanTransport() => _session is not null && IsMediaLoaded;

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private async Task SeekToSliderAsync()
    {
        if (_session is null || Duration <= TimeSpan.Zero)
            return;
        await WithPlaybackArcAsync(async () =>
        {
            var (session, playing, holdFb, sliderValue) = await Dispatcher.UIThread.InvokeAsync(() =>
                (_session, IsPlaying, HoldFallbackVideo, SeekSliderValue));
            if (session is null) return;

            var t = TimeSpan.FromTicks((long)(sliderValue * Duration.Ticks / 1000.0));

            await RunBoundedCancelableAsync(ct =>
                {
                    session.Router.SeekCoordinatedSkippingSharedMuxFlush(t, ct);
                    if (playing)
                    {
                        // No NDI warmup on seek — silence at the seek target would be obviously wrong audio.
                        session.PrepareOutputsBeforePlay(holdFb);
                        session.Router.Play(prefillBeforeHardware: null, startHardware: session.StartAllPortAudio);
                    }
                },
                innerTimeout: TimeSpan.FromSeconds(3),
                outerTimeout: TimeSpan.FromSeconds(5));

            if (!playing) return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HoldFallbackVideo) StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    private bool CanSeek() => _session is not null && IsMediaLoaded && Duration > TimeSpan.Zero;

    [RelayCommand]
    private Task CloseSessionAsync() => WithPlaybackArcAsync(() => CloseSessionCoreInnerAsync(false));

    private bool TryGetNextPlaylistPath([NotNullWhen(true)] out string? nextPath)
    {
        nextPath = null;
        if (PlaylistPaths.Count == 0 || string.IsNullOrEmpty(MediaFilePath))
            return false;
        var idx = PlaylistPaths.IndexOf(MediaFilePath);
        if (idx < 0)
            return false;
        var n = idx + 1;
        if (n >= PlaylistPaths.Count)
            return false;
        nextPath = PlaylistPaths[n];
        return true;
    }

    private static Window? TryGetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            return d.MainWindow;
        return null;
    }

    private void StopIdleSlate()
    {
        _idleSlate?.Dispose();
        _idleSlate = null;
        _idleSlateSig = null;
    }

    private void SyncIdleSlate()
    {
        if (IsMediaLoaded)
        {
            StopIdleSlate();
            return;
        }

        var selected = SelectedOutputLines();
        var sig = IdleLogoSlateSession.BuildSignature(HoldFallbackVideo, FallbackImagePath, selected);
        if (_idleSlate is not null && _idleSlateSig == sig)
            return;

        StopIdleSlate();

        if (!HoldFallbackVideo || string.IsNullOrWhiteSpace(FallbackImagePath) ||
            !File.Exists(FallbackImagePath!) || selected.Count == 0)
            return;

        if (!IdleLogoSlateSession.TryStart(selected, _outputs, FallbackImagePath!, out var slate, out var err))
        {
            if (!string.IsNullOrWhiteSpace(err))
                StatusMessage = err;
            return;
        }

        _idleSlate = slate;
        _idleSlateSig = sig;
    }
}
