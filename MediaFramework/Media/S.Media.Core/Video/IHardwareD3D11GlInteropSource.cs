namespace S.Media.Core.Video;

/// <summary>
/// Optional capability of an <see cref="IVideoSource"/> that exposes libav's Windows D3D11VA
/// <c>ID3D11Device</c> when hardware NV12 shared-handle decode is active (same adapter as decoded textures).
/// </summary>
/// <remarks>
/// Implemented by FFmpeg decoders when D3D11 shared-handle export is enabled. GL sinks such as
/// <c>SDL3GLVideoSink</c> can call <see cref="TryGetHardwareD3D11DeviceForWin32Gl"/> during setup so
/// <c>OpenSharedResource</c> uses the same device as the decoder without a second host-created D3D11 device.
/// <see cref="TryGetHardwareD3D11AdapterLuid"/> is optional metadata (DXGI adapter LUID) for diagnostics and multi-GPU mismatch warnings.
/// </remarks>
public interface IHardwareD3D11GlInteropSource
{
    /// <summary>
    /// When Windows D3D11VA NV12 shared-handle decode is active, returns libav's device COM pointer; otherwise <c>0</c>.
    /// </summary>
    bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr);

    /// <summary>
    /// When <see cref="TryGetHardwareD3D11DeviceForWin32Gl"/> would return a device, tries to read the DXGI adapter LUID
    /// (packed into a <see langword="long"/>) for diagnostics / adapter-matching experiments; otherwise <c>0</c> and <see langword="false"/>.
    /// </summary>
    bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked);
}
