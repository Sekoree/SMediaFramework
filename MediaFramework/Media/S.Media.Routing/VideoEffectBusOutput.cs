using S.Media.Core.Buses;

namespace S.Media.Routing;

/// <summary>
/// Inserts an <see cref="IVideoBusEffect"/> chain in front of any <see cref="IVideoOutput"/>. Wrap the
/// REAL output with this and register the wrapper on the router; when the router pumps the branch
/// (the default), <see cref="Submit"/> - and therefore the chain - runs on the pump's drain thread,
/// never the clock path. Capability mixins forward to the inner output (same pattern as
/// <see cref="VideoOutputPump"/>) so Pause/Stop/idle semantics are unchanged. The wrapper owns its
/// effects; inner-output ownership follows the usual router registration flags.
/// </summary>
public sealed class VideoEffectBusOutput :
    IVideoOutput, IVideoOutputQueueControl, IVideoOutputCooperativeAbort, IVideoOutputD3D11GlBorrowSetup, IDisposable
{
    private readonly IVideoOutput _inner;
    private readonly bool _disposeInner;
    private IVideoBusEffect[] _effects;
    private VideoFormat _configuredFormat;
    private bool _configured;
    private bool _disposed;

    public VideoEffectBusOutput(IVideoOutput inner, IReadOnlyList<IVideoBusEffect> effects, bool disposeInnerOnDispose = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(effects);
        _effects = effects.ToArray();
        _disposeInner = disposeInnerOnDispose;
    }

    public IVideoOutput InnerOutput => _inner;

    /// <summary>The current chain snapshot (for UI listing).</summary>
    public IReadOnlyList<IVideoBusEffect> Effects => Volatile.Read(ref _effects);

    /// <summary>Replaces the chain atomically (new effects are configured first when the output already
    /// runs). Removed effects are disposed after the swap - a Process racing the swap finishes on the
    /// old array for at most one frame.</summary>
    public void SetEffects(IReadOnlyList<IVideoBusEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = effects.ToArray();
        if (_configured)
        {
            foreach (var effect in next)
                effect.Configure(_configuredFormat);
        }

        var previous = Interlocked.Exchange(ref _effects, next);
        foreach (var old in previous)
        {
            if (!next.Contains(old))
                MediaDiagnostics.SwallowDisposeErrors(old.Dispose, "VideoEffectBusOutput.SetEffects: removed effect");
        }
    }

    public VideoFormat Format => _inner.Format;

    public IReadOnlyList<PixelFormat> AcceptedPixelFormats => _inner.AcceptedPixelFormats;

    public void Configure(VideoFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inner.Configure(format);
        _configuredFormat = format;
        _configured = true;
        foreach (var effect in Volatile.Read(ref _effects))
            effect.Configure(format);
    }

    public void Submit(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed)
        {
            frame.Dispose();
            return;
        }

        var effects = Volatile.Read(ref _effects);
        var working = frame;
        foreach (var effect in effects)
        {
            try
            {
                working = effect.Process(working, working.PresentationTime);
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning($"VideoEffectBusOutput: effect {effect.GetType().Name} failed - frame passed through unprocessed ({ex.Message})");
            }
        }

        _inner.Submit(working);
    }

    // --- capability forwarding (mirrors VideoOutputPump) ---------------------

    public void AbandonQueuedFrames() => (_inner as IVideoOutputQueueControl)?.AbandonQueuedFrames();

    public bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        (_inner as IVideoOutputQueueControl)?.WaitForIdle(timeout, cancellationToken) ?? true;

    public void RequestSubmitAbort() => (_inner as IVideoOutputCooperativeAbort)?.RequestSubmitAbort();

    public void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource) =>
        (_inner as IVideoOutputD3D11GlBorrowSetup)?.SetBorrowVideoSourceForWin32Nv12Gl(videoSource);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var effects = Interlocked.Exchange(ref _effects, []);
        foreach (var effect in effects)
            MediaDiagnostics.SwallowDisposeErrors(effect.Dispose, "VideoEffectBusOutput.Dispose: effect");
        if (_disposeInner && _inner is IDisposable d)
            MediaDiagnostics.SwallowDisposeErrors(d.Dispose, "VideoEffectBusOutput.Dispose: inner output");
    }
}
