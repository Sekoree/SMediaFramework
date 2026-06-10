using System.Text.Json.Serialization;

namespace HaPlay.Models;

/// <summary>
/// Phase C.5 (§6.8) — discriminated playlist entry. Replaces the v1 flat <see cref="string"/> path
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
[JsonDerivedType(typeof(TextPlaylistItem), typeDiscriminator: "text")]
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

/// <summary>File-on-disk playlist item — the v1-equivalent path, but now wrapped as a discriminated
/// item so it co-exists with live sources.</summary>
public sealed record FilePlaylistItem(string Path) : PlaylistItem
{
    /// <summary>Explicit audio track (container stream index) for multi-track files; <c>null</c> =
    /// automatic. A stale index falls back to automatic inside the demuxer, never an open failure.</summary>
    public int? AudioTrackIndex { get; init; }

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

/// <summary>NDI receiver item — identified by the NDI source name. Manual-name items load even when
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

    public override string DisplayName =>
        !string.IsNullOrWhiteSpace(CustomDisplayName) ? CustomDisplayName!
        : string.IsNullOrWhiteSpace(SourceName) ? "(NDI source unset)"
        : SourceName;

    public override bool IsLive => true;
    public override string ToolTip => $"NDI · {SourceName}";
    public override string KindGlyph => "📡";
}

/// <summary>PortAudio capture device item — host API + device name + channel count + sample rate.
/// On load, the device is resolved by name first with <see cref="GlobalDeviceIndex"/> as fallback so
/// moving a USB interface between ports doesn't silently break the binding (§6.4).</summary>
public sealed record PortAudioInputPlaylistItem(string DeviceName) : PlaylistItem
{
    /// <summary>Optional human label override; defaults to the device name.</summary>
    public string? CustomDisplayName { get; init; }

    public string? HostApiName { get; init; }
    public int? HostApiIndex { get; init; }

    /// <summary>Last known global device index for the device — used as the load-time fallback when
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
