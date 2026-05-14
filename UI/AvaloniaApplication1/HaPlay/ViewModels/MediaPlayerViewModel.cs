using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HaPlay.Playback;
using S.Media.Core.Audio;

namespace HaPlay.ViewModels;

public partial class MediaPlayerViewModel : ViewModelBase
{
    private readonly OutputManagementViewModel _outputs;
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

    public MediaPlayerViewModel(OutputManagementViewModel outputs)
    {
        _outputs = outputs;
        _outputs.Outputs.CollectionChanged += OnOutputsCollectionChanged;
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
    }

    private void OnOutputsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(SyncIdleSlate);

    public OutputManagementViewModel OutputsRepository => _outputs;

    /// <summary>Paths queued for sequential playback after the current file ends.</summary>
    public ObservableCollection<string> PlaylistPaths { get; } = new();

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

    partial void OnMediaFilePathChanged(string? value)
    {
        _ = value;
        LoadMediaCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMediaLoadedChanged(bool value)
    {
        _ = value;
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
    }

    partial void OnHoldFallbackVideoChanged(bool value)
    {
        _session?.SetHoldFallback(value);
        if (_session is not null && IsMediaLoaded)
        {
            if (value)
                StartHoldPumpTimer();
            else
                StopHoldPumpTimer();
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
    private async Task BrowseMediaAsync()
    {
        var top = TryGetMainWindow();
        if (top is null)
            return;
        var opts = new FilePickerOpenOptions { Title = "Open media file", AllowMultiple = false };
        opts.FileTypeFilter =
        [
            new FilePickerFileType("Media") { Patterns = ["*.mp4", "*.mkv", "*.mov", "*.webm", "*.m4v", "*.avi", "*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a", "*.ogg"] },
            new FilePickerFileType("All files") { Patterns = ["*"] },
        ];
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null)
            return;
        var path = f.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            MediaFilePath = path;
    }

    [RelayCommand]
    private async Task AddFilesToPlaylistAsync()
    {
        var top = TryGetMainWindow();
        if (top is null)
            return;
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
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlaylistItem))]
    private void RemoveFromPlaylist()
    {
        if (string.IsNullOrEmpty(SelectedPlaylistPath))
            return;
        var i = PlaylistPaths.IndexOf(SelectedPlaylistPath);
        if (i < 0)
            return;
        PlaylistPaths.RemoveAt(i);
        SelectedPlaylistPath = PlaylistPaths.Count > 0
            ? PlaylistPaths[Math.Min(i, PlaylistPaths.Count - 1)]
            : null;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemovePlaylistItem() =>
        !string.IsNullOrEmpty(SelectedPlaylistPath) && PlaylistPaths.Contains(SelectedPlaylistPath);

    partial void OnSelectedPlaylistPathChanged(string? value)
    {
        _ = value;
        RemoveFromPlaylistCommand.NotifyCanExecuteChanged();
        MovePlaylistItemUpCommand.NotifyCanExecuteChanged();
        MovePlaylistItemDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemUp))]
    private void MovePlaylistItemUp()
    {
        var path = SelectedPlaylistPath;
        if (string.IsNullOrEmpty(path))
            return;
        var i = PlaylistPaths.IndexOf(path);
        if (i <= 0)
            return;
        (PlaylistPaths[i - 1], PlaylistPaths[i]) = (PlaylistPaths[i], PlaylistPaths[i - 1]);
        SelectedPlaylistPath = path;
    }

    private bool CanMovePlaylistItemUp() =>
        !string.IsNullOrEmpty(SelectedPlaylistPath) && PlaylistPaths.IndexOf(SelectedPlaylistPath!) > 0;

    [RelayCommand(CanExecute = nameof(CanMovePlaylistItemDown))]
    private void MovePlaylistItemDown()
    {
        var path = SelectedPlaylistPath;
        if (string.IsNullOrEmpty(path))
            return;
        var i = PlaylistPaths.IndexOf(path);
        if (i < 0 || i >= PlaylistPaths.Count - 1)
            return;
        (PlaylistPaths[i + 1], PlaylistPaths[i]) = (PlaylistPaths[i], PlaylistPaths[i + 1]);
        SelectedPlaylistPath = path;
    }

    private bool CanMovePlaylistItemDown()
    {
        if (string.IsNullOrEmpty(SelectedPlaylistPath))
            return false;
        var idx = PlaylistPaths.IndexOf(SelectedPlaylistPath);
        return idx >= 0 && idx < PlaylistPaths.Count - 1;
    }

    [RelayCommand]
    private Task LoadSelectedPlaylistItemAsync()
    {
        if (string.IsNullOrEmpty(SelectedPlaylistPath) || !File.Exists(SelectedPlaylistPath))
            return Task.CompletedTask;
        MediaFilePath = SelectedPlaylistPath;
        return OpenOrReloadAsync();
    }

    [RelayCommand]
    private async Task BrowseFallbackAsync()
    {
        var top = TryGetMainWindow();
        if (top is null)
            return;
        var opts = new FilePickerOpenOptions { Title = "Fallback image (PNG / JPEG)", AllowMultiple = false };
        opts.FileTypeFilter =
        [
            new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] },
            new FilePickerFileType("All files") { Patterns = ["*"] },
        ];
        var files = await top.StorageProvider.OpenFilePickerAsync(opts);
        var f = files.FirstOrDefault();
        if (f is null)
            return;
        var path = f.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            FallbackImagePath = path;
    }

    [RelayCommand(CanExecute = nameof(CanLoadMedia))]
    private Task LoadMediaAsync() => OpenOrReloadAsync();

    private bool CanLoadMedia() =>
        !string.IsNullOrWhiteSpace(MediaFilePath) && File.Exists(MediaFilePath!);

    private async Task CloseSessionCoreInnerAsync(bool deferIdleSync, bool resetPlayingUi = true)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StopHoldPumpTimer();
            _loopTimer?.Stop();
            _loopTimer = null;
        });

        HaPlayPlaybackSession? snapshot = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            snapshot = _session;
            if (_session is not null)
            {
                try
                {
                    _session.Player.PlayClock.PositionChanged -= OnClockPositionChanged;
                }
                catch
                {
                    /* best effort */
                }

                _session = null;
            }
        });

        if (snapshot is not null)
        {
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using (var pauseCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                        {
                            try
                            {
                                snapshot.Router.PauseSkippingSharedMuxFlush(pauseCts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                /* bounded pause — continue to Dispose */
                            }
                            catch (ObjectDisposedException)
                            {
                                /* already torn down */
                            }
                            catch
                            {
                                /* best effort */
                            }
                        }

                        snapshot.Dispose();
                    }
                    catch
                    {
                        /* best effort */
                    }
                }).WaitAsync(TimeSpan.FromSeconds(50)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                /* Dispose may still be running on the pool; do not call Dispose again here. */
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsMediaLoaded = false;
            if (resetPlayingUi)
                IsPlaying = false;
            CurrentPosition = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            SeekSliderValue = 0;
            LoadMediaCommand.NotifyCanExecuteChanged();
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            SeekToSliderCommand.NotifyCanExecuteChanged();
            if (!deferIdleSync)
                SyncIdleSlate();
        });
    }

    private async Task OpenOrReloadAsync()
    {
        if (!CanLoadMedia())
            return;

        var resumeAfterOpen = false;
        await Dispatcher.UIThread.InvokeAsync(() => resumeAfterOpen = IsPlaying);

        await WithPlaybackArcAsync(async () =>
        {
            await CloseSessionCoreInnerAsync(deferIdleSync: true, resetPlayingUi: !resumeAfterOpen);
            await Dispatcher.UIThread.InvokeAsync(StopIdleSlate);

            var openCtx = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var path = MediaFilePath!;
                var selected = _outputs.Outputs.ToList();
                _outputs.StopPreviewsForPlayback(selected);
                return (Path: path, Selected: selected, Repo: _outputs);
            });

            HaPlayPlaybackSession? created = null;
            string? createErr = null;
            await Task.Run(() =>
            {
                if (!HaPlayPlaybackSession.TryCreate(openCtx.Path, openCtx.Selected, openCtx.Repo, out created,
                        out createErr))
                    created = null;
            }).ConfigureAwait(false);

            // Never use InvokeAsync(async () => await Task.Run(...)): the UI dispatcher can deadlock with the
            // threadpool continuation that is waiting for InvokeAsync to complete.
            HaPlayPlaybackSession? sessionForResume = null;
            var holdFbAfterOpen = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (created is null)
                {
                    StatusMessage = createErr ?? "Failed to open media.";
                    SyncIdleSlate();
                    return;
                }

                _session = created;
                IsMediaLoaded = true;
                StatusMessage = null;
                if (created.Player.Decoder.Audio is ISeekableSource a)
                    Duration = a.Duration;
                else
                    Duration = TimeSpan.Zero;

                created.Player.PlayClock.PositionChanged += OnClockPositionChanged;
                if (!string.IsNullOrWhiteSpace(FallbackImagePath))
                    created.ApplyFallbackImage(FallbackImagePath);
                created.SetHoldFallback(HoldFallbackVideo);

                holdFbAfterOpen = HoldFallbackVideo;
                if (holdFbAfterOpen)
                {
                    try
                    {
                        created.PumpHoldFrames(created.Player.PlayClock.CurrentPosition);
                    }
                    catch
                    {
                        /* best effort */
                    }
                }

                EnsureLoopTimerStarted();
                LoadMediaCommand.NotifyCanExecuteChanged();
                sessionForResume = created;
            });

            if (sessionForResume is null)
                return;

            if (resumeAfterOpen)
            {
                var s = sessionForResume;
                var hf = holdFbAfterOpen;
                try
                {
                    await Task.Run(() =>
                    {
                        s.PrimeVideoOutputsBeforePlay(hf);
                        s.Router.Play(
                            prefillBeforeHardware: null,
                            startHardware: () => s.StartAllPortAudio());
                    }).WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsPlaying = false;
                        StatusMessage = "Playback failed to resume after loading (timed out).";
                    });
                }
                catch
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsPlaying = false;
                        StatusMessage = "Playback failed to resume after loading.";
                    });
                }
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
            if (session is null)
                return;

            if (IsLooping)
            {
                if (!session.Player.Video.CompletedNaturally)
                    return;
                try
                {
                    await Task.Run(() =>
                    {
                        using var seekCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                        session.Router.SeekCoordinatedSkippingSharedMuxFlush(TimeSpan.Zero, seekCts.Token);
                        session.PrimeVideoOutputsBeforePlay(holdFb);
                        session.Router.Play(
                            prefillBeforeHardware: null,
                            startHardware: () => session.StartAllPortAudio());
                    }).WaitAsync(TimeSpan.FromSeconds(18)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    /* do not hold _playbackArc indefinitely */
                }
                catch
                {
                    /* best effort */
                }

                if (HoldFallbackVideo)
                    StartHoldPumpTimer();
                EnsureLoopTimerStarted();
                return;
            }

            var fileEnded = session.Player.Audio?.Router is { } ar
                ? !ar.IsRunning && ar.CompletedNaturally
                : session.Player.Video.CompletedNaturally;

            if (!fileEnded)
                return;

            resumePlayForPlaylist = IsPlaying;
            advancePlaylist = true;
            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        session.Router.PauseSkippingSharedMuxFlush(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        /* bounded pause */
                    }
                    catch
                    {
                        /* best effort */
                    }
                }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                /* bounded pause */
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

        if (!advancePlaylist)
            return;

        var shouldLoadNext = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_session is null || !IsMediaLoaded)
                return;
            IsPlaying = false;
            if (!TryGetNextPlaylistPath(out var nextPath))
                return;
            shouldLoadNext = true;
            MediaFilePath = nextPath;
            SelectedPlaylistPath = nextPath;
        });

        if (!shouldLoadNext)
            return;

        await OpenOrReloadAsync();

        if (!resumePlayForPlaylist)
            return;

        var holdForPrime = false;
        await Dispatcher.UIThread.InvokeAsync(() => { holdForPrime = HoldFallbackVideo; });

        await WithPlaybackArcAsync(async () =>
        {
            HaPlayPlaybackSession? s = null;
            await Dispatcher.UIThread.InvokeAsync(() => { s = _session; });
            if (s is null)
                return;

            var playStarted = false;
            try
            {
                await Task.Run(() =>
                {
                    s.PrimeVideoOutputsBeforePlay(holdForPrime);
                    s.Router.Play(
                        prefillBeforeHardware: null,
                        startHardware: () => s.StartAllPortAudio());
                }).WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                playStarted = true;
            }
            catch (TimeoutException)
            {
                /* best effort */
            }
            catch
            {
                /* best effort */
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!playStarted)
                    return;
                IsPlaying = true;
                if (HoldFallbackVideo)
                    StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private async Task PlayAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            HaPlayPlaybackSession? s = null;
            var holdFb = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                s = _session;
                holdFb = HoldFallbackVideo;
            });
            if (s is null)
                return;

            var playStarted = false;
            try
            {
                await Task.Run(() =>
                {
                    s.PrimeVideoOutputsBeforePlay(holdFb);
                    s.Router.Play(
                        prefillBeforeHardware: null,
                        startHardware: () => s.StartAllPortAudio());
                }).WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                playStarted = true;
            }
            catch (TimeoutException)
            {
                /* best effort */
            }
            catch
            {
                /* best effort */
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!playStarted)
                    return;
                IsPlaying = true;
                if (HoldFallbackVideo)
                    StartHoldPumpTimer();
                EnsureLoopTimerStarted();
            });
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private async Task PauseAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!HoldFallbackVideo)
                    StopHoldPumpTimer();
            });
            HaPlayPlaybackSession? s = null;
            await Dispatcher.UIThread.InvokeAsync(() => { s = _session; });
            if (s is null)
                return;

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        s.Router.PauseSkippingSharedMuxFlush(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        /* bounded pause */
                    }
                    catch
                    {
                        /* best effort */
                    }
                }).WaitAsync(TimeSpan.FromSeconds(14)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                /* best effort */
            }

            await Dispatcher.UIThread.InvokeAsync(() => { IsPlaying = false; });
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanTransport))]
    private async Task StopAsync()
    {
        await WithPlaybackArcAsync(async () =>
        {
            var pumpHoldAfterStop = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StopHoldPumpTimer();
                _loopTimer?.Stop();
                _loopTimer = null;
                IsPlaying = false;
                pumpHoldAfterStop = HoldFallbackVideo;
            });

            HaPlayPlaybackSession? snap = null;
            await Dispatcher.UIThread.InvokeAsync(() => { snap = _session; });
            if (snap is null)
                return;

            var doPump = pumpHoldAfterStop;
            try
            {
                await Task.Run(() =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
                    snap.Router.PauseSkippingSharedMuxFlush(cts.Token);
                }).WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                /* bounded pause */
            }
            catch (OperationCanceledException)
            {
                /* bounded pause */
            }
            catch
            {
                /* best effort */
            }

            // Never bundle PlayClock.Seek with a long demux seek behind one WaitAsync: if the seek times out,
            // the clock never resets and the UI/playhead stay at the old time.
            try
            {
                await Task.Run(() => snap.Player.PlayClock.Seek(TimeSpan.Zero)).WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                /* extremely unlikely */
            }
            catch (ObjectDisposedException)
            {
                /* session closed */
            }
            catch
            {
                /* best effort */
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_session != snap)
                    return;
                CurrentPosition = TimeSpan.Zero;
                SeekSliderValue = 0;
            });

            try
            {
                await Task.Run(() =>
                {
                    snap.Router.Seek(TimeSpan.Zero);
                    if (doPump)
                    {
                        try
                        {
                            snap.PumpHoldFrames(TimeSpan.Zero);
                        }
                        catch
                        {
                            /* best effort */
                        }
                    }
                }).WaitAsync(TimeSpan.FromSeconds(6));
            }
            catch (TimeoutException)
            {
                /* best effort */
            }
            catch (ObjectDisposedException)
            {
                /* session closed */
            }
            catch
            {
                /* best effort */
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_session != snap)
                    return;
                CurrentPosition = TimeSpan.Zero;
                SeekSliderValue = 0;
            });
        }).ConfigureAwait(false);
    }

    private bool CanTransport() => _session is not null && IsMediaLoaded;

    [RelayCommand(CanExecute = nameof(CanSeek))]
    private async Task SeekToSliderAsync()
    {
        if (_session is null || Duration <= TimeSpan.Zero)
            return;
        if (!await _playbackArc.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false))
            return;
        try
        {
            var t = TimeSpan.FromTicks((long)(SeekSliderValue * Duration.Ticks / 1000.0));
            var playing = false;
            var holdFb = false;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                playing = IsPlaying;
                holdFb = HoldFallbackVideo;
            });
            var session = _session;
            if (session is null)
                return;

            try
            {
                await Task.Run(() =>
                {
                    using var seekCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    session.Router.SeekCoordinatedSkippingSharedMuxFlush(t, seekCts.Token);
                    if (playing)
                    {
                        session.PrimeVideoOutputsBeforePlay(holdFb);
                        session.Router.Play(
                            prefillBeforeHardware: null,
                            startHardware: () => session.StartAllPortAudio());
                    }
                }).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                /* release arc — otherwise Load stays disabled forever */
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (HoldFallbackVideo && playing)
                    StartHoldPumpTimer();
                if (playing)
                    EnsureLoopTimerStarted();
            });
        }
        finally
        {
            _playbackArc.Release();
        }
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

        var sig = IdleLogoSlateSession.BuildSignature(HoldFallbackVideo, FallbackImagePath, _outputs.Outputs);
        if (_idleSlate is not null && _idleSlateSig == sig)
            return;

        StopIdleSlate();

        if (!HoldFallbackVideo || string.IsNullOrWhiteSpace(FallbackImagePath) ||
            !File.Exists(FallbackImagePath!))
            return;

        if (!IdleLogoSlateSession.TryStart(_outputs.Outputs.ToList(), _outputs, FallbackImagePath!, out var slate,
                out var err))
        {
            if (!string.IsNullOrWhiteSpace(err))
                StatusMessage = err;
            return;
        }

        _idleSlate = slate;
        _idleSlateSig = sig;
    }
}
