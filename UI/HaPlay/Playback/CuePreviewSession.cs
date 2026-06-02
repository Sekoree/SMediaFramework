using HaPlay.Models;
using S.Media.Core.Clock;
using S.Media.Playback;
using S.Media.PortAudio;
using S.Media.SDL3;

namespace HaPlay.Playback;

/// <summary>
/// Transient audition path for a single file media cue — default PortAudio + optional SDL preview
/// window. Not registered in the engine's shared output pools (Phase 5.5).
/// </summary>
internal sealed class CuePreviewSession : IDisposable
{
    private bool _disposed;

    public CuePreviewSession(
        Guid cueId,
        MediaPlayer player,
        SDL3GLVideoOutput? videoOutput,
        PortAudioPlaybackHost? audioHost,
        CancellationTokenSource cts)
    {
        CueId = cueId;
        Player = player;
        VideoOutput = videoOutput;
        AudioHost = audioHost;
        Cts = cts;
    }

    public Guid CueId { get; }
    public MediaPlayer Player { get; }
    public SDL3GLVideoOutput? VideoOutput { get; }
    public PortAudioPlaybackHost? AudioHost { get; }
    public CancellationTokenSource Cts { get; }

    public IPlaybackClock? VideoMaster => AudioHost?.MainOutput;

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Action? prefill = null;
        Action? startHardware = null;
        if (AudioHost is { } host)
        {
            prefill = () => host.PrefillMainOutputDirectFromDecoder(TimeSpan.FromMilliseconds(250));
            startHardware = host.StartHardwareOutput;
        }

        Player.Play(
            prefillBeforeHardware: prefill,
            startHardware: startHardware,
            videoOnlyMaster: VideoMaster);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Cts.Cancel(); } catch { /* best effort */ }
        try { Cts.Dispose(); } catch { /* best effort */ }

        if (VideoOutput is { } sdl)
        {
            try { sdl.CloseRequested -= OnVideoCloseRequested; } catch { /* best effort */ }
        }

        try { Player.Dispose(); } catch { /* best effort */ }
        try { AudioHost?.Dispose(); } catch { /* best effort */ }
        try { VideoOutput?.Dispose(); } catch { /* best effort */ }
    }

    public event EventHandler? CloseRequested;

    public void AttachVideoCloseHandler()
    {
        if (VideoOutput is null) return;
        VideoOutput.CloseRequested += OnVideoCloseRequested;
    }

    private void OnVideoCloseRequested(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    public static async Task<(CuePreviewSession? Session, string? Error)> TryOpenAsync(
        MediaCueNode cue,
        CancellationToken ct,
        int? previewAudioDeviceIndex = null)
    {
        if (cue.Source is not FilePlaylistItem fileItem)
            return ((CuePreviewSession?)null, "Preview requires a file media source.");

        var hasVideo = cue.HasVideo;
        var hasAudio = cue.HasAudio;
        if (!hasVideo && !hasAudio)
            return ((CuePreviewSession?)null, "Cue has no audio or video to preview.");

        return await Task.Run<(CuePreviewSession? Session, string? Error)>(() =>
        {
            ct.ThrowIfCancellationRequested();
            SDL3GLVideoOutput? sdl = null;
            MediaPlayer? player = null;
            PortAudioPlaybackHost? paHost = null;

            try
            {
                if (hasVideo)
                {
                    sdl = new SDL3GLVideoOutput(
                        title: $"Preview: {fileItem.DisplayName}",
                        initialWidth: 480,
                        initialHeight: 270)
                    {
                        ViewportFit = S.Media.OpenGL.VideoViewportFit.Contain,
                    };
                }

                var opts = new MediaPlayerOpenOptions(
                    TryHardwareAcceleration: true,
                    IncludeAudioRouter: hasAudio);

                var builder = MediaPlayer.OpenFile(fileItem.Path)
                    .WithOptions(opts)
                    .WithDecoderOwnership(MediaPlayerDecoderOwnership.BundleDisposesDecoder);

                if (sdl is not null)
                    builder = builder.WithVideoLead(sdl, disposeOnPlayerDispose: false);

                if (hasAudio)
                    // This session owns paHost explicitly (stored + disposed in its lifecycle below),
                    // so opt out of player-owned-host transfer to keep a single owner.
                    builder = builder.WithPortAudio(deviceIndex: previewAudioDeviceIndex, transferHostOwnershipToPlayer: false);

                if (!builder.TryBuild(out player, out var err))
                {
                    err ??= "Failed to open media.";
                    return ((CuePreviewSession?)null, err);
                }

                if (hasAudio)
                {
                    paHost = builder.GetWiredPortAudioHost();
                    if (paHost is null)
                        return ((CuePreviewSession?)null, "PortAudio preview wiring failed.");
                }

                if (cue.StartOffsetMs > 0)
                    player.SeekCoordinated(TimeSpan.FromMilliseconds(cue.StartOffsetMs));

                var session = new CuePreviewSession(cue.Id, player, sdl, paHost, new CancellationTokenSource());
                session.AttachVideoCloseHandler();
                player = null;
                sdl = null;
                paHost = null;
                return ((CuePreviewSession?)session, (string?)null);
            }
            catch (Exception ex)
            {
                return ((CuePreviewSession?)null, ex.Message);
            }
            finally
            {
                player?.Dispose();
                paHost?.Dispose();
                if (player is null)
                {
                    try { sdl?.Dispose(); } catch { /* best effort */ }
                }
            }
        }, ct).ConfigureAwait(false);
    }
}
