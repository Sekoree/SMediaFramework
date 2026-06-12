using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using S.Media.Core.Clock;
using S.Media.Core.Diagnostics;
using S.Media.Core.Playback;
using S.Media.FFmpeg;
using S.Media.Playback;

namespace HaPlay.Playback;

/// <summary>Everything the engine needs to fire one tile. The view model resolves board defaults
/// (output line etc.) before building this, so the engine stays board-agnostic.</summary>
public readonly record struct SoundboardPlayRequest(
    Guid TileId,
    string FilePath,
    Guid OutputLineId,
    double Volume,
    bool Loop,
    int FadeOutMs);

/// <summary>Periodic progress sample for a playing tile. <see cref="FadeRemaining"/> is non-null
/// only while a tap-to-fade is running (drives the tile's fade countdown bar).</summary>
public readonly record struct SoundboardSoundProgress(
    Guid TileId,
    TimeSpan Position,
    TimeSpan Duration,
    TimeSpan? FadeRemaining);

/// <summary>
/// Soundboard playback runtime: one audio-only <see cref="MediaPlayer"/> per playing tile, mixed
/// into the same per-line <see cref="ClipAudioOutputRuntime"/> pool as cue audio (via
/// <see cref="CuePlaybackEngine.AcquireSharedAudioRuntime"/> — the underlying device lease is
/// exclusive, so the pool must be shared for a tile and a cue to sound through one line).
/// Loop wraps and fade-outs reuse the same primitives as the cue engine (coordinated seek to zero,
/// <see cref="ClipAudioOutputRuntime.FadeOutSourceAsync"/>).
/// </summary>
public sealed class SoundboardEngine : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.Playback.SoundboardEngine");
    private static readonly TimeSpan BoundedDisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly CuePlaybackEngine _cueEngine;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ActiveSound> _active = new();
    private bool _disposed;

    public SoundboardEngine(CuePlaybackEngine cueEngine)
    {
        _cueEngine = cueEngine ?? throw new ArgumentNullException(nameof(cueEngine));
    }

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler<Guid>? SoundStarted;

    /// <summary>Raised on the UI thread once per sound, for natural ends, stops and fade completions.</summary>
    public event EventHandler<Guid>? SoundEnded;

    /// <summary>Raised on the UI thread (~10 Hz per playing tile).</summary>
    public event EventHandler<SoundboardSoundProgress>? SoundProgress;

    public bool IsPlaying(Guid tileId)
    {
        lock (_gate) return _active.ContainsKey(tileId);
    }

    /// <summary>Opens and starts one tile. A tile that is already playing is restarted. Returns an
    /// operator-facing error string, or null on success.</summary>
    public async Task<string?> PlayAsync(SoundboardPlayRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return "Tile has no sound file.";
        if (request.OutputLineId == Guid.Empty)
            return "Tile has no output line (set one on the tile or as the board default).";

        // Retrigger: tear the old instance down first so the new one owns the tile id.
        await StopAsync(request.TileId).ConfigureAwait(false);

        MediaPlayer? player = null;
        ActiveSound? entry = null;
        try
        {
            string? error = null;
            player = await Task.Run(() =>
            {
                var builder = MediaPlayer.OpenFile(request.FilePath)
                    .WithOptions(new MediaPlayerOpenOptions(
                        TryHardwareAcceleration: false,
                        IncludeAudioRouter: false)
                    {
                        // Sound-only playback: never elect a video stream, even for video files —
                        // the demux runs its audio-only stub video path (same as sound-only cues).
                        VideoStreamIndex = MediaStreamSelection.Disabled,
                    })
                    .WithDecoderOwnership(MediaPlayerDecoderOwnership.BundleDisposesDecoder);
                if (!builder.TryBuild(out var built, out var err))
                {
                    error = err ?? "Failed to open sound file.";
                    return null;
                }
                return built;
            }).ConfigureAwait(false);

            if (player is null)
                return error;
            if (!player.HasContainerDecoder || !player.Decoder.HasAudio)
                return "File has no audio stream.";

            var runtime = _cueEngine.AcquireSharedAudioRuntime(request.OutputLineId);
            if (runtime is null)
                return "The tile's output line is not available.";

            entry = new ActiveSound(
                request.TileId,
                player,
                runtime,
                request.Loop,
                Math.Max(0, request.FadeOutMs),
                player.Duration > TimeSpan.Zero ? player.Duration : TimeSpan.Zero);

            // Route ids are captured so SetVolume can rewrite the routes' base gains live.
            var routeIds = new List<string>();
            entry.SourceId = runtime.AddSource(
                player.Decoder.Audio,
                BuildRouteSpecs(runtime, player.Decoder.Audio.Format.Channels, request.Volume),
                $"sb_{request.TileId:N}",
                (sourceId, ordinal) =>
                {
                    var routeId = $"{sourceId}_r{ordinal}";
                    routeIds.Add(routeId);
                    return routeId;
                });
            entry.RouteIds = routeIds;
            player = null; // owned by the entry (and its cleanup path) from here on

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _active[request.TileId] = entry;
            }

            // Transport start off the UI thread (Play blocks on its prefill).
            var started = entry;
            await Task.Run(() =>
            {
                started.Player.Play(videoOnlyMaster: runtime.PlaybackClock);
                runtime.EnsureStarted();
            }).ConfigureAwait(false);

            Dispatcher.UIThread.Post(() => SoundStarted?.Invoke(this, request.TileId));
            _ = WatchSoundAsync(entry);
            return null;
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "SoundboardEngine.PlayAsync: {Path}", request.FilePath);
            if (entry is not null)
                await CleanupAsync(entry).ConfigureAwait(false); // unroutes + disposes the player
            return ex.Message;
        }
        finally
        {
            if (player is not null)
                await DisposePlayerBoundedAsync(player).ConfigureAwait(false);
        }
    }

    /// <summary>Starts the tile's configured fade-out and stops it when the ramp lands. Falls back
    /// to an immediate stop when the tile has no fade time. No-op while a fade is already running
    /// (the second tap's "stop now" semantics are the view model's call via <see cref="StopAsync"/>).</summary>
    public async Task FadeOutAsync(Guid tileId)
    {
        ActiveSound? entry;
        lock (_gate)
            _active.TryGetValue(tileId, out entry);
        if (entry is null)
            return;

        if (entry.FadeOutMs <= 0)
        {
            await StopAsync(tileId).ConfigureAwait(false);
            return;
        }

        if (!entry.TryBeginFade())
            return;

        var duration = TimeSpan.FromMilliseconds(entry.FadeOutMs);
        entry.FadeDeadlineTicks = Environment.TickCount64 + entry.FadeOutMs;
        try
        {
            await entry.Runtime.FadeOutSourceAsync(entry.SourceId, duration, entry.Cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; /* stopped mid-fade — cleanup already ran */ }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "SoundboardEngine.FadeOutAsync: {Tile}", tileId);
        }

        await CleanupAsync(entry).ConfigureAwait(false);
    }

    /// <summary>Live volume for a playing tile: rewrites the routes' BASE gains (not a scale), so a
    /// later fade-out ramps from the new level. No-op while a fade runs — the fade stepper works
    /// from its own gain snapshot and would overwrite the change on its next step anyway.</summary>
    public void SetVolume(Guid tileId, double volume)
    {
        ActiveSound? entry;
        lock (_gate)
            _active.TryGetValue(tileId, out entry);
        if (entry is null || entry.FadeDeadlineTicks is not null)
            return;

        var (gainDb, muted) = VolumeToGain(volume);
        foreach (var routeId in entry.RouteIds)
        {
            try { entry.Runtime.SetRouteGain(entry.SourceId, routeId, gainDb, muted); }
            catch (Exception ex) { Trace.LogWarning(ex, "SoundboardEngine.SetVolume: {Route}", routeId); }
        }
    }

    /// <summary>Immediate stop (also used to cut a running fade short).</summary>
    public async Task StopAsync(Guid tileId)
    {
        ActiveSound? entry;
        lock (_gate)
            _active.TryGetValue(tileId, out entry);
        if (entry is not null)
            await CleanupAsync(entry).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        List<ActiveSound> entries;
        lock (_gate)
            entries = _active.Values.ToList();
        await Task.WhenAll(entries.Select(CleanupAsync)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        List<ActiveSound> entries;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _active.Values.ToList();
            _active.Clear();
        }

        foreach (var entry in entries)
        {
            if (!entry.TryBeginStop())
                continue;
            try { entry.Cts.Cancel(); } catch { /* best effort */ }
            try { entry.Runtime.RemoveSource(entry.SourceId); } catch { /* best effort */ }
            MediaDiagnostics.SwallowDisposeErrors(entry.Player.Dispose, "SoundboardEngine.Dispose: player");
            entry.Cts.Dispose();
        }
    }

    /// <summary>Linear 0..1 volume → route gain. Zero is a mute, not −∞ dB.</summary>
    private static (double GainDb, bool Muted) VolumeToGain(double volume)
    {
        var clamped = Math.Clamp(volume, 0.0, 1.0);
        var muted = clamped <= 0.0001;
        return (muted ? 0 : 20.0 * Math.Log10(clamped), muted);
    }

    private static List<AudioRouteSpec> BuildRouteSpecs(
        ClipAudioOutputRuntime runtime,
        int sourceChannels,
        double volume)
    {
        var (gainDb, muted) = VolumeToGain(volume);
        var outChannels = runtime.OutputFormat.Channels;

        var specs = new List<AudioRouteSpec>();
        if (sourceChannels <= 1)
        {
            // Mono sound: feed every output channel pair-wise up to stereo (the common case for a
            // soundboard is a mono stinger on a stereo PA).
            specs.Add(new AudioRouteSpec(runtime.OutputId, 0, 1, gainDb, muted));
            if (outChannels >= 2)
                specs.Add(new AudioRouteSpec(runtime.OutputId, 0, 2, gainDb, muted));
        }
        else
        {
            for (var ch = 0; ch < Math.Min(sourceChannels, outChannels); ch++)
                specs.Add(new AudioRouteSpec(runtime.OutputId, ch, ch + 1, gainDb, muted));
        }

        return specs;
    }

    private async Task WatchSoundAsync(ActiveSound entry)
    {
        var ct = entry.Cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);

                TimeSpan pos;
                try { pos = entry.Player.PlayClock.CurrentPosition; }
                catch { continue; }

                TimeSpan? fadeRemaining = null;
                if (entry.FadeDeadlineTicks is { } deadline)
                    fadeRemaining = TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64));

                var progress = new SoundboardSoundProgress(entry.TileId, pos, entry.Duration, fadeRemaining);
                Dispatcher.UIThread.Post(() => SoundProgress?.Invoke(this, progress));

                if (entry.Duration <= TimeSpan.Zero || pos < entry.Duration)
                    continue;

                if (entry.Loop)
                {
                    // Wrap like a loop cue: coordinated seek back to zero, restart transport, and
                    // re-Play the shared router (it auto-stops if this was its only source and the
                    // decoder hit EOF before the seek landed). Wrapping keeps running during a fade
                    // so a fading loop stays audible until the ramp lands.
                    await Task.Run(() =>
                    {
                        entry.Player.SeekCoordinated(TimeSpan.Zero, CancellationToken.None, PauseFlushPolicy.SkipFlush);
                        entry.Player.Play(videoOnlyMaster: entry.Runtime.PlaybackClock);
                        entry.Runtime.EnsureStarted();
                    }, ct).ConfigureAwait(false);
                    continue;
                }

                if (entry.FadeDeadlineTicks is not null)
                    continue; // fading a non-loop sound past its end — FadeOutAsync owns the cleanup

                await CleanupAsync(entry).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "SoundboardEngine.WatchSoundAsync: {Tile}", entry.TileId);
            await CleanupAsync(entry).ConfigureAwait(false);
        }
    }

    private async Task CleanupAsync(ActiveSound entry)
    {
        if (!entry.TryBeginStop())
            return;

        try { entry.Cts.Cancel(); } catch { /* best effort */ }

        lock (_gate)
            _active.Remove(entry.TileId);

        // Stop the router pulling from the decoder before tearing the player down.
        try { entry.Runtime.RemoveSource(entry.SourceId); }
        catch (Exception ex) { Trace.LogWarning(ex, "SoundboardEngine.CleanupAsync: remove source"); }

        await DisposePlayerBoundedAsync(entry.Player).ConfigureAwait(false);
        entry.Cts.Dispose();

        try { _cueEngine.ReleaseUnusedSharedAudioRuntimes(); }
        catch (Exception ex) { Trace.LogWarning(ex, "SoundboardEngine.CleanupAsync: release runtimes"); }

        Dispatcher.UIThread.Post(() => SoundEnded?.Invoke(this, entry.TileId));
    }

    private static async Task DisposePlayerBoundedAsync(MediaPlayer player)
    {
        // Transport teardown joins decode threads — keep it off the caller and bounded
        // (framework Pause/Stop can nest into multi-second waits on heavy files).
        try
        {
            await Task.Run(player.Dispose).WaitAsync(BoundedDisposeTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.LogWarning(ex, "SoundboardEngine: bounded player dispose");
        }
    }

    private sealed class ActiveSound
    {
        public ActiveSound(
            Guid tileId,
            MediaPlayer player,
            ClipAudioOutputRuntime runtime,
            bool loop,
            int fadeOutMs,
            TimeSpan duration)
        {
            TileId = tileId;
            Player = player;
            Runtime = runtime;
            Loop = loop;
            FadeOutMs = fadeOutMs;
            Duration = duration;
        }

        public Guid TileId { get; }
        public MediaPlayer Player { get; }
        public ClipAudioOutputRuntime Runtime { get; }
        public bool Loop { get; }
        public int FadeOutMs { get; }
        public TimeSpan Duration { get; }
        public string SourceId { get; set; } = "";
        public IReadOnlyList<string> RouteIds { get; set; } = [];
        public CancellationTokenSource Cts { get; } = new();

        /// <summary>TickCount64 deadline of the running fade; null while not fading. Written by
        /// FadeOutAsync, read by the watcher for the countdown progress events.</summary>
        public long? FadeDeadlineTicks { get; set; }

        private int _stopStarted;
        private int _fadeStarted;

        public bool TryBeginStop() => Interlocked.Exchange(ref _stopStarted, 1) == 0;

        public bool TryBeginFade() => Interlocked.Exchange(ref _fadeStarted, 1) == 0;
    }
}
