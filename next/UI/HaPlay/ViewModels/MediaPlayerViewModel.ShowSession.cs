using Avalonia.Threading;
using HaPlay.Playback;
using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Interop;
using S.Media.Session;

namespace HaPlay.ViewModels;

/// <summary>
/// 8a.4 convergence (gated by <c>HAPLAY_USE_SHOWSESSION=1</c>): re-backs the media-player deck's FILE playback
/// onto a per-player headless <see cref="ShowSession"/> (via <see cref="MediaPlayerShowMapper"/>) instead of
/// <c>HaPlayPlaybackSession</c>. Off by default → the engine path is untouched. File-core slice: play / pause /
/// resume / stop / seek + position readout; live inputs, logo/hold, and advanced state (fault recovery,
/// end-of-track auto-advance) stay on the engine for now. The transport methods early-return into here only
/// while <see cref="ShowSessionActive"/>.
/// </summary>
public partial class MediaPlayerViewModel
{
    private static readonly ILogger ShowLog = MediaDiagnostics.CreateLogger("HaPlay.MediaPlayer.ShowSession");

    private ShowSession? _playerShowSession;
    private Dictionary<string, IVideoOutput[]> _playerVideoOutputs = new(StringComparer.Ordinal);
    private readonly List<Guid> _playerAcquiredLines = new();
    private DispatcherTimer? _playerShowPoll;

    /// <summary>True while a file is playing through the per-player ShowSession (the transport guards divert here).</summary>
    public bool ShowSessionActive { get; private set; }

    private static bool UseShowSessionPlayer =>
        Environment.GetEnvironmentVariable("HAPLAY_USE_SHOWSESSION") == "1";

    /// <summary>Gated file open: builds/loads the 1-cue player show and fires it. Returns false (and leaves the
    /// engine path to run) when disabled or on any failure.</summary>
    private async Task<bool> TryOpenViaShowSessionAsync(FilePlaylistItem item, IReadOnlyList<OutputLineViewModel> lines)
    {
        if (!UseShowSessionPlayer)
            return false;

        try
        {
            var probe = await CueMediaProbe.TryProbeAsync(item.Path).ConfigureAwait(true);
            var hasVideo = probe?.HasVideo == true;

            _playerShowSession ??= new ShowSession(
                MediaRuntime.Registry,
                MediaRuntime.Registry.AudioBackends.FirstOrDefault(),
                (path, streamIndex, w, h) => SubtitleOverlayFactory.FromFileDeferred(path, w, h, streamIndex),
                (compId, _, _, _) => _playerVideoOutputs.TryGetValue(compId, out var outs) ? outs : Array.Empty<IVideoOutput>());

            // Release the previous play's single-holder video leases before re-acquiring (else they stay held).
            foreach (var held in _playerAcquiredLines)
                _outputs.ReleaseVideoOutputForLine(held);
            _playerAcquiredLines.Clear();

            var outputs = new List<IVideoOutput>();
            if (hasVideo)
            {
                foreach (var line in lines)
                {
                    if (_outputs.AcquireVideoOutputForLine(line.Definition.Id) is not { } o)
                        continue;
                    outputs.Add(o);
                    _playerAcquiredLines.Add(line.Definition.Id);
                }
            }
            _playerVideoOutputs = new Dictionary<string, IVideoOutput[]>(StringComparer.Ordinal)
            {
                [MediaPlayerShowMapper.PlayerCompositionId] = outputs.ToArray(),
            };

            _playerShowSession.LoadDocument(MediaPlayerShowMapper.ToShowDocument(item.Path, hasVideo));
            await _playerShowSession.FireCueAsync(MediaPlayerShowMapper.PlayerCueId).ConfigureAwait(true);

            ShowSessionActive = true;
            MediaFilePath = item.Path;
            IsMediaLoaded = true;
            LoadState = PlayerLoadState.Ready;
            IsPlaying = true;
            StartShowSessionPoll();
            ShowLog.LogInformation("MediaPlayer: file re-backed onto per-player ShowSession (HAPLAY_USE_SHOWSESSION=1).");
            return true;
        }
        catch (Exception ex)
        {
            ShowLog.LogWarning(ex, "MediaPlayer: ShowSession open failed; falling back to the engine path");
            await ShowSessionStopAsync().ConfigureAwait(true);
            return false;
        }
    }

    private Task ShowSessionPauseAsync(bool paused)
    {
        IsPlaying = !paused;
        return _playerShowSession?.SetPausedAsync(paused) ?? Task.CompletedTask;
    }

    private Task ShowSessionSeekAsync(TimeSpan position) =>
        _playerShowSession?.SeekAsync(position) ?? Task.CompletedTask;

    /// <summary>Stops the player ShowSession, releases its video leases, and returns the deck to idle.</summary>
    private async Task ShowSessionStopAsync()
    {
        StopShowSessionPoll();
        if (_playerShowSession is not null)
        {
            try { await _playerShowSession.StopAsync().ConfigureAwait(true); }
            catch (Exception ex) { ShowLog.LogWarning(ex, "MediaPlayer: ShowSession stop"); }
        }
        foreach (var held in _playerAcquiredLines)
            _outputs.ReleaseVideoOutputForLine(held);
        _playerAcquiredLines.Clear();
        _playerVideoOutputs = new(StringComparer.Ordinal);
        ShowSessionActive = false;
        IsPlaying = false;
        IsMediaLoaded = false;
        CurrentPosition = TimeSpan.Zero;
    }

    private void StartShowSessionPoll()
    {
        _playerShowPoll ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playerShowPoll.Tick -= OnShowSessionPollTick;
        _playerShowPoll.Tick += OnShowSessionPollTick;
        _playerShowPoll.Start();
    }

    private void StopShowSessionPoll()
    {
        _playerShowPoll?.Stop();
    }

    private async void OnShowSessionPollTick(object? sender, EventArgs e)
    {
        if (_playerShowSession is null || !ShowSessionActive)
            return;
        try
        {
            var snap = (await _playerShowSession.SnapshotAsync().ConfigureAwait(true))
                .FirstOrDefault(s => s.GroupId == ShowSession.DefaultGroup);
            if (snap is null)
                return;

            Duration = snap.ClipDuration;
            if (!IsScrubbing && !_seekArcRunning)
            {
                CurrentPosition = snap.ClipPosition;
                if (Duration > TimeSpan.Zero)
                    SeekSliderValue = snap.ClipPosition.Ticks * 1000.0 / Duration.Ticks;
            }

            // Natural end: the clip finished and we're not paused → return the deck to idle.
            if (!snap.IsRunning && IsPlaying)
                await ShowSessionStopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowLog.LogTrace("MediaPlayer: ShowSession poll: {Message}", ex.Message);
        }
    }
}
