namespace S.Media.Core.Video;

/// <summary>
/// Optional capability for an <see cref="IVideoOutput"/> whose <see cref="IVideoOutput.Submit"/> can
/// block for a noticeable time (network encoders, paced senders such as NDI). A
/// <see cref="VideoOutputPump"/> calls <see cref="RequestSubmitAbort"/> from
/// <see cref="VideoOutputPump.Dispose"/> just before joining its drain thread, so a cooperative output
/// can abandon an in-flight (and any subsequent) <see cref="IVideoOutput.Submit"/> promptly instead of
/// forcing the pump to wait out its join cap and then leak pump state to avoid a use-after-dispose.
/// </summary>
/// <remarks>
/// Implementations must be idempotent and must not throw. The signal is one-way and terminal: it is only
/// raised on the teardown path, so an output may treat itself as unusable afterwards (drop the in-flight
/// frame, fail fast on later submits). Outputs that do not implement this keep the pump's prior
/// leak-avoidance fallback (the drainer exits on its own once the slow Submit finally returns).
/// </remarks>
public interface IVideoOutputCooperativeAbort
{
    /// <summary>
    /// Ask the output to abandon any in-flight and subsequent <see cref="IVideoOutput.Submit"/> as soon
    /// as practical. Called once, on teardown, before the pump joins its drain thread. Idempotent; must not throw.
    /// </summary>
    void RequestSubmitAbort();
}
