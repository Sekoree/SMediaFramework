using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace S.Media.Effects;

/// <summary>Stable handle for one layer in a <see cref="VideoCompositor"/>.</summary>
public sealed class LayerHandle
{
    // Bounded look-ahead of decoded-but-not-yet-displayed layer frames, ascending PTS. The currently
    // displayed frame lives in the slot (handed off), not here. Catch-up pulls are capped per advance so
    // a large timeline jump can't pull an unbounded number of frames in a single composite.
    private const int MaxQueuedFrames = 8;
    private const int MaxPullsPerAdvance = 8;

    private readonly VideoCompositor _owner;
    private readonly object _gate = new();
    private readonly List<ScheduledTransition> _transitions = [];
    private readonly List<VideoFrame> _lookahead = [];
    private LayerConfig _config;
    private VideoCpuFrameConverter? _toBgra;
    private VideoFormat _displayedSrcFormat;
    private TimeSpan _newestQueuedPts = TimeSpan.MinValue;
    private TimeSpan _displayedPts = TimeSpan.MinValue;
    private bool _hasDisplayed;

    internal LayerHandle(
        VideoCompositor owner,
        IVideoSource source,
        VideoCompositorSource.Slot slot,
        LayerConfig initialConfig)
    {
        _owner = owner;
        Source = source;
        Slot = slot;
        _config = initialConfig;
    }

    public IVideoSource Source { get; }

    internal VideoCompositorSource.Slot Slot { get; }

    public LayerConfig CurrentConfig { get { lock (_gate) return _config; } }

    public void SetConfig(LayerConfig config) { lock (_gate) _config = config; }

    public void AddTransition(TimeSpan at, Transition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        lock (_gate)
        {
            _transitions.Add(new ScheduledTransition(at, transition));
            _transitions.Sort(static (a, b) => a.At.CompareTo(b.At));
        }
    }

    public void ClearTransitions() { lock (_gate) _transitions.Clear(); }

    /// <summary>
    /// Advances this layer to <paramref name="masterTime"/>: catches the layer's decode timeline up to
    /// the master clock (bounded), selects the frame whose interval contains <paramref name="masterTime"/>
    /// (the last frame with PTS ≤ master time), and submits it to the slot — but only when that frame
    /// changed since the previous advance, so a downstream consumer reading faster than the clock
    /// advances neither re-decodes the layer nor re-submits. Per-layer opacity / transform / blend are
    /// re-resolved every call so animated transitions stay smooth even while the frame is held.
    /// </summary>
    internal void AdvanceTo(TimeSpan masterTime, VideoFormat canvasFormat)
    {
        // 1. Catch up: pull future frames until the newest queued frame reaches/passes the master time
        //    (so the look-ahead brackets it), bounded by buffer size and a per-advance pull cap.
        var pulls = 0;
        while (_lookahead.Count < MaxQueuedFrames
               && pulls < MaxPullsPerAdvance
               && (_lookahead.Count == 0 || _newestQueuedPts < masterTime))
        {
            if (!Source.TryReadNextFrame(out var src))
                break;
            _lookahead.Add(src);
            _newestQueuedPts = src.PresentationTime;
            pulls++;
        }

        // 2a. Drop anything at or before what we've already shown (duplicate / non-monotonic source).
        while (_hasDisplayed && _lookahead.Count > 0 && _lookahead[0].PresentationTime <= _displayedPts)
        {
            _lookahead[0].Dispose();
            _lookahead.RemoveAt(0);
        }

        // 2b. Drop frames superseded by a newer frame that is also ≤ master time — they'll never be the
        //     cover (the newer one is closer to / contains master time).
        while (_lookahead.Count >= 2 && _lookahead[1].PresentationTime <= masterTime)
        {
            _lookahead[0].Dispose();
            _lookahead.RemoveAt(0);
        }

        // 3. The front is now the cover (last frame ≤ master time), or — before the master time has
        //    reached any frame — the earliest future frame stands in as start-up pre-roll. Submit it
        //    once; subsequent advances within its interval just re-resolve the slot params.
        if (_lookahead.Count > 0
            && (_lookahead[0].PresentationTime <= masterTime || !_hasDisplayed))
        {
            var cover = _lookahead[0];
            _lookahead.RemoveAt(0);
            _displayedSrcFormat = cover.Format;
            _displayedPts = cover.PresentationTime;
            _hasDisplayed = true;
            SubmitFrameToSlot(cover, masterTime, canvasFormat);
            return;
        }

        // Frame unchanged (held in the slot) — refresh time-driven slot params only.
        UpdateSlotParams(masterTime, canvasFormat);
    }

    /// <summary>
    /// Read-paced advance (no master clock): pulls exactly one frame from the source and submits it,
    /// resolving per-layer params at <paramref name="timelineTime"/>. Preserves the 1-frame-per-read
    /// passthrough a single-layer scaler relies on (no look-ahead / PTS-grid selection).
    /// </summary>
    internal void PullOneAndSubmit(TimeSpan timelineTime, VideoFormat canvasFormat)
    {
        if (!Source.TryReadNextFrame(out var src))
            return;
        SubmitFrameToSlot(src, timelineTime, canvasFormat);
    }

    private (LayerConfig Config, ScheduledTransition[] Transitions) SnapshotState()
    {
        lock (_gate)
            return (_config, _transitions.Count == 0 ? [] : _transitions.ToArray());
    }

    private void SubmitFrameToSlot(VideoFrame src, TimeSpan masterTime, VideoFormat canvasFormat)
    {
        var (config, transitions) = SnapshotState();
        var resolved = LayerConfigResolver.ResolveAt(config, transitions, masterTime);
        Slot.Opacity = resolved.Opacity;
        Slot.BlendMode = resolved.Blend;
        Slot.Transform = LayerConfigResolver.ResolveTransform(config, transitions, masterTime, src.Format, canvasFormat);

        VideoFrame pending;
        if (src.Format.PixelFormat != PixelFormat.Bgra32)
        {
            if (!CompositorBgraHelper.TryToBgra(src, ref _toBgra, out var bgra))
            {
                src.Dispose();
                return;
            }

            src.Dispose();
            pending = bgra;
        }
        else
        {
            pending = src;
        }

        // Configure/Submit hand the frame to the slot. If Configure throws (e.g. the slot rejects the
        // format) ownership has NOT moved, so dispose to avoid leaking the converted/owned frame; a
        // failing Submit likewise leaves us owning it (per the IVideoOutput contract).
        try
        {
            Slot.Output.Configure(pending.Format);
            Slot.Output.Submit(pending);
        }
        catch
        {
            pending.Dispose();
        }
    }

    private void UpdateSlotParams(TimeSpan masterTime, VideoFormat canvasFormat)
    {
        if (!_hasDisplayed)
            return;

        var (config, transitions) = SnapshotState();
        var resolved = LayerConfigResolver.ResolveAt(config, transitions, masterTime);
        Slot.Opacity = resolved.Opacity;
        Slot.BlendMode = resolved.Blend;
        // Re-resolve transform against the displayed frame's source size so animated scale/position keep
        // moving while the underlying frame is held.
        Slot.Transform = LayerConfigResolver.ResolveTransform(config, transitions, masterTime, _displayedSrcFormat, canvasFormat);
    }

    /// <summary>Disposes the look-ahead buffer and the layer's converter. Called when the layer is
    /// removed or the compositor is disposed.</summary>
    internal void Close()
    {
        foreach (var f in _lookahead)
            f.Dispose();
        _lookahead.Clear();
        _toBgra?.Dispose();
        _toBgra = null;
    }
}
