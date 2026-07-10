using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Phase C.5 (§6.8) - discriminated playlist entry. Replaces the v1 flat <see cref="string"/> path
/// list with a polymorphic union so live inputs (PortAudio capture, NDI receivers) sit alongside
/// file items in the same playlist / cue list. Persisted via <c>kind</c> discriminator so projects
/// round-trip even before <see cref="NDIInputPlaylistItem"/> / <see cref="PortAudioInputPlaylistItem"/>
/// can actually play (e.g. an audio-only NDI input saved today reloads fine after
/// <c>NDIVideoReceiver</c> backfill).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FilePlaylistItem), typeDiscriminator: "file")]
[JsonDerivedType(typeof(NDIInputPlaylistItem), typeDiscriminator: "ndi-input")]
[JsonDerivedType(typeof(PortAudioInputPlaylistItem), typeDiscriminator: "pa-input")]
[JsonDerivedType(typeof(ImagePlaylistItem), typeDiscriminator: "image")]
[JsonDerivedType(typeof(SubtitlePlaylistItem), typeDiscriminator: "subtitle")]
[JsonDerivedType(typeof(TextPlaylistItem), typeDiscriminator: "text")]
[JsonDerivedType(typeof(YouTubePlaylistItem), typeDiscriminator: "youtube")]
[JsonDerivedType(typeof(MMDPlaylistItem), typeDiscriminator: "mmd")]
public abstract record PlaylistItem
{
    /// <summary>Stable per-instance identity. Lets the same underlying source appear twice in a
    /// playlist (file added twice, two NDI receivers of the same source) without collisions on
    /// selection / equality. Round-trips through the project file so re-opening a project keeps
    /// the selected-item reference stable.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonIgnore] public abstract string DisplayName { get; }

    /// <summary>True when this item represents a live (non-finite, retry-on-disconnect) source.</summary>
    [JsonIgnore] public abstract bool IsLive { get; }

    /// <summary>Long-form tooltip / status-line text. Defaults to <see cref="DisplayName"/>.</summary>
    [JsonIgnore] public virtual string ToolTip => DisplayName;

    /// <summary>One-character glyph hint for the playlist row (file = blank, mic = "🎙", camera/NDI = "📡").</summary>
    [JsonIgnore] public virtual string KindGlyph => string.Empty;
}

/// <summary>File-on-disk playlist item - the v1-equivalent path, but now wrapped as a discriminated
/// item so it co-exists with live sources.</summary>
public sealed record FilePlaylistItem(string Path) : PlaylistItem
{
    /// <summary>Explicit audio track (container stream index) for multi-track files; <c>null</c> =
    /// automatic. A stale index falls back to automatic inside the demuxer, never an open failure.</summary>
    public int? AudioTrackIndex { get; init; }

    /// <summary>Subtitle tracks to render over this item when played in the media player - none / one / many
    /// (embedded stream or sidecar, with optional font/placement overrides). Empty = no subtitles.</summary>
    public IReadOnlyList<CueSubtitleSelection> Subtitles { get; init; } = [];

    public override string DisplayName =>
        string.IsNullOrEmpty(Path) ? "(empty)" : System.IO.Path.GetFileName(Path);
    public override bool IsLive => false;
    public override string ToolTip =>
        AudioTrackIndex is { } track ? $"{Path} · audio track #{track}" : Path;
}

public enum TextAlignH
{
    Left,
    Center,
    Right,
}

public enum TextAlignV
{
    Top,
    Middle,
    Bottom,
}

/// <summary>A still image shown as a cue for the cue's custom duration (a single held frame). Distinct
/// from <see cref="FilePlaylistItem"/> so the engine holds the frame rather than decoding it as video.</summary>
public sealed record ImagePlaylistItem(string Path) : PlaylistItem
{
    public override string DisplayName =>
        string.IsNullOrEmpty(Path) ? "(image)" : System.IO.Path.GetFileName(Path);
    public override bool IsLive => false;
    public override string ToolTip => Path;
    public override string KindGlyph => "🖼";
}

/// <summary>A standalone caption-overlay cue source: a sidecar subtitle file (.srt/.ass/…) rendered as a
/// timed transparent overlay, placeable on a composition like any video cue (no media clip). Font/placement
/// overrides apply to text formats; the render canvas is scaled onto the composition by the placement.</summary>
public sealed record SubtitlePlaylistItem(string Path) : PlaylistItem
{
    /// <summary>Override font family (libass fallback); <c>null</c> keeps the document font.</summary>
    public string? FontFamily { get; init; }

    /// <summary>Font-size multiplier (1.0 = document default); <c>null</c> keeps document sizing.</summary>
    public double? FontScale { get; init; }

    /// <summary>ASS numpad alignment 1–9; <c>null</c> keeps the document alignment.</summary>
    public int? Alignment { get; init; }

    /// <summary>Render canvas size; the placement scales it onto the composition.</summary>
    public int CanvasWidth { get; init; } = 1920;

    public int CanvasHeight { get; init; } = 1080;

    public override string DisplayName =>
        string.IsNullOrEmpty(Path) ? "(subtitles)" : System.IO.Path.GetFileName(Path);
    public override bool IsLive => false;
    public override string ToolTip => Path;
    public override string KindGlyph => "💬";
}

/// <summary>A rendered text / title card shown as a cue for the cue's custom duration (a single held
/// frame). Rendered to a BGRA frame by <c>TextFrameRenderer</c>; placeable on the composition like any
/// media cue. Colours are 0xAARRGGBB.</summary>
public sealed record TextPlaylistItem : PlaylistItem
{
    public string Text { get; init; } = "Text";

    public string FontFamily { get; init; } = "Inter";

    public double FontSizePx { get; init; } = 96;

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    /// <summary>Text fill colour (0xAARRGGBB). Default opaque white.</summary>
    public uint ColorArgb { get; init; } = 0xFFFFFFFF;

    /// <summary>Background box colour (0xAARRGGBB). Default fully transparent (no box).</summary>
    public uint BackgroundArgb { get; init; } = 0x00000000;

    /// <summary>Outline colour (0xAARRGGBB). Default opaque black; only drawn when width &gt; 0.</summary>
    public uint OutlineArgb { get; init; } = 0xFF000000;

    public double OutlineWidthPx { get; init; }

    public TextAlignH HAlign { get; init; } = TextAlignH.Center;

    public TextAlignV VAlign { get; init; } = TextAlignV.Middle;

    /// <summary>Word-wrap width as a fraction [0,1] of the canvas width. 0 = no wrap (single line grows).</summary>
    public double WrapWidthFraction { get; init; } = 0.9;

    public double PaddingPx { get; init; } = 24;

    /// <summary>Render canvas size. Defaults to 1080p; the placement system scales it onto the composition.</summary>
    public int CanvasWidth { get; init; } = 1920;

    public int CanvasHeight { get; init; } = 1080;

    public override string DisplayName
    {
        get
        {
            var trimmed = (Text ?? string.Empty).Trim();
            if (trimmed.Length == 0) return "(text)";
            var firstLine = trimmed.Split('\n', 2)[0];
            return firstLine.Length > 24 ? firstLine[..24] + "…" : firstLine;
        }
    }

    public override bool IsLive => false;
    public override string ToolTip => $"Text · {DisplayName}";
    public override string KindGlyph => "🅣";
}

/// <summary>An MMD scene: a PMX model + optional VMD motion + optional VMD camera
/// motion, rendered through the MMD compositor surface behind an <c>mmd://</c> URI. When no camera
/// VMD is set, the manual placement fields below drive the camera - the deck preview IS the
/// camera-placement view (tweak → replay → see the framing).</summary>
public sealed record MMDPlaylistItem(string ModelPath) : PlaylistItem
{
    /// <summary>MSAA in the GL renderer (the add-dialog toggle).</summary>
    public bool Antialias { get; init; } = true;

    /// <summary>Stage-5 physics - hair/skirt secondary motion (the add-dialog toggle).</summary>
    public bool Physics { get; init; } = true;

    public string? MotionPath { get; init; }

    /// <summary>Camera VMD; when set it overrides the manual placement below.</summary>
    public string? CameraMotionPath { get; init; }

    public int RenderWidth { get; init; } = 1280;
    public int RenderHeight { get; init; } = 720;

    // Manual camera placement (MMD conventions: orbit target at distance, XYZ rotation in degrees).
    public double CameraDistance { get; init; } = -35;
    public double CameraTargetX { get; init; }
    public double CameraTargetY { get; init; } = 12;
    public double CameraTargetZ { get; init; }
    public double CameraRotationXDeg { get; init; }
    public double CameraRotationYDeg { get; init; }
    public double CameraRotationZDeg { get; init; }
    public double CameraFovDeg { get; init; } = 30;

    public override string DisplayName =>
        string.IsNullOrEmpty(ModelPath) ? "(MMD model unset)" : System.IO.Path.GetFileName(ModelPath);
    public override bool IsLive => false;
    public override string ToolTip =>
        $"MMD · {ModelPath}" +
        (MotionPath is { Length: > 0 } m ? $" · {System.IO.Path.GetFileName(m)}" : " · bind pose") +
        (CameraMotionPath is { Length: > 0 } c ? $" · cam {System.IO.Path.GetFileName(c)}" : string.Empty);
    public override string KindGlyph => "🕺";
}

/// <summary>A YouTube video prepared into the local cache (Gate 5). Persists the RESOLVED stream
/// descriptors chosen in the add/edit dialog (muxed streams are rarely offered, so audio and video are
/// separate stream selections) plus display metadata cached at resolve time. Playback is reliable-mode:
/// the mapped <c>youtube://</c> URI only opens the locally cached asset - never the network.</summary>
public sealed record YouTubePlaylistItem(string VideoId) : PlaylistItem
{
    /// <summary>Video title cached at resolve time (display only - refresh by re-resolving).</summary>
    public string? Title { get; init; }

    public string? Author { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>Resolved video stream descriptor (<c>label|codec|container</c>); null = audio-only item.</summary>
    public string? VideoStreamDescriptor { get; init; }

    /// <summary>Resolved audio stream descriptor (<c>codec|container|language</c>).</summary>
    public string? AudioStreamDescriptor { get; init; }

    /// <summary>Selected caption-track language code; the prepared cache holds its sidecar .ass (converted
    /// from YouTube's rich json3 timedtext so colour/style/positioning survive).</summary>
    public string? SubtitleLanguage { get; init; }

    /// <summary>True = the video leg was deliberately not selected (audio-only cue/deck item).</summary>
    public bool AudioOnly { get; init; }

    /// <summary>Subtitle overlays for playback - filled with the prepared caption sidecar by the dialog;
    /// same shape as <see cref="FilePlaylistItem.Subtitles"/> so the overlay path is shared.</summary>
    public IReadOnlyList<CueSubtitleSelection> Subtitles { get; init; } = [];

    public override string DisplayName =>
        !string.IsNullOrWhiteSpace(Title) ? Title! : $"YouTube · {VideoId}";
    public override bool IsLive => false;
    public override string ToolTip =>
        $"YouTube · {VideoId}" +
        (VideoStreamDescriptor is { } v ? $" · {v.Split('|')[0]}" : " · audio only") +
        (Author is { Length: > 0 } a ? $" · {a}" : string.Empty);
    public override string KindGlyph => "▶";
}

/// <summary>NDI receiver item - identified by the NDI source name. Manual-name items load even when
/// the source is currently offline (§6.3); the playlist enters a "waiting for source" state on Play.</summary>
public sealed record NDIInputPlaylistItem(string SourceName) : PlaylistItem
{
    /// <summary>Optional human label override; defaults to the NDI source name.</summary>
    public string? CustomDisplayName { get; init; }

    /// <summary>Request the sender's low-bandwidth preview stream (NDI 5+).</summary>
    public bool LowBandwidth { get; init; }

    /// <summary>Discard the sender's video side even if present.</summary>
    public bool AudioOnly { get; init; }

    /// <summary>Discard the sender's audio side even if present.</summary>
    public bool VideoOnly { get; init; }

    /// <summary>Reconnect interval (seconds) when the source disappears. 0 disables retries.</summary>
    public int RetrySeconds { get; init; } = 5;

    /// <summary>Manual override for the audio jitter-buffer reserve, in milliseconds. <c>null</c> keeps the
    /// framework default (~50 ms). Smaller brings the audio forward toward the live video - lower latency, at
    /// more underrun risk; use the dialog's probe to find the lowest glitch-free size for this network.</summary>
    public int? AudioMinBufferedDurationMs { get; init; }

    public override string DisplayName =>
        !string.IsNullOrWhiteSpace(CustomDisplayName) ? CustomDisplayName!
        : string.IsNullOrWhiteSpace(SourceName) ? "(NDI source unset)"
        : SourceName;

    public override bool IsLive => true;
    public override string ToolTip => $"NDI · {SourceName}";
    public override string KindGlyph => "📡";
}

/// <summary>PortAudio capture device item - host API + device name + channel count + sample rate.
/// On load, the device is resolved by name first with <see cref="GlobalDeviceIndex"/> as fallback so
/// moving a USB interface between ports doesn't silently break the binding (§6.4).</summary>
public sealed record PortAudioInputPlaylistItem(string DeviceName) : PlaylistItem
{
    /// <summary>Optional human label override; defaults to the device name.</summary>
    public string? CustomDisplayName { get; init; }

    public string? HostApiName { get; init; }
    public int? HostApiIndex { get; init; }

    /// <summary>Last known global device index for the device - used as the load-time fallback when
    /// no device with <see cref="DeviceName"/> exists on the host.</summary>
    public int? GlobalDeviceIndex { get; init; }

    public int Channels { get; init; } = 2;
    public int SampleRate { get; init; } = 48000;

    /// <summary>Optional suggested latency override (seconds); null uses the device's
    /// default-low-input-latency.</summary>
    public double? SuggestedLatency { get; init; }

    public override string DisplayName =>
        !string.IsNullOrWhiteSpace(CustomDisplayName) ? CustomDisplayName!
        : string.IsNullOrWhiteSpace(DeviceName) ? "(PortAudio device unset)"
        : DeviceName;

    public override bool IsLive => true;
    public override string ToolTip =>
        !string.IsNullOrWhiteSpace(HostApiName)
            ? $"PortAudio · {HostApiName} · {DeviceName}"
            : $"PortAudio · {DeviceName}";
    public override string KindGlyph => "🎙";
}
