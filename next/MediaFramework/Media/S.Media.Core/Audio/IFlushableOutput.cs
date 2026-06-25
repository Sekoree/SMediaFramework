namespace S.Media.Core.Audio;

/// <summary>
/// Optional <see cref="IAudioOutput"/> capability: drop any buffered audio
/// downstream of the router for immediate silence.
/// </summary>
/// <remarks>
/// Called by <see cref="AudioRouter.Pause"/> after the router stops feeding
/// the pumps, so that local hardware buffers (PortAudio's ring, OS playback
/// queue, …) don't continue to play "stale" audio between Pause and Resume.
/// Outputs that don't buffer (NDI sender's synchronous Submit) can leave this
/// unimplemented.
/// </remarks>
public interface IFlushableOutput
{
    /// <summary>
    /// Discard any audio currently queued downstream. May briefly interrupt
    /// the device (e.g. PortAudio aborts and restarts its stream); subsequent
    /// <see cref="IAudioOutput.Submit"/> calls play normally.
    /// </summary>
    void Flush();
}
