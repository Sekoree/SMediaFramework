using S.Media.Core.Diagnostics;

namespace S.Media.Core.Audio;

/// <summary>
/// Observes <see cref="AudioRouter.PumpPressure"/> (output queue drops) and maintains a bounded
/// playback-rate hint in parts-per-million. A negative value suggests slowing the master clock
/// slightly so the router produces fewer chunks ahead of a slow output.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally minimal — no automatic clock wiring. Hosts subscribe to
/// <see cref="HintPpmBiasChanged"/> or poll <see cref="HintPpmBias"/> and apply bias to
/// <see cref="Clock.IPlaybackClock"/> (or logging / metrics) as they see fit.
/// Use the overload with a output id when several pumps are active so hints are not
/// conflated across outputs.
/// </para>
/// <para>
/// Coordinated <strong>master</strong> clock ppm and synchronized <strong>drop/repeat</strong> policies across multiple outputs are out of scope here —
/// this type only derives a scalar hint from queue pressure. Per-output rate nudging without retuning the whole graph lives in FFmpeg <c>AdaptiveRateAudioOutput</c> instead.
/// </para>
/// <para>
/// <see cref="Dispose"/> unsubscribes from <see cref="AudioRouter.PumpPressure"/>; <strong>Debug</strong> builds log a failed unsubscribe via <see cref="MediaDiagnostics.LogError"/>.
/// </para>
/// </remarks>
public sealed class PumpPressurePlaybackHintMonitor : IDisposable
{
    private readonly AudioRouter _router;
    private readonly string? _observeSinkId;
    private readonly object _gate = new();
    private readonly double _maxAbsPpm;
    private readonly double _ppmPerDropPerSecond;

    private long _lastTotal;
    private DateTimeOffset _lastAt;
    private double _hintPpm;
    private bool _disposed;

    /// <param name="router">Router whose pump pressure is observed.</param>
    /// <param name="maxAbsPpm">Absolute clamp for <see cref="HintPpmBias"/>.</param>
    /// <param name="ppmPerDropPerSecond">Gain from drop rate (drops per second) to ppm bias.</param>
    public PumpPressurePlaybackHintMonitor(
        AudioRouter router,
        double maxAbsPpm = 40,
        double ppmPerDropPerSecond = 4)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _observeSinkId = null;
        _maxAbsPpm = maxAbsPpm;
        _ppmPerDropPerSecond = ppmPerDropPerSecond;
        _lastAt = DateTimeOffset.UtcNow;
        _router.PumpPressure += OnPumpPressure;
    }

    /// <summary>
    /// Observes pump pressure for a single output id (per-output drops). Other outputs' drops are ignored.
    /// </summary>
    /// <param name="observeSinkId">Non-empty output id to match against <see cref="AudioRouterPumpPressureEventArgs.SinkId"/>.</param>
    public PumpPressurePlaybackHintMonitor(
        AudioRouter router,
        string observeSinkId,
        double maxAbsPpm = 40,
        double ppmPerDropPerSecond = 4)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentException.ThrowIfNullOrWhiteSpace(observeSinkId);
        _router = router;
        _observeSinkId = observeSinkId;
        _maxAbsPpm = maxAbsPpm;
        _ppmPerDropPerSecond = ppmPerDropPerSecond;
        _lastAt = DateTimeOffset.UtcNow;
        _router.PumpPressure += OnPumpPressure;
    }

    /// <summary>Latest suggested bias in ppm (negative ⇒ slow clock).</summary>
    public double HintPpmBias
    {
        get
        {
            lock (_gate) return _hintPpm;
        }
    }

    /// <summary>Raised when <see cref="HintPpmBias"/> changes meaningfully.</summary>
    public event EventHandler<double>? HintPpmBiasChanged;

    /// <summary>
    /// Feed an observation without subscribing to <see cref="AudioRouter"/> (tests and custom telemetry).
    /// </summary>
    public void ApplyObservation(long cumulativeDroppedChunks, DateTimeOffset now)
    {
        double? toFire = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var dt = (now - _lastAt).TotalSeconds;
            if (dt <= 0)
            {
                _lastTotal = cumulativeDroppedChunks;
                _lastAt = now;
                return;
            }

            if (dt < 0.05)
            {
                _lastTotal = cumulativeDroppedChunks;
                _lastAt = now;
                return;
            }

            var delta = cumulativeDroppedChunks - _lastTotal;
            _lastTotal = cumulativeDroppedChunks;
            _lastAt = now;

            if (delta <= 0)
                return;

            var dropsPerSec = delta / dt;
            var raw = -_ppmPerDropPerSecond * dropsPerSec;
            var next = Math.Clamp(raw, -_maxAbsPpm, _maxAbsPpm);
            if (Math.Abs(next - _hintPpm) < 0.05)
                return;
            _hintPpm = next;
            toFire = next;
        }

        if (toFire is { } v)
            HintPpmBiasChanged?.Invoke(this, v);
    }

    private void OnPumpPressure(object? sender, AudioRouterPumpPressureEventArgs e)
    {
        if (_observeSinkId is not null && e.SinkId != _observeSinkId)
            return;
        ApplyObservation(e.DroppedTotal, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MediaDiagnostics.SwallowDisposeErrors(() => _router.PumpPressure -= OnPumpPressure, "PumpPressurePlaybackHintMonitor.Dispose: PumpPressure unsubscribe");
    }
}
