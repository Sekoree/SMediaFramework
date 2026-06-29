using System.Globalization;
using S.Media.Core;
using S.Media.Core.Audio;
using S.Media.Time;
using S.Media.Session;
using S.Media.Audio.PortAudio;
using S.Media.Players;
using S.Media.Present.SDL3;

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
        PortAudioOutput? audioOutput,
        CancellationTokenSource cts,
        ClipWindow clipWindow)
    {
        CueId = cueId;
        Player = player;
        VideoOutput = videoOutput;
        AudioOutput = audioOutput;
        Cts = cts;
        ClipWindow = clipWindow;
    }

    public Guid CueId { get; }
    public MediaPlayer Player { get; }
    public SDL3GLVideoOutput? VideoOutput { get; }

    // The master PortAudio output. In the rewritten framework PortAudioOutput is itself an IPlaybackClock,
    // so it drives the session's video presentation directly (the old PortAudioPlaybackHost.MainOutput role).
    public PortAudioOutput? AudioOutput { get; }
    public CancellationTokenSource Cts { get; }
    public ClipWindow ClipWindow { get; }

    public IPlaybackClock? VideoMaster => AudioOutput;

    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Player.Play(videoOnlyMaster: VideoMaster);
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

        // Player.Dispose tears down its router (which owns the attached output); the explicit dispose here is
        // a guarded best-effort backstop (PortAudioOutput.Dispose is idempotent).
        try { Player.Dispose(); } catch { /* best effort */ }
        try { AudioOutput?.Dispose(); } catch { /* best effort */ }
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
            PortAudioOutput? paOutput = null;

            try
            {
                if (hasVideo)
                {
                    sdl = new SDL3GLVideoOutput(
                        title: $"Preview: {fileItem.DisplayName}",
                        initialWidth: 480,
                        initialHeight: 270)
                    {
                        ViewportFit = S.Media.Gpu.VideoViewportFit.Contain,
                    };
                }

                var opts = new MediaPlayerOpenOptions(
                    TryHardwareAcceleration: true,
                    IncludeAudioRouter: hasAudio)
                {
                    // Preview hears the same track Go will play. Invalid/stale indices fall back to
                    // automatic inside the demuxer, so no signature re-resolution is needed here.
                    AudioStreamIndex = cue.AudioTrackIndex,
                };

                var builder = MediaPlayer.OpenFile(MediaRuntime.Registry, fileItem.Path)
                    .WithOptions(opts)
                    ;

                if (sdl is not null)
                    builder = builder.WithVideoLead(sdl, disposeOnPlayerDispose: false);

                if (!builder.TryBuild(out player, out var err))
                {
                    err ??= "Failed to open media.";
                    return ((CuePreviewSession?)null, err);
                }

                if (hasAudio && player.AudioRouter is not null)
                {
                    // Open a default PortAudio output at the source rate and attach it as the master. The
                    // router maps source→output channels (ChannelMap.DefaultFor) when they differ. Replaces
                    // the old builder WithPortAudio + PortAudioPlaybackHost convenience (gone in the rewrite).
                    var rate = player.SampleRate > 0 ? player.SampleRate : 48000;
                    var backend = new PortAudioBackend();
                    var deviceId = previewAudioDeviceIndex?.ToString(CultureInfo.InvariantCulture);
                    paOutput = backend.CreateOutput(deviceId, new AudioFormat(rate, 2)) as PortAudioOutput;
                    if (paOutput is null)
                        return ((CuePreviewSession?)null, "PortAudio preview wiring failed.");
                    player.AttachAudioOutput(paOutput);
                }

                var clipWindow = CueClipWindow.From(cue, player.Duration);
                if (clipWindow.Start > TimeSpan.Zero)
                    player.SeekCoordinated(clipWindow.Start);

                var session = new CuePreviewSession(cue.Id, player, sdl, paOutput, new CancellationTokenSource(), clipWindow);
                session.AttachVideoCloseHandler();
                player = null;
                sdl = null;
                paOutput = null;
                return ((CuePreviewSession?)session, (string?)null);
            }
            catch (Exception ex)
            {
                return ((CuePreviewSession?)null, ex.Message);
            }
            finally
            {
                player?.Dispose();
                paOutput?.Dispose();
                if (player is null)
                {
                    try { sdl?.Dispose(); } catch { /* best effort */ }
                }
            }
        }, ct).ConfigureAwait(false);
    }
}
