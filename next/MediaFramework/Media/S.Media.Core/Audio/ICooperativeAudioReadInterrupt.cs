namespace S.Media.Core.Audio;

/// <summary>
/// Optional hook for <see cref="AudioRouter"/> shutdown: a long-running <see cref="IAudioSource.ReadInto"/>
/// implementation should observe <see cref="RequestYieldBetweenReads"/> and return promptly, usually with
/// zero samples, so the router thread can honor <see cref="AudioRouter.Pause"/> / <see cref="AudioRouter.Dispose"/>.
/// </summary>
public interface ICooperativeAudioReadInterrupt
{
    /// <summary>Ask in-flight reads to return quickly.</summary>
    void RequestYieldBetweenReads();

    /// <summary>Clear the yield request before playback resumes.</summary>
    void ClearYieldRequest();
}
