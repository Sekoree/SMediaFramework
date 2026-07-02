using S.Media.Core.Audio;

namespace S.Media.Session;

/// <summary>
/// The session's independent-players surface: soundboard voices (polyphonic keyed one-shots, each a fresh
/// player on an output) and the single cue preview (a loaded cue auditioned on a separate device) — playback
/// that is deliberately OUTSIDE the transport groups. Owns the voice/preview registries and their end
/// monitors; state is dispatcher-confined exactly like the session's (every mutation marshals through
/// <see cref="ShowSession.InvokeAsync{T}"/>), and media opens run OFF the dispatcher with a published claim
/// CTS so a stop / re-fire / dispose preempts them (NXT-19). Split out of <see cref="ShowSession"/> along its
/// ownership seam (review Part-5 #2); the session's public voice/preview API delegates here.
/// </summary>
internal sealed class VoicePlayer
{
    private readonly ShowSession _session;
    private readonly ClipStandbyEngine _standby;
    private readonly IAudioBackend? _audioBackend;
    private readonly string? _outputDeviceId;
    // Spec builders stay on the session (they read _clipsByCue / the registry / the device-rate cache); both
    // run inside a dispatcher work item, so they may read dispatcher-confined session state.
    private readonly Func<string, ClipSpec?> _buildPreviewSpec;
    private readonly Func<string, string, string?, ClipSpec> _buildVoiceSpec;

    // Preview playback (a loaded cue auditioned on a separate device, independent of the transport groups).
    private IArmedClip? _previewClip;
    private IReadOnlyList<IAudioOutput> _previewOutputs = [];
    private CancellationTokenSource? _previewCts;
    // Soundboard voices (task #10): polyphonic one-shots, each a fresh MediaPlayer on an output, keyed by a
    // host id (the GUI's soundboard tile). Owned by the dispatcher.
    private readonly Dictionary<string, VoiceHandle> _voices = new(StringComparer.Ordinal);
    // Voice opens in flight (NXT-19): voiceId → the open's claim CTS, published so a stop / re-fire / dispose
    // preempts the OFF-dispatcher open before it commits. Owned by the dispatcher; a canceller only Cancel()s —
    // the open flow that created the CTS is the one that disposes it (the blocked open still holds its token).
    private readonly Dictionary<string, CancellationTokenSource> _pendingVoiceOpens = new(StringComparer.Ordinal);

    private sealed record VoiceHandle(
        IArmedClip Clip, IReadOnlyList<IAudioOutput> Outputs, string OutputId, CancellationTokenSource Cts);

    // Lock-free query view (NXT-16 residue): the current voices (id + player), republished on the dispatcher
    // whenever a voice commits or releases, so the soundboard's 200 ms progress poll and the is-playing query
    // never round-trip the dispatcher — a parked loop must not freeze the tiles.
    private volatile VoiceView[] _voiceViews = [];

    private sealed record VoiceView(string Id, S.Media.Players.MediaPlayer Player);

    /// <summary>Republishes the lock-free voice view. Call on the dispatcher after <see cref="_voices"/> changes.</summary>
    private void PublishVoiceViews() =>
        _voiceViews = _voices.Select(kv => new VoiceView(kv.Key, kv.Value.Clip.Player)).ToArray();

    /// <summary>Raised (with the voice id) when a voice ends on its own. Raised from the session dispatcher;
    /// <see cref="ShowSession"/> forwards it to its public event.</summary>
    public event Action<string>? VoiceEnded;

    /// <summary>Raised (with the cue id) when a preview ends on its own. Raised from the session dispatcher;
    /// <see cref="ShowSession"/> forwards it to its public event.</summary>
    public event Action<string>? PreviewEnded;

    public VoicePlayer(
        ShowSession session,
        ClipStandbyEngine standby,
        IAudioBackend? audioBackend,
        string? outputDeviceId,
        Func<string, ClipSpec?> buildPreviewSpec,
        Func<string, string, string?, ClipSpec> buildVoiceSpec)
    {
        _session = session;
        _standby = standby;
        _audioBackend = audioBackend;
        _outputDeviceId = outputDeviceId;
        _buildPreviewSpec = buildPreviewSpec;
        _buildVoiceSpec = buildVoiceSpec;
    }

    // --- preview ------------------------------------------------------------------------------------

    /// <summary>See <see cref="ShowSession.PreviewCueAsync"/> (the public doc lives there).</summary>
    public async Task<bool> PreviewCueAsync(string cueId, string? previewDeviceId)
    {
        // --- SETUP (dispatcher): stop any current preview / pending preview open, resolve the binding, claim.
        var setup = await _session.InvokeAsync<(ClipSpec Spec, CancellationTokenSource Cts)?>(async () =>
        {
            await ReleasePreviewAsync().ConfigureAwait(false);
            if (_buildPreviewSpec(cueId) is not { } spec)
                return null;
            var claim = new CancellationTokenSource();
            _previewCts = claim; // published: ReleasePreviewAsync cancels it to preempt the open
            return (spec, claim);
        }).ConfigureAwait(false);
        if (setup is not { } s)
            return false;

        // --- OPEN (OFF the dispatcher): the long part — the loop stays free throughout (NXT-19).
        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(s.Spec, s.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // preempted by StopPreview / a replacing preview / dispose — not an error
        }

        // --- COMMIT (dispatcher): only if our claim is still the current preview.
        try
        {
            return await CommitPreviewAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the open completing and the commit — release the orphaned clip directly.
            await armed.ReleaseAsync().ConfigureAwait(false);
            return false;
        }

        Task<bool> CommitPreviewAsync() => _session.InvokeAsync(async () =>
        {
            if (!ReferenceEquals(_previewCts, s.Cts) || s.Cts.IsCancellationRequested || _session.IsDisposed)
            {
                await armed.ReleaseAsync().ConfigureAwait(false);
                return false;
            }

            var player = armed.Player;
            var outputs = new List<IAudioOutput>();
            try
            {
                if (_audioBackend is not null && player.AudioRouter is not null)
                {
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                    var output = _audioBackend.CreateOutput(previewDeviceId ?? _outputDeviceId, new AudioFormat(rate, 2));
                    player.AttachAudioOutput(output, "_preview");
                    outputs.Add(output);
                }

                armed.Start();
                _previewClip = armed;
                _previewOutputs = outputs;
                StartPreviewEndMonitor(cueId, player, s.Cts.Token);
                return true;
            }
            catch
            {
                foreach (var output in outputs)
                    (output as IDisposable)?.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                throw;
            }
        });
    }

    /// <summary>Stops the current preview, if any — including one still opening (NXT-19).</summary>
    public Task StopPreviewAsync() => _session.InvokeAsync(() => ReleasePreviewAsync().AsTask());

    /// <summary>Releases the preview clip/outputs and preempts a pending preview open. Call on the dispatcher.</summary>
    public async ValueTask ReleasePreviewAsync()
    {
        // Cancel only — never Dispose the CTS here: a preempted preview open (NXT-19) may still hold its token
        // off-dispatcher. A cancelled CTS with no timer holds no unmanaged state, so GC reclaims it.
        _previewCts?.Cancel();
        _previewCts = null;
        var clip = _previewClip;
        var outputs = _previewOutputs;
        _previewClip = null;
        _previewOutputs = [];
        if (clip is not null)
            await clip.ReleaseAsync().ConfigureAwait(false);
        foreach (var output in outputs)
            (output as IDisposable)?.Dispose();
    }

    /// <summary>Watches the preview clip; when it ends on its own (ran, then stopped) it releases it and raises
    /// <see cref="PreviewEnded"/>. Cancelled by <see cref="ReleasePreviewAsync"/> (an explicit stop / replace),
    /// which exits without raising the event. Marshals each check onto the dispatcher.</summary>
    private void StartPreviewEndMonitor(string cueId, S.Media.Players.MediaPlayer player, CancellationToken ct)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            await Task.Delay(ShowSession.EndMonitorPollInterval, ct).ConfigureAwait(false);
                            var ended = await _session.InvokeAsync<bool>(() =>
                                Task.FromResult(
                                    !ReferenceEquals(_previewClip?.Player, player)          // replaced/stopped → exit
                                    || (!player.IsRunning && player.Position > TimeSpan.Zero))) // ran, then ended
                                .ConfigureAwait(false);
                            if (!ended)
                                continue;
                            await _session.InvokeAsync(async () =>
                            {
                                if (ReferenceEquals(_previewClip?.Player, player)) // still ours ⇒ natural end
                                {
                                    await ReleasePreviewAsync().ConfigureAwait(false);
                                    PreviewEnded?.Invoke(cueId);
                                }
                            }).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // best-effort — a preview-monitor hiccup must never crash the session
                    }
                },
                ct);
        }
    }

    // --- soundboard voices ----------------------------------------------------------------------------

    /// <summary>See <see cref="ShowSession.FireVoiceAsync"/> (the public doc lives there).</summary>
    public async Task FireVoiceAsync(string voiceId, string mediaPath, string? deviceId, float volume)
    {
        var outputId = $"voice:{voiceId}";

        // --- SETUP (dispatcher): replace any prior voice / pending open and claim this open.
        var (spec, cts) = await _session.InvokeAsync(async () =>
        {
            await ReleaseVoiceAsync(voiceId).ConfigureAwait(false); // re-trigger replaces the prior voice
            var clipSpec = _buildVoiceSpec(outputId, mediaPath, deviceId);
            var claim = new CancellationTokenSource();
            _pendingVoiceOpens[voiceId] = claim;
            return (clipSpec, claim);
        }).ConfigureAwait(false);

        // --- OPEN (OFF the dispatcher): the long part — the loop stays free throughout (NXT-19).
        IArmedClip armed;
        try
        {
            armed = await _standby.ArmAsync(spec, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException;
            try
            {
                await _session.InvokeAsync(() =>
                {
                    if (_pendingVoiceOpens.TryGetValue(voiceId, out var current) && ReferenceEquals(current, cts))
                        _pendingVoiceOpens.Remove(voiceId);
                    cts.Dispose(); // the open flow owns the claim CTS; the open is over, no one else holds the token
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // disposed mid-open — ReleaseAllAsync already dropped/cancelled the pending claim
            }

            if (cancelled)
                return; // preempted by stop/re-fire/dispose — not an error, the voice just never started
            throw; // a real open failure (bad path/device) surfaces to the caller as before
        }

        // --- COMMIT (dispatcher): only if our claim is still current (not stopped/re-fired during the open).
        try
        {
            await CommitVoiceAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the open completing and the commit — release the orphaned clip directly (the
            // standby engine is internally thread-safe; nothing registered it, so nothing else will).
            await armed.ReleaseAsync().ConfigureAwait(false);
        }

        Task CommitVoiceAsync() => _session.InvokeAsync(async () =>
        {
            var current = _pendingVoiceOpens.TryGetValue(voiceId, out var pending) && ReferenceEquals(pending, cts);
            if (current)
                _pendingVoiceOpens.Remove(voiceId);
            if (!current || cts.IsCancellationRequested || _session.IsDisposed)
            {
                cts.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                return;
            }

            var player = armed.Player;
            var outputs = new List<IAudioOutput>();
            try
            {
                if (_audioBackend is not null && player.AudioRouter is not null)
                {
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48_000;
                    var output = _audioBackend.CreateOutput(deviceId ?? _outputDeviceId, new AudioFormat(rate, 2));
                    player.AttachAudioOutput(output, outputId, gain: volume);
                    outputs.Add(output);
                }

                armed.Start();
                // The claim CTS becomes the running voice's CTS (cancels the end monitor on release).
                _voices[voiceId] = new VoiceHandle(armed, outputs, outputId, cts);
                PublishVoiceViews();
                StartVoiceEndMonitor(voiceId, player, cts.Token);
            }
            catch
            {
                foreach (var output in outputs)
                    (output as IDisposable)?.Dispose();
                await armed.ReleaseAsync().ConfigureAwait(false);
                cts.Dispose();
                throw;
            }
        });
    }

    /// <summary>Stops one soundboard voice (no <see cref="VoiceEnded"/>).</summary>
    public Task StopVoiceAsync(string voiceId) => _session.InvokeAsync(() => ReleaseVoiceAsync(voiceId).AsTask());

    /// <summary>Stops every soundboard voice — including any still opening (NXT-19).</summary>
    public Task StopAllVoicesAsync() => _session.InvokeAsync(() => ReleaseAllVoicesAsync().AsTask());

    /// <summary>Live-sets a voice's output gain (linear). No-op when the voice isn't playing.</summary>
    public Task SetVoiceVolumeAsync(string voiceId, float volume) =>
        _session.InvokeAsync(() =>
        {
            if (_voices.TryGetValue(voiceId, out var v)
                && v.Clip.Player.AudioRouter is { } router
                && v.Clip.Player.AudioSourceId is { } sourceId)
                router.SetRouteGain(sourceId, v.OutputId, volume);
            return Task.CompletedTask;
        });

    /// <summary>Fades a voice's gain to silence over <paramref name="duration"/>, then stops it. No
    /// <see cref="VoiceEnded"/>. A zero/negative duration stops immediately.</summary>
    public Task FadeVoiceAsync(string voiceId, TimeSpan duration) =>
        _session.InvokeAsync(() =>
        {
            if (!_voices.TryGetValue(voiceId, out var v))
                return Task.CompletedTask;
            if (duration <= TimeSpan.Zero)
                return ReleaseVoiceAsync(voiceId).AsTask();
            StartVoiceFadeOut(voiceId, v.Clip.Player, duration, v.Cts.Token);
            return Task.CompletedTask;
        });

    /// <summary>Whether a soundboard voice is currently playing — a lock-free view read (NXT-16 residue),
    /// eventually consistent with the dispatcher state like every session snapshot query.</summary>
    public Task<bool> IsVoicePlayingAsync(string voiceId)
    {
        foreach (var v in _voiceViews)
            if (string.Equals(v.Id, voiceId, StringComparison.Ordinal))
                return Task.FromResult(true);
        return Task.FromResult(false);
    }

    /// <summary>Per-voice playhead (id, position, duration) for every currently-playing soundboard voice — a
    /// lock-free view read (NXT-16 residue): the 200 ms soundboard poll must never queue behind the dispatcher.
    /// Player position/duration reads are thread-safe (the transport snapshot reads them the same way).</summary>
    public Task<IReadOnlyList<VoiceProgress>> GetVoiceProgressAsync()
    {
        var views = _voiceViews; // single volatile read of the published view
        var snaps = new VoiceProgress[views.Length];
        for (var i = 0; i < views.Length; i++)
        {
            TimeSpan pos = TimeSpan.Zero, dur = TimeSpan.Zero;
            try { pos = views[i].Player.Position; dur = views[i].Player.Duration; }
            catch { /* concurrent teardown — zeros for this tick */ }
            snaps[i] = new VoiceProgress(views[i].Id, pos, dur);
        }
        return Task.FromResult<IReadOnlyList<VoiceProgress>>(snaps);
    }

    /// <summary>Releases the preview and every voice (running or still opening) — the session's disposal
    /// teardown. Call on the dispatcher (disposal runs there directly, not through InvokeAsync).</summary>
    public async ValueTask ReleaseAllAsync()
    {
        await ReleasePreviewAsync().ConfigureAwait(false);
        await ReleaseAllVoicesAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseAllVoicesAsync()
    {
        foreach (var id in _voices.Keys.Concat(_pendingVoiceOpens.Keys).Distinct().ToArray())
            await ReleaseVoiceAsync(id).ConfigureAwait(false);
    }

    private async ValueTask ReleaseVoiceAsync(string voiceId)
    {
        // Preempt a still-opening voice (NXT-19): cancel its claim so the off-dispatcher open aborts and its
        // commit is refused. Only Cancel here — the open flow that created the CTS disposes it (it still holds
        // the token inside the blocked open).
        if (_pendingVoiceOpens.Remove(voiceId, out var pending))
            pending.Cancel();

        if (!_voices.Remove(voiceId, out var v))
            return;
        PublishVoiceViews();
        v.Cts.Cancel();
        v.Cts.Dispose();
        await v.Clip.ReleaseAsync().ConfigureAwait(false);
        foreach (var output in v.Outputs)
            (output as IDisposable)?.Dispose();
    }

    /// <summary>Watches a voice; on natural end releases it + raises <see cref="VoiceEnded"/> (the keyed
    /// counterpart of the preview end-monitor).</summary>
    private void StartVoiceEndMonitor(string voiceId, S.Media.Players.MediaPlayer player, CancellationToken ct)
    {
        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            await Task.Delay(ShowSession.EndMonitorPollInterval, ct).ConfigureAwait(false);
                            var ended = await _session.InvokeAsync<bool>(() =>
                                Task.FromResult(
                                    !_voices.TryGetValue(voiceId, out var cur) || !ReferenceEquals(cur.Clip.Player, player)
                                    || (!player.IsRunning && player.Position > TimeSpan.Zero)))
                                .ConfigureAwait(false);
                            if (!ended)
                                continue;
                            await _session.InvokeAsync(async () =>
                            {
                                if (_voices.TryGetValue(voiceId, out var cur) && ReferenceEquals(cur.Clip.Player, player))
                                {
                                    await ReleaseVoiceAsync(voiceId).ConfigureAwait(false);
                                    VoiceEnded?.Invoke(voiceId);
                                }
                            }).ConfigureAwait(false);
                            return;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch { /* best-effort — a voice-monitor hiccup must never crash the session */ }
                },
                ct);
        }
    }

    /// <summary>Ramps a voice's gain to 0 over <paramref name="duration"/> then releases it (fade-out) — a
    /// <see cref="FadeRamp"/> at the same step rate as every other fade.</summary>
    private void StartVoiceFadeOut(string voiceId, S.Media.Players.MediaPlayer player, TimeSpan duration, CancellationToken ct)
    {
        if (player.AudioSourceId is not { } sourceId)
            return;
        var outputId = $"voice:{voiceId}";
        FadeRamp.Start(
            FadeRamp.DefaultStepInterval, ct,
            step: elapsed => _session.InvokeAsync<bool>(() =>
            {
                if (ct.IsCancellationRequested
                    || !_voices.TryGetValue(voiceId, out var cur) || !ReferenceEquals(cur.Clip.Player, player)
                    || player.AudioRouter is not { } router)
                    return Task.FromResult(true);
                var level = FadeRamp.LevelDown(elapsed, duration);
                router.SetRouteGain(sourceId, outputId, level);
                return Task.FromResult(level <= 0f);
            }),
            onCompleted: () => _session.InvokeAsync(() => ReleaseVoiceAsync(voiceId).AsTask()));
    }
}
