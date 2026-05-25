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
    public override string DisplayName =>
        string.IsNullOrEmpty(Path) ? "(empty)" : System.IO.Path.GetFileName(Path);
    public override bool IsLive => false;
    public override string ToolTip => Path;
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
