using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Stream.Http;

namespace HaPlay.OutputPreview;

internal readonly record struct LiveStreamRuntimeState(
    bool IsLive,
    LiveStreamStatus? Status,
    string? LastError);

/// <summary>
/// The live-stream line's runtime: while LIVE it holds a <see cref="LiveStreamSession"/> (one encode →
/// N push sinks + optional LAN server) whose sinks playback acquires exactly like the file-record
/// runtime. Go-live/stop is an explicit operator action; each go-live builds a fresh session.
/// </summary>
internal sealed class LiveStreamOutputRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.OutputPreview.LiveStreamOutputRuntime");

    private readonly object _gate = new();
    private LiveStreamOutputDefinition _definition;
    private LiveStreamSession? _session;
    private string? _lastError;
    private bool _videoAcquired;
    private bool _audioAcquired;
    private bool _disposed;

    public LiveStreamOutputRuntime(LiveStreamOutputDefinition definition) => _definition = definition;

    public LiveStreamOutputDefinition Definition
    {
        get { lock (_gate) return _definition; }
    }

    public bool IsLive
    {
        get { lock (_gate) return _session is not null; }
    }

    /// <summary>The LAN server's bound port while live (0 otherwise / server disabled).</summary>
    public int LocalServerPort
    {
        get { lock (_gate) return _session?.LocalServerPort ?? 0; }
    }

    public LiveStreamStatus? GetStatus()
    {
        LiveStreamSession? session;
        lock (_gate) session = _session;
        return session?.GetStatus();
    }

    public LiveStreamRuntimeState GetRuntimeState()
    {
        lock (_gate)
            return new LiveStreamRuntimeState(
                _session is not null,
                _session?.GetStatus(),
                _lastError);
    }

    public IReadOnlyList<string> ValidateOptions() => BuildOptions(Definition).Validate();

    public void GoLive(int audioSampleRate = 48_000)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
                return;
            try
            {
                _session = LiveStreamSession.Start(BuildOptions(_definition), audioSampleRate);
                _lastError = null;
                Trace.LogInformation("stream live: '{Name}' (local port {Port})", _definition.EffectiveName, _session.LocalServerPort);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                throw;
            }
        }
    }

    public async Task StopStreamAsync()
    {
        LiveStreamSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _videoAcquired = false;
            _audioAcquired = false;
            _lastError = null;
        }

        if (session is null)
            return;

        try
        {
            await session.StopAsync().ConfigureAwait(false);
            Trace.LogInformation("stream stopped: '{Name}'", Definition.EffectiveName);
        }
        catch (Exception ex)
        {
            lock (_gate)
                _lastError = ex.Message;
            Trace.LogError(ex, "stream stop failed for '{Name}'", Definition.EffectiveName);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>Single-holder acquire of the live session's sinks (same semantics as the file-record runtime).</summary>
    public (IVideoOutput? Video, IAudioOutput? Audio) AcquireForPlayback(bool needsVideo, bool needsAudio)
    {
        lock (_gate)
        {
            if (_disposed || _session is null)
                return (null, null);

            IVideoOutput? video = null;
            IAudioOutput? audio = null;
            if (needsVideo && !_videoAcquired && _session.VideoSink is { } vs)
            {
                _videoAcquired = true;
                video = vs;
            }

            if (needsAudio && !_audioAcquired && _session.CombinedAudioSink is { } combined)
            {
                // Combined multi-track sink - see FileOutputRuntime; the route's channel matrix
                // decides which source channels land on which stream track.
                _audioAcquired = true;
                audio = combined;
            }

            // Declare which routes playback owns. The keep-alive continues sending filler until the
            // first actual sample reaches each wrapped sink, then resumes if that route falls silent.
            _session.SetPlaybackActive(_videoAcquired, _audioAcquired);
            return (video, audio);
        }
    }

    public void ReleaseFromPlayback(bool releaseVideo = true, bool releaseAudio = true)
    {
        lock (_gate)
        {
            if (releaseVideo)
                _videoAcquired = false;
            if (releaseAudio)
                _audioAcquired = false;
            // Playback let go of these legs - the keep-alive resumes filling them with blank/silence
            // so the stream stays live between tracks instead of going dead.
            _session?.SetPlaybackActive(_videoAcquired, _audioAcquired);
        }
    }

    public void Reconfigure(LiveStreamOutputDefinition definition)
    {
        lock (_gate)
        {
            // Applies on the NEXT go-live; a live session keeps its destinations.
            _definition = definition;
            _lastError = null;
        }
    }

    internal static LiveStreamOptions BuildOptions(LiveStreamOutputDefinition definition)
    {
        var encode = FileOutputRuntime.BuildOptions(definition.EffectiveEncode) with
        {
            // Live streams always mux per destination (flv/mpegts/hls); the persisted container is a
            // UI default only - force the session's primary shape to TS.
            Container = S.Media.Encode.FFmpeg.EncodeContainer.MpegTs,
        };

        var pushTargets = definition.PushTargets
            .Where(t => !string.IsNullOrWhiteSpace(t.Url))
            .Select(t => new PushTarget(
                Enum.TryParse<PushProtocol>(t.Protocol, ignoreCase: true, out var p) ? p : PushProtocol.Rtmp,
                t.Url.Trim(),
                string.IsNullOrWhiteSpace(t.StreamKey) ? null : t.StreamKey!.Trim())
            {
                SrtLatencyMilliseconds = t.SrtLatencyMilliseconds,
            })
            .ToArray();

        var server = definition.EffectiveLocalServer;
        return new LiveStreamOptions
        {
            Encode = encode,
            PushTargets = pushTargets,
            LocalServer = server.Enabled
                ? new LocalServerOptions(server.Port, server.EnableTs, server.EnableHls,
                    string.IsNullOrWhiteSpace(server.MountName) ? "stream" : server.MountName)
                : null,
        };
    }

    public void Dispose()
    {
        LiveStreamSession? session;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        if (session is not null)
            MediaDiagnostics.SwallowDisposeErrors(session.Dispose, "LiveStreamOutputRuntime.Dispose: session");
    }
}
