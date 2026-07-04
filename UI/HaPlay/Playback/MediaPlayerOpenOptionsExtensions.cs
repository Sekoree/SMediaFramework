using S.Media.Decode.FFmpeg.Video;
using S.Media.Players;

namespace HaPlay.Playback;

/// <summary>
/// Maps the player-level <see cref="MediaPlayerOpenOptions"/> onto the FFmpeg decoder's
/// <see cref="VideoDecoderOpenOptions"/> for HaPlay's own decoder-cache opens (the rewrite dropped the
/// built-in <c>MediaPlayerOpenOptions.ToVideoDecoderOpenOptions</c> since the framework no longer references
/// the concrete decoder; HaPlay, which manages its own decoders, owns the mapping).
/// </summary>
internal static class MediaPlayerOpenOptionsExtensions
{
    public static VideoDecoderOpenOptions ToVideoDecoderOpenOptions(this MediaPlayerOpenOptions o) =>
        new()
        {
            TryHardwareAcceleration = o.TryHardwareAcceleration,
            RetainDmabufForGl = o.RetainDmabufForGl,
            RetainD3D11SharedHandleForGl = o.RetainD3D11SharedHandleForGl,
            Win32Nv12SharedHandleOnlyExport = o.Win32Nv12SharedHandleOnlyExport,
            AudioPacketQueueDepth = o.AudioPacketQueueDepth,
            VideoPacketQueueDepth = o.VideoPacketQueueDepth,
            FileReadBufferBytes = o.FileReadBufferBytes,
            AudioStreamIndex = o.AudioStreamIndex,
            VideoStreamIndex = o.VideoStreamIndex,
        };
}
