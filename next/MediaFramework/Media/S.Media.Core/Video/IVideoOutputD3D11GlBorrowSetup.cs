namespace S.Media.Core.Video;

/// <summary>
/// Optional <see cref="IVideoOutput"/> capability: accept the active <see cref="IVideoSource"/> before
/// <see cref="IVideoOutput.Configure"/> so a Win32 NV12 GL path can borrow libav's <c>ID3D11Device</c> when available.
/// </summary>
public interface IVideoOutputD3D11GlBorrowSetup
{
    /// <summary>
    /// Called by <see cref="VideoFormatNegotiator.Connect"/> after <see cref="IVideoSource.SelectOutputFormat"/>
    /// and before <see cref="IVideoOutput.Configure"/>. Pass <see langword="null"/> to clear any prior borrow.
    /// </summary>
    void SetBorrowVideoSourceForWin32Nv12Gl(IVideoSource? videoSource);
}
