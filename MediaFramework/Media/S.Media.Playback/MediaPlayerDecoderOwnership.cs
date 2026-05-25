namespace S.Media.Playback;

/// <summary>Who disposes the <see cref="S.Media.FFmpeg.MediaContainerDecoder"/> after <see cref="MediaPlayer"/> is disposed.</summary>
public enum MediaPlayerDecoderOwnership
{
    /// <summary><see cref="S.Media.FFmpeg.MediaContainerPlaybackBundle"/> disposes the decoder (default for path-based <see cref="MediaPlayer.TryOpen(string,...)"/>).</summary>
    BundleDisposesDecoder,

    /// <summary>Caller opened the decoder and keeps ownership — the bundle must not dispose it.</summary>
    CallerKeepsDecoder,
}
