namespace S.Media.Core.Clock;

/// <summary>
/// Read-only time source that <see cref="MediaClock"/> can slave to via
/// <c>MediaClock.SetMaster</c>. Typically implemented by the audio output that
/// owns the playback hardware (PortAudio output, CoreAudio, …): the output
/// reports how much audio it has actually played, and the clock derives its
/// position from that instead of a wall-clock <see cref="System.Diagnostics.Stopwatch"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ElapsedSinceStart"/> is monotonic — it never goes backwards. It
/// represents real playback progress (samples consumed by the device divided
/// by sample rate, etc.). Pausing the underlying source should freeze it;
/// stopping should also freeze it.
/// </para>
/// <para>
/// Implementations should be safe to read concurrently from <see cref="MediaClock"/>'s
/// driver thread. <see cref="ElapsedSinceStart"/> is read frequently — keep it
/// cheap (a couple of <see cref="System.Threading.Interlocked"/> reads + a
/// division is fine).
/// </para>
/// </remarks>
public interface IPlaybackClock
{
    /// <summary>Monotonic playback duration since the underlying source started.</summary>
    TimeSpan ElapsedSinceStart { get; }

    /// <summary>True when the source is actively advancing (playing, not paused/stopped/disposed).</summary>
    bool IsAdvancing { get; }
}
