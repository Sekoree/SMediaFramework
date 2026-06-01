using S.Media.Core.Video;
using S.Media.FFmpeg.Video;

namespace S.Media.Effects;

/// <summary>Stable handle for one layer in a <see cref="VideoCompositor"/>.</summary>
public sealed class LayerHandle
{
    private readonly VideoCompositor _owner;
    private readonly object _gate = new();
    private readonly List<ScheduledTransition> _transitions = [];
    private LayerConfig _config;
    private int _lastWidth;
    private int _lastHeight;
    private VideoCpuFrameConverter? _toBgra;

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
        _lastWidth = source.Format.Width;
        _lastHeight = source.Format.Height;
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

    internal bool TryPullFrame(VideoFormat canvasFormat, out VideoFrame? frame, out bool converted)
    {
        frame = null;
        converted = false;
        if (!Source.TryReadNextFrame(out var src))
            return false;

        // Snapshot layer state under the lock so a concurrent SetConfig / AddTransition /
        // ClearTransitions (UI/control thread) can't tear the struct read or mutate the transition
        // list while the compositor path iterates it.
        LayerConfig config;
        ScheduledTransition[] transitions;
        lock (_gate)
        {
            config = _config;
            transitions = _transitions.Count == 0 ? [] : _transitions.ToArray();
        }

        var timeline = _owner.TimelinePosition;
        var resolved = LayerConfigResolver.ResolveAt(config, transitions, timeline);
        Slot.Opacity = resolved.Opacity;
        Slot.BlendMode = resolved.Blend;

        if (src.Format.Width != _lastWidth || src.Format.Height != _lastHeight)
        {
            _lastWidth = src.Format.Width;
            _lastHeight = src.Format.Height;
        }

        Slot.Transform = LayerConfigResolver.ResolveTransform(config, transitions, timeline, src.Format, canvasFormat);

        VideoFrame pending;
        if (src.Format.PixelFormat != PixelFormat.Bgra32)
        {
            if (!CompositorBgraHelper.TryToBgra(src, ref _toBgra, out var bgra))
            {
                src.Dispose();
                return false;
            }

            converted = true;
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
            return false;
        }

        return true;
    }
}
