using System.Globalization;
using Microsoft.Extensions.Logging;
using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.Encode.FFmpeg;
using HaPlay.Resources;

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
    private ContinuousEncodeCarrier? _carrier;
    private ContentOnlyEncodeVideoSink? _contentOnlyVideoSink;
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

    /// <summary>Validates the definition's encode settings and recording-clock policy (empty = ok).</summary>
    public IReadOnlyList<string> ValidateEncode()
    {
        var definition = Definition;
        var options = BuildOptions(definition.EffectiveEncode);
        var errors = options.Validate().ToList();
        if (definition.RecordsContinuousProgram
            && options.IncludesVideo
            && (options.Video.ScaleWidth <= 0 || options.Video.ScaleHeight <= 0 || options.Video.Fps <= 0))
        {
            errors.Add(Strings.FileOutputContinuousFormatRequired);
        }
        return errors;
    }

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
            if (_definition.RecordsContinuousProgram
                && options.IncludesVideo
                && (options.Video.ScaleWidth <= 0 || options.Video.ScaleHeight <= 0 || options.Video.Fps <= 0))
            {
                throw new ArgumentException(Strings.FileOutputContinuousFormatRequired);
            }

            var path = ReserveUniqueFilePath(_definition, options.Container);
            FFmpegEncodeSession? session = null;
            ContinuousEncodeCarrier? carrier = null;
            try
            {
                session = FFmpegEncodeSession.Create(options, new FileEncodeTarget(path), audioSampleRate);
                if (_definition.RecordsContinuousProgram)
                {
                    carrier = new ContinuousEncodeCarrier(
                        session,
                        options.IncludesVideo ? options.Video.ScaleWidth : 0,
                        options.IncludesVideo ? options.Video.ScaleHeight : 0,
                        options.IncludesVideo ? options.Video.Fps : 0);
                    carrier.Start();
                }

                _session = session;
                _carrier = carrier;
                _contentOnlyVideoSink = carrier is null && session.VideoSink is { } contentVideo
                    ? new ContentOnlyEncodeVideoSink(contentVideo)
                    : null;
            }
            catch
            {
                carrier?.Dispose();
                session?.Dispose();
                TryDeleteEmptyReservation(path);
                throw;
            }
            _currentFilePath = path;
            Trace.LogInformation(
                "record armed: '{Name}' → {Path} ({Mode})",
                _definition.EffectiveName,
                path,
                _definition.EffectiveRecordingMode);
        }
    }

    /// <summary>Finishes the recording (flush + trailer) and drops the session. Safe when not armed.</summary>
    public async Task DisarmAsync()
    {
        FFmpegEncodeSession? session;
        ContinuousEncodeCarrier? carrier;
        lock (_gate)
        {
            session = _session;
            carrier = _carrier;
            _session = null;
            _carrier = null;
            _contentOnlyVideoSink = null;
            _videoAcquired = false;
            _audioAcquired = false;
        }

        if (session is null)
            return;

        carrier?.Dispose();
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
            if (needsVideo
                && !_videoAcquired
                && (_carrier?.VideoSink ?? (IVideoOutput?)_contentOnlyVideoSink ?? _session.VideoSink) is { } vs)
            {
                _videoAcquired = true;
                video = vs;
            }

            if (needsAudio
                && !_audioAcquired
                && (_carrier?.CombinedAudioSink ?? _session.CombinedAudioSink) is { } combined)
            {
                // The COMBINED sink: all tracks as concatenated channels, so a deck/cue channel matrix
                // routes source channels onto specific tracks (ch 0..k-1 = track 1, k.. = track 2, …).
                _audioAcquired = true;
                audio = combined;
            }

            _carrier?.SetPlaybackActive(_videoAcquired, _audioAcquired);
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
            _carrier?.SetPlaybackActive(_videoAcquired, _audioAcquired);
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
        var videoCodec = ParseOrDefault(encode.VideoCodec, EncodeVideoCodec.H264);
        return new EncodeSessionOptions
        {
            Container = ParseOrDefault(encode.Container, EncodeContainer.Mp4),
            OutputMode = ParseOrDefault(encode.OutputMode, EncodeOutputMode.VideoAndAudio),
            Video = new VideoEncodeOptions
            {
                Codec = videoCodec,
                BitrateBps = encode.VideoBitrateBps,
                BitrateMode = ParseOrDefault(encode.VideoBitrateMode, EncodeVideoBitrateMode.Average),
                BufferSizeBits = VbvBufferBits(
                    encode.VideoBitrateBps,
                    encode.VideoVbvBufferMilliseconds),
                Crf = encode.VideoBitrateBps > 0 || !videoCodec.SupportsCrf() ? null : encode.VideoCrf,
                Preset = videoCodec.SupportsNamedPreset() ? encode.VideoPreset : null,
                GopSize = encode.GopSize,
                MaxBFrames = encode.VideoMaxBFrames,
                LowLatencyTune = encode.VideoLowLatencyTune,
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

    private static long VbvBufferBits(long bitrateBps, int bufferMilliseconds)
    {
        if (bitrateBps <= 0 || bufferMilliseconds <= 0)
            return 0;
        var bits = (decimal)bitrateBps * bufferMilliseconds / 1000m;
        return bits >= long.MaxValue ? long.MaxValue : (long)bits;
    }

    internal static string ReserveUniqueFilePath(FileOutputDefinition definition, EncodeContainer container)
    {
        Directory.CreateDirectory(definition.DirectoryPath);
        var name = string.IsNullOrWhiteSpace(definition.FileNamePattern)
            ? "recording_{timestamp}"
            : definition.FileNamePattern;
        name = name.Replace(
            "{timestamp}",
            DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);

        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name))
            name = "recording_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var extension = container.GetFileExtension();
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name += extension;

        var stem = name[..^extension.Length];
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var candidateName = suffix == 0 ? name : $"{stem}_{suffix}{extension}";
            var candidate = Path.Combine(definition.DirectoryPath, candidateName);
            try
            {
                using var reservation = new FileStream(
                    candidate, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Collision with an existing recording or another concurrent arm; try the next suffix.
            }
        }

        throw new IOException($"Could not reserve a unique recording path in '{definition.DirectoryPath}'.");
    }

    private static void TryDeleteEmptyReservation(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);
        }
        catch { /* best effort - never hide the encoder/open failure */ }
    }

    public void Dispose()
    {
        FFmpegEncodeSession? session;
        ContinuousEncodeCarrier? carrier;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            session = _session;
            carrier = _carrier;
            _session = null;
            _carrier = null;
            _contentOnlyVideoSink = null;
        }

        // Shutdown disposal is an abort; the operator's normal Disarm path above performs the trailer flush.
        // Stop the shared carrier first so it cannot submit into the retiring encode session.
        carrier?.Dispose();
        if (session is not null)
            MediaDiagnostics.SwallowDisposeErrors(session.Dispose, "FileOutputRuntime.Dispose: encode session");
    }
}
