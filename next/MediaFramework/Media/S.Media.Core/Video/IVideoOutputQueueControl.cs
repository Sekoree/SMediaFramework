namespace S.Media.Core.Video;

/// <summary>
/// Optional control surface for asynchronous <see cref="IVideoOutput"/> implementations that buffer
/// submitted frames before they are actually consumed by the device or worker thread.
/// </summary>
public interface IVideoOutputQueueControl
{
    /// <summary>Drops frames queued inside the output that have not started presentation yet.</summary>
    void AbandonQueuedFrames();

    /// <summary>
    /// Waits until the output has no queued frame and no presentation currently in flight.
    /// Returns <c>false</c> on timeout.
    /// </summary>
    bool WaitForIdle(TimeSpan timeout, CancellationToken cancellationToken = default);
}
