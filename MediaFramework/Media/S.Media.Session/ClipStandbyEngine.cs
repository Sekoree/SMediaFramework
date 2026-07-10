using S.Media.Players;
using S.Media.Routing;
using S.Media.Core.Registry;
using S.Media.Core;
using S.Media.Time;


namespace S.Media.Session;

/// <summary>Stable identity for a clip standby entry: host cue id plus the host-computed configuration key.</summary>
public readonly record struct ClipKey(string Id, string CacheKey)
{
    public override string ToString() => string.IsNullOrEmpty(CacheKey) ? Id : $"{Id}:{CacheKey}";
}

/// <summary>
/// Host-agnostic media source factory for a cue clip. It is intentionally backed by
/// <see cref="MediaPlayerOpenBuilder"/> so callers can reuse existing file/URI/stream/live open paths.
/// </summary>
public interface IClipMediaSource
{
    string Description { get; }

    MediaPlayerOpenBuilder CreateOpenBuilder();
}

/// <summary>Convenience source factory for common clip inputs.</summary>
public sealed class ClipMediaSource : IClipMediaSource
{
    private readonly Func<MediaPlayerOpenBuilder> _createBuilder;

    private ClipMediaSource(string description, Func<MediaPlayerOpenBuilder> createBuilder)
    {
        Description = string.IsNullOrWhiteSpace(description) ? "(clip source)" : description;
        _createBuilder = createBuilder ?? throw new ArgumentNullException(nameof(createBuilder));
    }

    public string Description { get; }

    public MediaPlayerOpenBuilder CreateOpenBuilder() => _createBuilder();

    public static ClipMediaSource FromBuilder(Func<MediaPlayerOpenBuilder> createBuilder, string? description = null) =>
        new(description ?? "(builder)", createBuilder);

    public static ClipMediaSource File(IMediaRegistry registry, string filePath, MediaPlayerOpenOptions? options = null) =>
        new(filePath, () =>
        {
            var builder = MediaPlayer.OpenFile(registry, filePath);
            if (options is { } opts)
                builder.WithOptions(opts);
            return builder;
        });

    public static ClipMediaSource Uri(IMediaRegistry registry, Uri mediaUri, MediaPlayerOpenOptions? options = null) =>
        new(mediaUri.ToString(), () =>
        {
            var builder = MediaPlayer.OpenUri(registry, mediaUri);
            if (options is { } opts)
                builder.WithOptions(opts);
            return builder;
        });
}

public sealed record AudioRouteSpec(
    string OutputId,
    int SourceChannel,
    int OutputChannel,
    double GainDb = 0,
    bool Muted = false);

/// <param name="Placement">Fit mode within the destination rect (Cover/Contain/Letterbox/Center/Stretch/FillWidth/FillHeight).</param>
/// <param name="DestX">Destination rectangle on the canvas, normalized to [0,1]. Defaults to the full canvas.</param>
/// <param name="CropLeft">Per-edge source crop insets as fractions [0,1). Default 0 = no crop.</param>
public sealed record VideoPlacementSpec(
    string CompositionId,
    int LayerIndex,
    double Opacity = 1,
    string? Placement = null,
    double DestX = 0,
    double DestY = 0,
    double DestWidth = 1,
    double DestHeight = 1,
    double CropLeft = 0,
    double CropTop = 0,
    double CropRight = 0,
    double CropBottom = 0,
    // Clockwise rotation (degrees) of the placed layer about its destination-rect centre. The rotated
    // image overflows its axis-aligned dest rect, as expected - unlike the unrotated fit which trims to
    // the rect for clean split-screen. Default 0 keeps existing placements pixel-identical.
    double RotationDegrees = 0,
    // Optional media-layer mapping/video FX. The mapping is resolved in source-video space, then placed
    // by the normal destination rectangle and fit mode so existing layout controls keep their meaning.
    ClipOutputMappingSpec? VideoFx = null);

/// <summary>
/// What to open and how the host intends to route it. The standby engine owns the open/seek/hold
/// lifecycle; hosts still wire concrete output runtimes from <see cref="AudioRoutes"/> and
/// <see cref="VideoPlacements"/> after <see cref="IClipStandbyEngine.ArmAsync"/>.
/// </summary>
public sealed record ClipSpec
{
    public ClipSpec(
        string id,
        IClipMediaSource source,
        ClipWindow window,
        string cacheKey,
        IReadOnlyList<AudioRouteSpec>? audioRoutes = null,
        IReadOnlyList<VideoPlacementSpec>? videoPlacements = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(cacheKey);
        Id = id;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Window = window;
        CacheKey = cacheKey;
        AudioRoutes = audioRoutes ?? [];
        VideoPlacements = videoPlacements ?? [];
    }

    public string Id { get; }

    public IClipMediaSource Source { get; }

    public ClipWindow Window { get; }

    public string CacheKey { get; }

    public IReadOnlyList<AudioRouteSpec> AudioRoutes { get; }

    public IReadOnlyList<VideoPlacementSpec> VideoPlacements { get; }

    public ClipKey Key => new(Id, CacheKey);
}

public enum ClipPreparationState
{
    Idle,
    Preparing,
    Ready,
    Stale,
    Failed,
}

public readonly record struct ClipPreparationStatus(ClipKey Key, ClipPreparationState State, string? Error);

public sealed record ClipStandbyPolicy(
    int MaxPreparedDecoders = 0,
    int Window = 0,
    /// <summary>Concurrent decoder opens during a standby refresh. <c>0</c> = default (2). Opens are
    /// CPU/IO heavy (container probe + optional HW session + seek-prime), so keep this small; raising
    /// it trades open-burst CPU for a faster warm window on large pre-rolls.</summary>
    int PrepareParallelism = 0)
{
    internal int ResolvedMaxPreparedDecoders => MaxPreparedDecoders <= 0 ? int.MaxValue : Math.Clamp(MaxPreparedDecoders, 1, 4096);
    internal int ResolvedWindow => Window <= 0 ? int.MaxValue : Math.Clamp(Window, 1, 4096);
    internal int ResolvedPrepareParallelism => PrepareParallelism <= 0 ? 2 : Math.Clamp(PrepareParallelism, 1, 8);
}

public interface IPreparedClip : IAsyncDisposable
{
    ClipKey Key { get; }

    ClipSpec Spec { get; }

    ClipPreparationState State { get; }

    string? Error { get; }
}

public interface IArmedClip : IAsyncDisposable
{
    ClipKey Key { get; }

    ClipSpec Spec { get; }

    MediaPlayer Player { get; }

    ClipWindow Window { get; }

    bool IsStarted { get; }

    void Start(
        Action? prefillBeforeHardware = null,
        Action? startHardware = null,
        IPlaybackClock? videoOnlyMaster = null,
        Func<bool>? verifyPrebufferAfterPrefill = null);

    ValueTask ReleaseAsync();
}

public interface IClipStandbyEngine : IAsyncDisposable
{
    event Action<IReadOnlyList<ClipPreparationStatus>>? StandbyStatesChanged;

    Task RefreshStandbyAsync(
        IReadOnlyList<ClipSpec> window,
        ClipStandbyPolicy policy,
        CancellationToken cancellationToken = default);

    Task<IArmedClip> ArmAsync(ClipSpec spec, CancellationToken cancellationToken = default);

    Task RemoveStandbyAsync(string id);

    Task<IReadOnlyList<IArmedClip>> StartGroupAsync(
        IReadOnlyList<ClipSpec> specs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default clip standby implementation: opens clips through <see cref="MediaPlayerOpenBuilder"/>,
/// seeks to the requested <see cref="ClipWindow.Start"/>, keeps ready entries warm, and exposes an
/// explicit Arm/Start barrier for single or grouped cue starts.
/// </summary>
public sealed class ClipStandbyEngine : IClipStandbyEngine
{
    private readonly Lock _gate = new();
    // Serializes whole refresh passes: two overlapping refreshes interleaving remove/store on the
    // same ids could otherwise evict each other's freshly prepared decoders mid-pass.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, PreparedClip> _preparedById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipPreparationStatus> _statusesById = new(StringComparer.Ordinal);
    private bool _disposed;

    public event Action<IReadOnlyList<ClipPreparationStatus>>? StandbyStatesChanged;

    public async Task RefreshStandbyAsync(
        IReadOnlyList<ClipSpec> window,
        ClipStandbyPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (window.Count == 0 || policy.ResolvedWindow == 0 || policy.ResolvedMaxPreparedDecoders == 0)
            {
                await ClearPreparedAsync().ConfigureAwait(false);
                return;
            }

            var candidates = window
                .Take(policy.ResolvedWindow)
                .Take(policy.ResolvedMaxPreparedDecoders)
                .ToArray();
            var keepIds = new HashSet<string>(candidates.Select(c => c.Id), StringComparer.Ordinal);

            // Cheap sequential pass first: mark warm entries Ready and queue the cold ones as
            // Preparing, so the host sees the whole window's state before the slow opens begin.
            var toPrepare = new List<ClipSpec>(candidates.Length);
            foreach (var spec in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (HasMatchingPrepared(spec.Key))
                {
                    SetStatus(spec.Key, ClipPreparationState.Ready);
                    continue;
                }

                await RemovePreparedAsync(spec.Id).ConfigureAwait(false);
                SetStatus(spec.Key, ClipPreparationState.Preparing);
                toPrepare.Add(spec);
            }

            if (toPrepare.Count > 0)
            {
                // Bounded parallel opens: a decoder open + seek-prime can take seconds on heavy
                // files, and the old serial pass made the warm window's total latency the sum of its
                // parts. ForEachAsync dequeues the list in order, so the next cue to fire still
                // starts opening first.
                var parallelism = Math.Min(policy.ResolvedPrepareParallelism, toPrepare.Count);
                await Parallel.ForEachAsync(
                    toPrepare,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                    async (spec, ct) =>
                    {
                        try
                        {
                            var prepared = await PrepareAsync(spec, ct).ConfigureAwait(false);
                            await StorePreparedAsync(prepared).ConfigureAwait(false);
                            SetStatus(spec.Key, ClipPreparationState.Ready);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            SetStatus(spec.Key, ClipPreparationState.Failed, ex.Message);
                        }
                    }).ConfigureAwait(false);
            }

            await EvictPreparedExceptAsync(keepIds, policy.ResolvedMaxPreparedDecoders).ConfigureAwait(false);
            ClearStatusesExcept(keepIds);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<IArmedClip> ArmAsync(ClipSpec spec, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(spec);

        var prepared = TakePrepared(spec.Key);
        if (prepared is null)
        {
            var removed = await RemovePreparedAsync(spec.Id).ConfigureAwait(false);
            var statusChanged = SetStatus(new ClipKey(spec.Id, string.Empty), ClipPreparationState.Idle);
            if (removed && !statusChanged)
                RaiseStandbyStatesChanged();
            prepared = await PrepareAsync(spec, cancellationToken).ConfigureAwait(false);
            return new ArmedClip(this, prepared, wasPrepared: false);
        }

        SetStatus(spec.Key, ClipPreparationState.Idle);
        return new ArmedClip(this, prepared, wasPrepared: true);
    }

    public async Task RemoveStandbyAsync(string id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var removed = await RemovePreparedAsync(id).ConfigureAwait(false);
        var statusChanged = SetStatus(new ClipKey(id, string.Empty), ClipPreparationState.Idle);
        if (removed && !statusChanged)
            RaiseStandbyStatesChanged();
    }

    /// <summary>
    /// Arms every spec in parallel, then starts them sequentially. Note the sync characteristics:
    /// each <see cref="IArmedClip.Start"/> includes that clip's prefill/buffer wait, so group members
    /// begin staggered by their predecessors' start costs. Hosts that need tighter cross-clip alignment
    /// should wire their audio paused, start all transports, and unpause collectively (the pattern
    /// HaPlay's cue engine uses) - pausing is a host-source concern this engine cannot impose.
    /// </summary>
    public async Task<IReadOnlyList<IArmedClip>> StartGroupAsync(
        IReadOnlyList<ClipSpec> specs,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(specs);

        var armed = new List<IArmedClip>(specs.Count);
        try
        {
            var tasks = specs.Select(spec => ArmAsync(spec, cancellationToken)).ToArray();
            armed.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));

            foreach (var clip in armed)
                clip.Start();

            return armed;
        }
        catch
        {
            foreach (var clip in armed)
                await clip.ReleaseAsync().ConfigureAwait(false);
            throw;
        }
    }

    public bool MarkStandbyStale(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        PreparedClip? prepared;
        lock (_gate)
            prepared = _preparedById.GetValueOrDefault(id);
        if (prepared is null)
            return false;

        SetStatus(prepared.Key, ClipPreparationState.Stale);
        return true;
    }

    public IReadOnlyList<ClipKey> PreparedKeys
    {
        get
        {
            lock (_gate)
                return _preparedById.Values.Select(p => p.Key).ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        await ClearPreparedAsync().ConfigureAwait(false);
    }

    private static async Task<PreparedClip> PrepareAsync(ClipSpec spec, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var builder = spec.Source.CreateOpenBuilder();
                    builder.Cancellation = cancellationToken; // abort a slow cold open mid-flight on STOP/cancel (NXT-03)
                    return MediaSession.Owning(builder.Build());
                },
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (spec.Window.Start > TimeSpan.Zero)
            {
                // The caller's token flows into SeekCoordinated so an aborted arm/refresh cancels a long
                // in-flight decode-to-target prime (CancelInFlightSeek) instead of letting it run on.
                await Task.Run(
                        () => session.Player.SeekCoordinated(
                            spec.Window.Start,
                            cancellationToken,
                            PauseFlushPolicy.SkipFlush),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return new PreparedClip(spec, session);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool HasMatchingPrepared(ClipKey key)
    {
        lock (_gate)
        {
            return _preparedById.TryGetValue(key.Id, out var prepared)
                   && prepared.Key == key;
        }
    }

    private PreparedClip? TakePrepared(ClipKey key)
    {
        lock (_gate)
        {
            if (!_preparedById.TryGetValue(key.Id, out var prepared) || prepared.Key != key)
                return null;

            _preparedById.Remove(key.Id);
            return prepared;
        }
    }

    private async Task StorePreparedAsync(PreparedClip prepared)
    {
        PreparedClip? replaced = null;
        var engineDisposed = false;
        lock (_gate)
        {
            // A prepare finishing after DisposeAsync's ClearPreparedAsync must not park a live
            // decoder in the (already drained) map - that would leak it until process exit.
            if (_disposed)
            {
                engineDisposed = true;
            }
            else
            {
                if (_preparedById.Remove(prepared.Key.Id, out var existing))
                    replaced = existing;
                _preparedById[prepared.Key.Id] = prepared;
            }
        }

        if (engineDisposed)
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (replaced is not null)
            await replaced.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<bool> RemovePreparedAsync(string id)
    {
        PreparedClip? prepared;
        lock (_gate)
            _preparedById.Remove(id, out prepared);
        if (prepared is not null)
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private async Task ClearPreparedAsync()
    {
        PreparedClip[] prepared;
        bool hadStatuses;
        lock (_gate)
        {
            prepared = _preparedById.Values.ToArray();
            _preparedById.Clear();
            hadStatuses = _statusesById.Count > 0;
            _statusesById.Clear();
        }

        foreach (var item in prepared)
            await item.DisposeAsync().ConfigureAwait(false);
        if (prepared.Length > 0 || hadStatuses)
            RaiseStandbyStatesChanged();
    }

    private async Task EvictPreparedExceptAsync(IReadOnlySet<string> keepIds, int maxEntries)
    {
        List<PreparedClip> toDispose = [];
        lock (_gate)
        {
            foreach (var id in _preparedById.Keys.Where(id => !keepIds.Contains(id)).ToArray())
            {
                toDispose.Add(_preparedById[id]);
                _preparedById.Remove(id);
            }

            while (_preparedById.Count > maxEntries)
            {
                var oldest = _preparedById
                    .OrderBy(kv => kv.Value.CreatedUtc)
                    .First()
                    .Key;
                toDispose.Add(_preparedById[oldest]);
                _preparedById.Remove(oldest);
            }
        }

        foreach (var prepared in toDispose)
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            SetStatus(prepared.Key, ClipPreparationState.Idle);
        }
    }

    private void ClearStatusesExcept(IReadOnlySet<string> keepIds)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var id in _statusesById.Keys.Where(id => !keepIds.Contains(id)).ToArray())
                changed |= _statusesById.Remove(id);
        }

        if (changed)
            RaiseStandbyStatesChanged();
    }

    private bool SetStatus(ClipKey key, ClipPreparationState state, string? error = null)
    {
        bool changed;
        lock (_gate)
        {
            if (state == ClipPreparationState.Idle)
            {
                changed = _statusesById.Remove(key.Id);
            }
            else
            {
                var status = new ClipPreparationStatus(key, state, error);
                changed = !_statusesById.TryGetValue(key.Id, out var current) || current != status;
                if (changed)
                    _statusesById[key.Id] = status;
            }
        }

        if (changed)
            RaiseStandbyStatesChanged();
        return changed;
    }

    private void RaiseStandbyStatesChanged()
    {
        ClipPreparationStatus[] snapshot;
        lock (_gate)
            snapshot = _statusesById.Values.OrderBy(s => s.Key.Id, StringComparer.Ordinal).ToArray();
        StandbyStatesChanged?.Invoke(snapshot);
    }

    private async ValueTask ReturnPreparedAsync(PreparedClip prepared)
    {
        if (_disposed)
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await StorePreparedAsync(prepared).ConfigureAwait(false);
        SetStatus(prepared.Key, ClipPreparationState.Ready);
    }

    private sealed class PreparedClip : IPreparedClip
    {
        public PreparedClip(ClipSpec spec, MediaSession session)
        {
            Spec = spec ?? throw new ArgumentNullException(nameof(spec));
            Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public ClipKey Key => Spec.Key;

        public ClipSpec Spec { get; }

        public MediaSession Session { get; }

        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        public ClipPreparationState State => ClipPreparationState.Ready;

        public string? Error => null;

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class ArmedClip : IArmedClip
    {
        private readonly ClipStandbyEngine _owner;
        private readonly PreparedClip _prepared;
        private readonly bool _wasPrepared;
        private bool _released;

        public ArmedClip(ClipStandbyEngine owner, PreparedClip prepared, bool wasPrepared)
        {
            _owner = owner;
            _prepared = prepared;
            _wasPrepared = wasPrepared;
        }

        public ClipKey Key => _prepared.Key;

        public ClipSpec Spec => _prepared.Spec;

        public MediaPlayer Player => _prepared.Session.Player;

        public ClipWindow Window => _prepared.Spec.Window;

        public bool IsStarted { get; private set; }

        public void Start(
            Action? prefillBeforeHardware = null,
            Action? startHardware = null,
            IPlaybackClock? videoOnlyMaster = null,
            Func<bool>? verifyPrebufferAfterPrefill = null)
        {
            ObjectDisposedException.ThrowIf(_released, this);
            if (IsStarted)
                return;

            Player.Play(prefillBeforeHardware, startHardware, videoOnlyMaster, verifyPrebufferAfterPrefill);
            IsStarted = true;
        }

        public ValueTask ReleaseAsync()
        {
            if (_released)
                return ValueTask.CompletedTask;
            _released = true;

            if (!IsStarted && _wasPrepared)
                return _owner.ReturnPreparedAsync(_prepared);

            return _prepared.DisposeAsync();
        }

        public ValueTask DisposeAsync() => ReleaseAsync();
    }
}
