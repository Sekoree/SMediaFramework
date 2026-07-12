using System.Globalization;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Encode.FFmpeg;

namespace HaPlay.OutputPreview;

/// <summary>
/// The record-to-file line's runtime: holds the line's definition and, while ARMED, an
/// <see cref="FFmpegEncodeSession"/> whose sinks playback acquires exactly like an NDI carrier's
/// (single-holder video/audio sides via <see cref="AcquireForPlayback"/>). Arm/disarm is an explicit
/// operator action - a file line that merely exists never writes anything. Each arm opens a fresh
/// file ("{timestamp}" in the pattern expands per arm), disarm flushes encoders and writes the trailer.
/// </summary>
internal sealed class FileOutputRuntime : IDisposable
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("HaPlay.OutputPreview.FileOutputRuntime");

    private readonly object _gate = new();
    private FileOutputDefinition _definition;
    private FFmpegEncodeSession? _session;
    private string? _currentFilePath;
    private bool _videoAcquired;
    private bool _audioAcquired;
    private bool _disposed;

    public FileOutputRuntime(FileOutputDefinition definition) => _definition = definition;

    public FileOutputDefinition Definition
    {
        get { lock (_gate) return _definition; }
    }

    public bool IsArmed
    {
        get { lock (_gate) return _session is not null; }
    }

    /// <summary>The file the current (or last) armed session writes to.</summary>
    public string? CurrentFilePath
    {
        get { lock (_gate) return _currentFilePath; }
    }

    public FFmpegEncodeSessionMetrics? GetMetrics()
    {
        FFmpegEncodeSession? session;
        lock (_gate) session = _session;
        return session?.GetMetrics();
    }

    /// <summary>Validates the definition's encode settings against this FFmpeg build (empty = ok).</summary>
    public IReadOnlyList<string> ValidateEncode() => BuildOptions(Definition.EffectiveEncode).Validate();

    /// <summary>Creates the encode session and opens the output file. Throws on invalid options or an
    /// un-creatable file. No-op when already armed.</summary>
    public void Arm(int audioSampleRate = 48_000)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
                return;

            var encode = _definition.EffectiveEncode;
            var options = BuildOptions(encode);
            var path = ResolveFilePath(_definition, options.Container);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(path), audioSampleRate);
            _currentFilePath = path;
            Trace.LogInformation("record armed: '{Name}' → {Path}", _definition.EffectiveName, path);
        }
    }

    /// <summary>Finishes the recording (flush + trailer) and drops the session. Safe when not armed.</summary>
    public async Task DisarmAsync()
    {
        FFmpegEncodeSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _videoAcquired = false;
            _audioAcquired = false;
        }

        if (session is null)
            return;

        try
        {
            await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            Trace.LogInformation("record finished: '{Name}' → {Path}", Definition.EffectiveName, CurrentFilePath);
        }
        catch (Exception ex)
        {
            Trace.LogError(ex, "record finish failed for '{Name}' - file may be truncated", Definition.EffectiveName);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>Single-holder acquire of the armed session's sinks (mirrors the NDI carrier semantics).
    /// Returns nulls when disarmed or the requested side is already held / not part of the output mode.</summary>
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
                // The COMBINED sink: all tracks as concatenated channels, so a deck/cue channel matrix
                // routes source channels onto specific tracks (ch 0..k-1 = track 1, k.. = track 2, …).
                _audioAcquired = true;
                audio = combined;
            }

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
        }
    }

    public void Reconfigure(FileOutputDefinition definition)
    {
        lock (_gate)
        {
            // Encode settings apply on the NEXT arm; an armed session keeps its opened file/options
            // (changing codecs mid-file is impossible anyway).
            _definition = definition;
        }
    }

    internal static EncodeSessionOptions BuildOptions(EncodeSettingsDefinition encode)
    {
        var legs = encode.AudioLegs.Count > 0 ? encode.AudioLegs : [new EncodeAudioLegDefinition()];
        return new EncodeSessionOptions
        {
            Container = ParseOrDefault(encode.Container, EncodeContainer.Mp4),
            OutputMode = ParseOrDefault(encode.OutputMode, EncodeOutputMode.VideoAndAudio),
            Video = new VideoEncodeOptions
            {
                Codec = ParseOrDefault(encode.VideoCodec, EncodeVideoCodec.H264),
                BitrateBps = encode.VideoBitrateBps,
                Crf = encode.VideoBitrateBps > 0 ? null : encode.VideoCrf,
                Preset = encode.VideoPreset,
                GopSize = encode.GopSize,
                ScaleWidth = encode.ScaleWidth,
                ScaleHeight = encode.ScaleHeight,
                Fps = encode.Fps,
            },
            AudioLegs = legs.Select(l => new AudioLegOptions
            {
                Codec = ParseOrDefault(l.Codec, EncodeAudioCodec.Aac),
                BitrateBps = l.BitrateBps,
                Channels = l.Channels,
                SampleRate = l.SampleRate,
                Name = l.Name,
                Language = l.Language,
            }).ToArray(),
        };
    }

    private static TEnum ParseOrDefault<TEnum>(string? value, TEnum fallback) where TEnum : struct =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static string ResolveFilePath(FileOutputDefinition definition, EncodeContainer container)
    {
        var name = string.IsNullOrWhiteSpace(definition.FileNamePattern)
            ? "recording_{timestamp}"
            : definition.FileNamePattern;
        name = name.Replace(
            "{timestamp}",
            DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);

        var extension = container switch
        {
            EncodeContainer.Mp4 => ".mp4",
            EncodeContainer.Matroska => ".mkv",
            EncodeContainer.Mov => ".mov",
            EncodeContainer.MpegTs => ".ts",
            EncodeContainer.Flv => ".flv",
            _ => ".mp4",
        };
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name += extension;

        return Path.Combine(definition.DirectoryPath, name);
    }

    public void Dispose()
    {
        FFmpegEncodeSession? session;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        // Synchronous best-effort finish: Dispose flushes + writes the trailer internally.
        if (session is not null)
            MediaDiagnostics.SwallowDisposeErrors(session.Dispose, "FileOutputRuntime.Dispose: encode session");
    }
}
