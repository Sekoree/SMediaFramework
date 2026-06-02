namespace S.Media.Playback;

/// <summary>User-facing transport state for <see cref="MediaPlayerController"/>.</summary>
public enum MediaPlayerControllerState
{
    Stopped,
    Opening,
    Ready,
    Playing,
    Paused,
    Buffering,
    Faulted,
    Disposed,
}

/// <summary>Stable snapshot for UI binding, logs, and health endpoints.</summary>
public sealed record MediaPlayerControllerSnapshot(
    MediaPlayerControllerState State,
    MediaGraphTopology Topology,
    string Description,
    TimeSpan Position,
    TimeSpan Duration,
    Exception? Fault,
    MediaPlayerMetrics Health,
    MediaTrackSelection TrackSelection,
    SubtitlePlan? SubtitlePlan,
    PlaybackRatePlan PlaybackRate,
    AbRepeatPlan? AbRepeat,
    IReadOnlyList<PlaylistItem> Playlist,
    IReadOnlyList<DeviceHotplugEvent> DeviceEvents);

public sealed record MediaTrackSelection(
    int? AudioTrack = null,
    int? VideoTrack = null,
    int? SubtitleTrack = null);

public sealed record SubtitlePlan(
    bool Enabled,
    string? Language = null,
    string? ExternalPath = null);

public sealed record PlaybackRatePlan(
    double Rate = 1.0,
    bool AudioTimeStretch = true);

public sealed record FrameSnapshotRequest(
    TimeSpan Position,
    string? OutputPath = null);

public sealed record AbRepeatPlan(
    TimeSpan A,
    TimeSpan B,
    bool Enabled = true);

public sealed record PlaylistItem(
    string Id,
    string Uri,
    bool Gapless = false);

public sealed record ScrubExtractionRequest(
    TimeSpan Position,
    TimeSpan? Window = null,
    bool IncludeWaveform = false);

public sealed record DeviceHotplugEvent(
    string DeviceId,
    string Kind,
    bool Connected,
    DateTimeOffset Timestamp);

/// <summary>
/// VLC-style controller facade over a built <see cref="MediaGraph"/>. It owns the graph, exposes
/// explicit lifecycle/transport state, and leaves low-level router/player access available through
/// <see cref="Graph"/> for advanced hosts.
/// </summary>
public sealed class MediaPlayerController : IDisposable, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly List<PlaylistItem> _playlist = [];
    private readonly List<DeviceHotplugEvent> _deviceEvents = [];
    private MediaPlayerControllerState _state;
    private Exception? _fault;
    private MediaTrackSelection _trackSelection = new();
    private SubtitlePlan? _subtitlePlan;
    private PlaybackRatePlan _playbackRate = new();
    private AbRepeatPlan? _abRepeat;
    private FrameSnapshotRequest? _lastFrameSnapshotRequest;
    private ScrubExtractionRequest? _lastScrubRequest;

    public MediaPlayerController(MediaGraph graph)
    {
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _state = MediaPlayerControllerState.Ready;
    }

    public MediaGraph Graph { get; }

    public MediaPlayer Player => Graph.Player;

    public MediaPlayerControllerState State
    {
        get { lock (_gate) return _state; }
    }

    public Exception? Fault
    {
        get { lock (_gate) return _fault; }
    }

    public MediaPlayerControllerSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new MediaPlayerControllerSnapshot(
                _state,
                Graph.Topology,
                Graph.Description,
                Player.PlayClock.CurrentPosition,
                Player.Duration,
                _fault,
                Graph.GetHealthSnapshot(),
                _trackSelection,
                _subtitlePlan,
                _playbackRate,
                _abRepeat,
                _playlist.ToArray(),
                _deviceEvents.ToArray());
        }
    }

    public void Play() => RunTransport(() =>
    {
        Player.Play();
        SetState(MediaPlayerControllerState.Playing);
    });

    public void Pause(CancellationToken cancellationToken = default) => RunTransport(() =>
    {
        Player.Pause(cancellationToken);
        SetState(MediaPlayerControllerState.Paused);
    });

    /// <summary>Pauses playback and seeks to the start when supported.</summary>
    public void Stop(CancellationToken cancellationToken = default) => RunTransport(() =>
    {
        Player.Pause(cancellationToken);
        Player.Seek(TimeSpan.Zero);
        SetState(MediaPlayerControllerState.Stopped);
    });

    public void Seek(TimeSpan position) => RunTransport(() => Player.Seek(position));

    public void SelectTracks(MediaTrackSelection selection)
    {
        lock (_gate)
            _trackSelection = selection;
    }

    public void SetSubtitlePlan(SubtitlePlan? plan)
    {
        lock (_gate)
            _subtitlePlan = plan;
    }

    public void SetPlaybackRate(PlaybackRatePlan plan)
    {
        if (plan.Rate <= 0)
            throw new ArgumentOutOfRangeException(nameof(plan), "playback rate must be positive.");
        lock (_gate)
            _playbackRate = plan;
    }

    public void SetAbRepeat(AbRepeatPlan? plan)
    {
        if (plan is not null && plan.B <= plan.A)
            throw new ArgumentException("AB repeat B must be after A.", nameof(plan));
        lock (_gate)
            _abRepeat = plan;
    }

    public FrameSnapshotRequest RequestFrameSnapshot(FrameSnapshotRequest request)
    {
        lock (_gate)
            _lastFrameSnapshotRequest = request;
        return request;
    }

    public ScrubExtractionRequest RequestScrubExtraction(ScrubExtractionRequest request)
    {
        lock (_gate)
            _lastScrubRequest = request;
        return request;
    }

    public FrameSnapshotRequest? LastFrameSnapshotRequest
    {
        get { lock (_gate) return _lastFrameSnapshotRequest; }
    }

    public ScrubExtractionRequest? LastScrubExtractionRequest
    {
        get { lock (_gate) return _lastScrubRequest; }
    }

    public void SetPlaylist(IEnumerable<PlaylistItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        lock (_gate)
        {
            _playlist.Clear();
            _playlist.AddRange(items);
        }
    }

    public void ReportDeviceHotplug(DeviceHotplugEvent deviceEvent)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceEvent.DeviceId);
        ArgumentException.ThrowIfNullOrEmpty(deviceEvent.Kind);
        lock (_gate)
            _deviceEvents.Add(deviceEvent);
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == MediaPlayerControllerState.Disposed)
                return;
            _state = MediaPlayerControllerState.Disposed;
        }
        Graph.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_state == MediaPlayerControllerState.Disposed)
                return;
            _state = MediaPlayerControllerState.Disposed;
        }
        await Graph.DisposeAsync().ConfigureAwait(false);
    }

    private void RunTransport(Action action)
    {
        ThrowIfDisposedOrFaulted();
        try
        {
            action();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _fault = ex;
                _state = MediaPlayerControllerState.Faulted;
            }
            throw;
        }
    }

    private void ThrowIfDisposedOrFaulted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == MediaPlayerControllerState.Disposed, this);
            if (_state == MediaPlayerControllerState.Faulted)
                throw new InvalidOperationException("Controller is faulted; close it and build a new graph.", _fault);
        }
    }

    private void SetState(MediaPlayerControllerState state)
    {
        lock (_gate)
            _state = state;
    }
}
