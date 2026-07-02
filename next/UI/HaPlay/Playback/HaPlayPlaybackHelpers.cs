using HaPlay.Models;
using S.Media.Core.Video;
using S.Media.Players;

namespace HaPlay.Playback;

/// <summary>
/// Stateless media-open / output helpers shared across the playback paths, extracted from
/// <see cref="HaPlayPlaybackSession"/> so the cue workspace, playlist cache, output-setup dialog, and cue
/// composition runtime do not depend on the (legacy, deletion-bound) deck engine type. Pure functions only —
/// no session/engine state. (NXT-13 engine-deletion prep, stage 1.)
/// </summary>
internal static class HaPlayPlaybackHelpers
{
    /// <summary>Builds the provider-owned <c>ndi:</c> descriptor URI for a live NDI input item, carrying its
    /// per-item stream selection, bandwidth mode, and audio jitter-buffer override — the ONE builder both the
    /// deck and the cue mapper must use, so a persisted item keeps its options on either playback path (the
    /// registry's <c>NDIDecoderProvider.ParseSourceUri</c> is the counterpart).</summary>
    internal static string BuildNdiInputUri(NDIInputPlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var query = new List<string>
        {
            $"audio={(item.VideoOnly ? 0 : 1)}",
            $"video={(item.AudioOnly ? 0 : 1)}",
            $"lowBandwidth={(item.LowBandwidth ? 1 : 0)}",
        };
        if (item.AudioMinBufferedDurationMs is { } bufferMs)
            query.Add($"audioBufferMs={bufferMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return $"ndi://{Uri.EscapeDataString(item.SourceName)}?{string.Join('&', query)}";
    }

    /// <summary>Builds the provider-owned <c>padev:</c> descriptor URI for a PortAudio capture item, carrying
    /// host API, saved global-index fallback, channels, sample rate, and latency (parsed by
    /// <c>PortAudioCaptureDecoderProvider.ParseDescriptor</c>). Shared by the deck and the cue mapper.</summary>
    internal static string BuildPortAudioInputUri(PortAudioInputPlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var query = new List<string>();
        static void Add(List<string> target, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                target.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        Add(query, "hostApiName", item.HostApiName);
        if (item.HostApiIndex is { } hostApi) query.Add($"hostApiIndex={hostApi}");
        if (item.GlobalDeviceIndex is { } global) query.Add($"globalDeviceIndex={global}");
        query.Add($"channels={item.Channels}");
        query.Add($"sampleRate={item.SampleRate}");
        if (item.SuggestedLatency is { } latency)
            query.Add($"latency={latency.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        return $"padev://{Uri.EscapeDataString(item.DeviceName)}?{string.Join('&', query)}";
    }

    /// <summary>
    /// The default file open options for deck / cue / playlist-cache file playback: hardware-decode + Windows
    /// D3D11 shared-handle GL retention gated on <see cref="HardwareVideoDecodeGate"/> and the absence of NDI
    /// (which needs CPU-readable pixels), plus the tuned packet-queue depths and read buffer.
    /// </summary>
    /// <remarks>
    /// <paramref name="anyNDI"/> forces software decode + CPU-readable frames when any NDI output is in play.
    /// The demux read buffer is generous so it keeps up on high-per-IOP-latency media.
    /// </remarks>
    internal static MediaPlayerOpenOptions BuildFileOpenOptions(bool anyNDI) =>
        new(
            // Software decode once the hardware path has faulted this process (see HardwareVideoDecodeGate).
            TryHardwareAcceleration: !anyNDI && HardwareVideoDecodeGate.HardwareDecodeEnabled,
            // Windows D3D11VA only: keep decoded frames on their D3D11 NV12 surfaces and let the GL output do the
            // GPU->CPU staging upload on its OWN render thread, instead of av_hwframe_transfer_data on the decode
            // thread. That parallelizes decode and upload and is what holds file video at a stable 60 fps — the
            // decode-thread transfer otherwise jitters frame production so the player presents ~1/6 of frames late
            // (measured: retain ≈ 60 fps / 0 late vs transfer ≈ 50 fps / climbing late). Gated exactly like hardware
            // decode: NDI needs CPU-readable pixels (a D3D11 surface has none), and a faulted hw path forces software.
            // The decoder sizes its surface pool for the retained pipeline via extra_hw_frames (no pool exhaustion).
            RetainD3D11SharedHandleForGl:
                OperatingSystem.IsWindows() && !anyNDI && HardwareVideoDecodeGate.HardwareDecodeEnabled,
            // Keep rendering on shared NT handles only. Borrowing libav-owned D3D11 COM objects couples GL upload
            // lifetime to decoder teardown, which is fragile during playlist switches on Windows.
            Win32Nv12SharedHandleOnlyExport:
                OperatingSystem.IsWindows() && !anyNDI && HardwareVideoDecodeGate.HardwareDecodeEnabled,
            IncludeAudioRouter: true,
            AudioPacketQueueDepth: 720,   // ~15 s @ ~21 ms/packet
            VideoPacketQueueDepth: 512,   // ~21 s @ 24 fps
            FileReadBufferBytes: 4 * 1024 * 1024);

    /// <summary>Reads a video output line's declared pixel resolution (local window size, or NDI resolution
    /// lock). Returns false when the output declares none.</summary>
    internal static bool TryGetOutputResolution(OutputDefinition definition, out int width, out int height)
    {
        switch (definition)
        {
            case LocalVideoOutputDefinition { WindowWidth: > 0, WindowHeight: > 0 } lv:
                width = lv.WindowWidth!.Value;
                height = lv.WindowHeight!.Value;
                return true;
            case NDIOutputDefinition { ResolutionLockWidth: > 0, ResolutionLockHeight: > 0 } nd:
                width = nd.ResolutionLockWidth!.Value;
                height = nd.ResolutionLockHeight!.Value;
                return true;
            default:
                width = 0;
                height = 0;
                return false;
        }
    }

    /// <summary>Wraps an NDI video sender in a <see cref="LockedFormatVideoOutput"/> when the output declares a
    /// pixel-format or resolution lock; otherwise returns it unchanged. The wrapper never disposes the inner
    /// sender (the caller owns its lifetime).</summary>
    internal static IVideoOutput WrapWithNDILockIfNeeded(IVideoOutput ndiSender, NDIOutputDefinition nd, string name)
    {
        if (nd.PixelFormatLock is null && nd.ResolutionLockWidth is null && nd.ResolutionLockHeight is null)
            return ndiSender;
        return new LockedFormatVideoOutput(
            ndiSender,
            nd.PixelFormatLock,
            nd.ResolutionLockWidth,
            nd.ResolutionLockHeight,
            name,
            disposeInnerOnDispose: false);
    }
}
