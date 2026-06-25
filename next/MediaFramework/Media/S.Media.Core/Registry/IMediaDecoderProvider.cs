using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Registry;

/// <summary>Which stream kind a decoder is being asked to open.</summary>
public enum MediaKind
{
    Video,
    Audio,
}

/// <summary>
/// Opens media addressed by a URI scheme (D2: <c>file:</c> / <c>ndi:</c> / <c>capture:</c> / <c>mic:</c> /
/// <c>image:</c> / <c>http(s):</c>). When several providers can open the same URI the registry picks by
/// confidence (D3): highest <see cref="Probe"/> wins, ties broken by registration order.
/// </summary>
public interface IMediaDecoderProvider
{
    /// <summary>Stable provider name (e.g. <c>"FFmpeg"</c>). Used for diagnostics and explicit pinning.</summary>
    string Name { get; }

    /// <summary>
    /// Confidence in <c>[0,1]</c> that this provider can open <paramref name="uri"/> for
    /// <paramref name="kind"/>; <c>0</c> means "cannot". The registry selects the highest score and
    /// breaks ties in favour of the earliest-registered provider (D3).
    /// </summary>
    double Probe(string uri, MediaKind kind);

    /// <summary>Opens the video track of <paramref name="uri"/>. Throws if it cannot.</summary>
    IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options);

    /// <summary>
    /// Opens an audio track of <paramref name="uri"/>. Throws if it cannot. Multi-track selection
    /// (none/one/many, 03 §6) is carried in <paramref name="options"/>; per-track enumeration arrives
    /// with the FFmpeg provider in Phase 2.
    /// </summary>
    IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options);
}
