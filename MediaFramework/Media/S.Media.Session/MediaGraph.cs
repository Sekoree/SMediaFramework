using System.Diagnostics.CodeAnalysis;
using S.Media.Players;
using S.Media.Routing;
using S.Media.Core.Registry;
using S.Media.Core.Video;

namespace S.Media.Session;

/// <summary>High-level playback graph topology built by <see cref="MediaGraphBuilder"/>.</summary>
public enum MediaGraphTopology
{
    /// <summary>File-backed media decoder with a video router and optional audio router.</summary>
    FilePlayback,
    FileToAudioDevice,
    FileToLocalDisplayAndNDI,
    FileToPreviewAndProgram,
    NDIInputToPreviewAndProgram,
    CueCompositor,
    SoundboardToAudioOutput,
}

public sealed record MediaGraphPreset(
    MediaGraphTopology Topology,
    string Name,
    string Description,
    bool RequiresExternalDevice = false);

public sealed record MediaGraphValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static MediaGraphValidationResult Success(params string[] warnings) =>
        new(true, [], warnings);

    public static MediaGraphValidationResult Failure(params string[] errors) =>
        new(false, errors, []);
}

/// <summary>
/// Owning handle for a built playback graph. It exposes the underlying player for transport control,
/// but centralizes ownership and health snapshots for product-facing hosts.
/// </summary>
public sealed class MediaGraph : IDisposable, IAsyncDisposable
{
    internal MediaGraph(MediaSession session, MediaGraphTopology topology, string description)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Topology = topology;
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    /// <summary>Owning session; dispose the graph to tear it down.</summary>
    public MediaSession Session { get; }

    /// <summary>The player that drives transport, routing, and metrics.</summary>
    public MediaPlayer Player => Session.Player;

    /// <summary>Topology preset used to build this graph.</summary>
    public MediaGraphTopology Topology { get; }

    /// <summary>Human-readable topology description for logs/UI.</summary>
    public string Description { get; }

    /// <summary>One-shot health/metrics snapshot across the graph.</summary>
    public MediaPlayerMetrics GetHealthSnapshot() => Player.GetMetrics();

    public void Dispose() => Session.Dispose();

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}

/// <summary>Entry points for common playback graph presets.</summary>
public static class MediaGraphBuilder
{
    public static IReadOnlyList<MediaGraphPreset> CommonPresets { get; } =
    [
        new(MediaGraphTopology.FilePlayback, "File to local display", "File decoder, video router, optional audio router."),
        new(MediaGraphTopology.FileToAudioDevice, "File to audio device", "File decoder plus audio router with optional package audio device output.", RequiresExternalDevice: true),
        new(MediaGraphTopology.FileToLocalDisplayAndNDI, "File to local display plus NDI output", "File decoder feeding local preview/display and NDI output branches.", RequiresExternalDevice: true),
        new(MediaGraphTopology.FileToPreviewAndProgram, "File to preview plus program output", "File decoder routed to separate preview and program outputs."),
        new(MediaGraphTopology.NDIInputToPreviewAndProgram, "NDI input to preview/program", "NDI receiver source routed to preview and program outputs.", RequiresExternalDevice: true),
        new(MediaGraphTopology.CueCompositor, "Cue compositor with audio/video outputs", "Cue graph driving compositor layers and audio/video routes."),
        new(MediaGraphTopology.SoundboardToAudioOutput, "Soundboard to audio output", "Soundboard grid/cues routed to an audio output.", RequiresExternalDevice: true),
    ];

    /// <summary>
    /// Builds a file playback graph. With no video output, a discard output keeps the graph valid;
    /// with <see cref="MediaGraphFileBuilder.WithVideoOutput"/>, the supplied output becomes the
    /// negotiation lead.
    /// </summary>
    public static MediaGraphFileBuilder File(string filePath) => new(filePath);
}

/// <summary>Builder for the file playback graph preset.</summary>
public sealed class MediaGraphFileBuilder(string filePath)
{
    private MediaPlayerOpenOptions _options = MediaPlayerOpenOptions.Default;
    private IVideoOutput? _videoOutput;
    private bool _disposeVideoOutput;

    public string FilePath { get; } = filePath ?? throw new ArgumentNullException(nameof(filePath));

    public MediaPlayerOpenOptions CurrentOptions => _options;

    public MediaGraphFileBuilder WithOptions(MediaPlayerOpenOptions options)
    {
        _options = options;
        return this;
    }

    public MediaGraphFileBuilder WithOptions(Func<MediaPlayerOpenOptions, MediaPlayerOpenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _options = configure(_options);
        return this;
    }

    /// <summary>Uses <paramref name="output"/> as the local display/preview output for the graph.</summary>
    public MediaGraphFileBuilder WithVideoOutput(IVideoOutput output, bool disposeOnGraphDispose = false)
    {
        _videoOutput = output ?? throw new ArgumentNullException(nameof(output));
        _disposeVideoOutput = disposeOnGraphDispose;
        return this;
    }

    /// <summary>Builds the graph and returns false instead of throwing on open/wiring failure. Opens
    /// through <paramref name="registry"/> (P2 - no globals).</summary>
    public bool TryBuild(IMediaRegistry registry, [NotNullWhen(true)] out MediaGraph? graph, out string? error)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var builder = MediaPlayer.OpenFile(registry, FilePath).WithOptions(_options);
        if (_videoOutput is not null)
            builder.WithVideoLead(_videoOutput, _disposeVideoOutput);

        if (!builder.TryBuild(out var player, out error))
        {
            graph = null;
            return false;
        }

        graph = new MediaGraph(MediaSession.Owning(player), MediaGraphTopology.FilePlayback, "file playback");
        return true;
    }

    /// <summary>Validates path/options before opening native decoder or device resources.</summary>
    public MediaGraphValidationResult DryRunValidate()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return MediaGraphValidationResult.Failure("file path is required.");
        if (!File.Exists(FilePath))
            return MediaGraphValidationResult.Failure($"file not found: {FilePath}");
        if (!_options.ValidateWin32Nv12Flags(out var error))
            return MediaGraphValidationResult.Failure(error ?? "invalid media player options.");

        return _videoOutput is null
            ? MediaGraphValidationResult.Success("no video output supplied; graph will use a discard negotiation output.")
            : MediaGraphValidationResult.Success();
    }

    /// <summary>Builds the graph or throws <see cref="InvalidOperationException"/> with the open failure.</summary>
    public MediaGraph Build(IMediaRegistry registry)
    {
        if (!TryBuild(registry, out var graph, out var error))
            throw new InvalidOperationException(error ?? "Media graph build failed.");
        return graph;
    }

    /// <summary>Async <see cref="Build(IMediaRegistry)"/> for UI hosts.</summary>
    public Task<MediaGraph> OpenAsync(IMediaRegistry registry, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Build(registry);
        }, cancellationToken);
}
