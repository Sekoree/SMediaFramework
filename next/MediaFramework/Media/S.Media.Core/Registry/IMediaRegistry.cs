using System.Diagnostics.CodeAnalysis;
using S.Media.Core.Audio;
using S.Media.Core.Video;

namespace S.Media.Core.Registry;

/// <summary>
/// Immutable, queryable snapshot of registered capabilities, injected into players/sessions. The
/// capability set is frozen at build time (D6 — AOT-clean); device enumeration <em>within</em> a backend
/// stays dynamic (see <see cref="IDeviceChangeNotifier"/>). A <c>false</c>/<c>null</c> result means no
/// registered module provides the capability — which is how "no audio module ⇒ no audio output" falls
/// out without special-casing.
/// </summary>
public interface IMediaRegistry
{
    /// <summary>Registered audio backends, in registration order. Empty ⇒ no audio I/O available.</summary>
    IReadOnlyList<IAudioBackend> AudioBackends { get; }

    /// <summary>Registered decoder providers, in registration order.</summary>
    IReadOnlyList<IMediaDecoderProvider> Decoders { get; }

    /// <summary>True if some decoder reports non-zero confidence for <paramref name="uri"/> (D2/D3).</summary>
    bool CanOpen(string uri, MediaKind kind);

    /// <summary>Opens the video track of <paramref name="uri"/> via the highest-confidence decoder (D3).</summary>
    bool TryOpenVideo(string uri, VideoSourceOpenOptions? options, [MaybeNullWhen(false)] out IVideoSource source);

    /// <summary>Opens an audio track of <paramref name="uri"/> via the highest-confidence decoder (D3).</summary>
    bool TryOpenAudio(string uri, AudioSourceOpenOptions? options, [MaybeNullWhen(false)] out IAudioSource source);

    /// <summary>The decoder provider registered under <paramref name="name"/> (case-insensitive), or <c>null</c>.</summary>
    IMediaDecoderProvider? FindDecoder(string name);

    /// <summary>Opens video via an explicitly <strong>pinned</strong> provider (D3 — bypasses confidence
    /// selection). Returns <c>false</c> if no provider named <paramref name="providerName"/> is registered.</summary>
    bool TryOpenVideo(string uri, VideoSourceOpenOptions? options, string providerName, [MaybeNullWhen(false)] out IVideoSource source);

    /// <summary>Opens audio via an explicitly <strong>pinned</strong> provider (D3 — bypasses confidence selection).</summary>
    bool TryOpenAudio(string uri, AudioSourceOpenOptions? options, string providerName, [MaybeNullWhen(false)] out IAudioSource source);

    /// <summary>Opens a still image by file extension (an image source registered by a module).</summary>
    bool TryOpenImage(string path, [MaybeNullWhen(false)] out IVideoSource source);

    /// <summary>Creates a CPU pixel converter, or <c>null</c> when no module registered one.</summary>
    IVideoCpuFrameConverter? CreateCpuConverter();

    /// <summary>Wraps <paramref name="source"/> to resample to <paramref name="targetSampleRate"/>, or <c>null</c> if unavailable.</summary>
    IAudioSource? CreateResampler(IAudioSource source, int targetSampleRate);

    /// <summary>Creates a deinterlacer; falls back to the built-in <see cref="BobDeinterlacer"/> when no module set one.</summary>
    IDeinterlacer CreateDeinterlacer(VideoFormat input);
}
