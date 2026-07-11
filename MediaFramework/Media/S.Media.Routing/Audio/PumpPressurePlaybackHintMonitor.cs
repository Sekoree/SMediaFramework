
namespace S.Media.Routing;

/// <summary>
/// Observes <see cref="AudioRouter.PumpPressure"/> (output queue drops) and maintains a bounded
/// playback-rate hint in parts-per-million. A negative value suggests slowing the master clock
/// slightly so the router produces fewer chunks ahead of a slow output.
/// </summary>
/// <remarks>
/// <para>
/// This is intentionally minimal - no automatic clock wiring. Hosts subscribe to
/// <see cref="HintPpmBiasChanged"/> or poll <see cref="HintPpmBias"/> and apply bias to
/// <see cref="Clock.IPlaybackClock"/> (or logging / metrics) as they see fit.
/// Use the overload with a output id when several pumps are active so hints are not
/// conflated across outputs.
/// </para>
/// <para>
/// <strong>Decay</strong>: while drops keep arriving the hint holds steady, but once the output goes
/// quiet for <see cref="DecayGracePeriod"/> the <em>read</em> value eases exponentially back toward 0
/// (half-life <see cref="DecayHalfLife"/>). Without this, one transient stall latched a bias on the
/// wrapped output for the rest of the session. Decay is applied on read (pollers like the FFmpeg
/// <c>AdaptiveRateAudioOutput</c> sample per chunk); <see cref="HintPpmBiasChanged"/> still fires only
/// on observations.
/// </para>
/// <para>
/// Coordinated <strong>master</strong> clock ppm and synchronized <strong>drop/repeat</strong> policies across multiple outputs are out of scope here -
/// this type only derives a scalar hint from queue pressure. Per-output rate nudging without retuning the whole graph lives in FFmpeg <c>AdaptiveRateAudioOutput</c> instead.
/// </para>
/// <para>
/// <see cref="Dispose"/> unsubscribes from <see cref="AudioRouter.PumpPressure"/>; <strong>Debug</strong> builds log a failed unsubscribe via <see cref="MediaDiagnostics.LogError"/>.
/// </para>
/// </remarks>
public sealed class PumpPressurePlaybackHintMonitor : IDisposable
{
    private readonly AudioRouter _router;
    private readonly string? _observeOutputId;
    private readonly object _gate = new();
    private readonly double _maxAbsPpm;
    private readonly double _ppmPerDropPerSecond;

    /// <summary>No decay while the last drop is at most this recent - sustained pressure holds the bias steady.</summary>
    public static readonly TimeSpan DecayGracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>Half-life of the exponential ease back toward 0 once the grace period has passed.</summary>
    public static readonly TimeSpan DecayHalfLife = TimeSpan.FromSeconds(5);

    private long _lastTotal;
    private DateTimeOffset _lastAt;
    /// <summary>When the most recent observation with new drops arrived - the decay anchor.</summary>
    private DateTimeOffset _lastDropAt;
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
        _observeOutputId = null;
        _maxAbsPpm = maxAbsPpm;
        _ppmPerDropPerSecond = ppmPerDropPerSecond;
        _lastAt = DateTimeOffset.UtcNow;
        _lastDropAt = _lastAt;
        _router.PumpPressure += OnPumpPressure;
    }

    /// <summary>
    /// Observes pump pressure for a single output id (per-output drops). Other outputs' drops are ignored.
    /// </summary>
    /// <param name="observeOutputId">Non-empty output id to match against <see cref="AudioRouterPumpPressureEventArgs.OutputId"/>.</param>
    public PumpPressurePlaybackHintMonitor(
        AudioRouter router,
        string observeOutputId,
        double maxAbsPpm = 40,
        double ppmPerDropPerSecond = 4)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentException.ThrowIfNullOrWhiteSpace(observeOutputId);
        _router = router;
        _observeOutputId = observeOutputId;
        _maxAbsPpm = maxAbsPpm;
        _ppmPerDropPerSecond = ppmPerDropPerSecond;
        _lastAt = DateTimeOffset.UtcNow;
        _lastDropAt = _lastAt;
        _router.PumpPressure += OnPumpPressure;
    }

    /// <summary>Latest suggested bias in ppm (negative ⇒ slow clock), decayed toward 0 when the
    /// output has been quiet (see the class remarks).</summary>
    public double HintPpmBias => GetHintPpmBias(DateTimeOffset.UtcNow);

    /// <summary>
    /// <see cref="HintPpmBias"/> evaluated at an explicit <paramref name="now"/> - use the same time base
    /// as the <see cref="ApplyObservation"/> calls (tests / custom telemetry drive synthetic clocks).
    /// </summary>
    public double GetHintPpmBias(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_hintPpm == 0)
                return 0;
            var quietSeconds = (now - _lastDropAt - DecayGracePeriod).TotalSeconds;
            if (quietSeconds <= 0)
                return _hintPpm;
            return _hintPpm * Math.Exp(-quietSeconds * Math.Log(2) / DecayHalfLife.TotalSeconds);
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

            // Fresh drops re-anchor the decay even when the computed hint value is unchanged, so a
            // steadily dropping output holds its full bias while a quiet one eases back toward 0.
            _lastDropAt = now;

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
        if (_observeOutputId is not null && e.OutputId != _observeOutputId)
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
