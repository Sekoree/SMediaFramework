namespace S.Media.Core.Video;

/// <summary>
/// Optional hook for <see cref="VideoPlayer"/> shutdown: a long-running <see cref="IVideoSource.TryReadNextFrame"/>
/// implementation should observe <see cref="RequestYieldBetweenReads"/> and return <c>false</c> promptly (without
/// producing a frame) so the decode thread can honor <see cref="VideoPlayer.Pause"/> / <see cref="VideoPlayer.Dispose"/>.
/// </summary>
public interface ICooperativeVideoReadInterrupt
{
    /// <summary>Ask in-flight reads to return quickly with no frame.</summary>
    void RequestYieldBetweenReads();

    /// <summary>Clear the yield request after <see cref="VideoPlayer.Play"/>.</summary>
    void ClearYieldRequest();
}
