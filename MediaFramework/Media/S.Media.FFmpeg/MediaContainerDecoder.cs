using S.Media.Core.Audio;
using S.Media.Core.Diagnostics;
using S.Media.Core.Video;
using S.Media.FFmpeg.Audio;
using S.Media.FFmpeg.Video;

namespace S.Media.FFmpeg;

/// <summary>
/// Opens the same media file for <strong>both</strong> audio and video decoding with a single
/// host-facing object. <see cref="SeekPresentation"/> seeks both streams to the same timeline position.
/// </summary>
/// <remarks>
/// <para>
/// This façade always uses <see cref="MediaContainerSharedDemux"/>: one libav
/// <c>AVFormatContext</c>, a background demux thread with bounded per-stream packet queues, and
/// independent audio/video decode locks so producers can run on different threads. Video follows
/// <see cref="VideoDecoderOpenOptions"/>: hardware acceleration is attempted first (with software
/// fallback inside the shared video codec setup), including Linux DRM PRIME semi-planar **NV12/P010** when
/// <see cref="VideoDecoderOpenOptions.RetainDmabufForGl"/> is enabled and Windows D3D11 NV12 shared handles when
/// <see cref="VideoDecoderOpenOptions.RetainD3D11SharedHandleForGl"/> is enabled. Optional
/// <see cref="VideoDecoderOpenOptions.Win32Nv12SharedHandleOnlyExport"/> (or <c>MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY</c>) omits libav D3D11 COM pointers on Win32 NV12 backing.
/// </para>
/// </remarks>
public sealed class MediaContainerDecoder : IDisposable
{
    private readonly MediaContainerSharedDemux _shared;
    private readonly string? _ownedTempPath;

    private MediaContainerDecoder(MediaContainerSharedDemux shared, string? ownedTempPath = null)
    {
        _shared = shared;
        _ownedTempPath = ownedTempPath;
    }

    /// <summary>Audio side — <see cref="ISeekableSource"/> for coordinated seeks. For video-only
    /// files <see cref="HasAudio"/> is <c>false</c> and this source reports <c>IsExhausted = true</c>
    /// immediately; consumers should check <see cref="HasAudio"/> before wiring an audio path.</summary>
    public IAudioSource Audio => _shared.Audio;

    /// <summary>Video side — <see cref="ISeekableSource"/>.</summary>
    public IVideoSource Video => _shared.Video;

    /// <summary>True when the container exposed a decodable audio stream — false for video-only files.</summary>
    public bool HasAudio => _shared.HasAudio;

    /// <summary>True when the container exposed a decodable video stream — false for audio-only files
    /// (e.g. an MP3 without cover art). <see cref="Video"/> stays non-null but its source reports
    /// <c>IsExhausted = true</c> immediately, so the negotiated video pipeline runs but never produces frames.</summary>
    public bool HasVideo => _shared.HasVideo;

    /// <summary>True when the chosen video stream is <c>AV_DISPOSITION_ATTACHED_PIC</c> (album cover art).
    /// Always false when <see cref="HasVideo"/> is false. Single-frame for the entire file.</summary>
    public bool VideoIsAttachedPicture => _shared.VideoIsAttachedPicture;

    /// <summary>
    /// Best available container duration. Uses stream duration when available
    /// and falls back to container duration; returns <see cref="TimeSpan.Zero"/>
    /// for live or unknown-duration media.
    /// </summary>
    public TimeSpan Duration => _shared.Duration;

    /// <summary>Always true for this implementation.</summary>
    public bool UsesSharedDemux => true;

    /// <summary>
    /// True when Windows D3D11 NV12 decode exports <see cref="Win32SharedNv12Backing"/> with DXGI NT shared handle only
    /// (no libav <c>ID3D11Device</c>/<c>ID3D11Texture2D</c> COM pointers on the backing). See
    /// <see cref="VideoDecoderOpenOptions.Win32Nv12SharedHandleOnlyExport"/> and <c>MF_MEDIA_WIN32_NV12_SHARED_HANDLE_ONLY</c>.
    /// </summary>
    public bool Win32Nv12SharedHandleOnlyActive => _shared.Win32Nv12SharedHandleOnlyActive;

    public static MediaContainerDecoder Open(string path, VideoDecoderOpenOptions? videoOptions = null) =>
        OpenInput(path, videoOptions, validateLocalFile: true, ownedTempPath: null);

    /// <summary>
    /// Opens a local media file. Prefer this explicit helper when accepting file paths from end users;
    /// use <see cref="OpenUri"/> for network/protocol URLs.
    /// </summary>
    public static MediaContainerDecoder OpenFile(string path, VideoDecoderOpenOptions? videoOptions = null) =>
        Open(path, videoOptions);

    /// <summary>
    /// Opens a media URI. <c>file:</c> URIs are validated as local files; other absolute URI schemes
    /// are passed to FFmpeg as protocol inputs (for example <c>http:</c>, <c>https:</c>, or <c>rtsp:</c>).
    /// </summary>
    public static MediaContainerDecoder OpenUri(Uri uri, VideoDecoderOpenOptions? videoOptions = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("media URI must be absolute.", nameof(uri));

        return uri.IsFile
            ? OpenInput(uri.LocalPath, videoOptions, validateLocalFile: true, ownedTempPath: null)
            : OpenInput(uri.AbsoluteUri, videoOptions, validateLocalFile: false, ownedTempPath: null);
    }

    /// <summary>
    /// Opens a finite media stream via in-memory AVIO (no temp-file spool by default).
    /// Use <see cref="OpenStreamSpooled"/> or <see cref="MediaContainerOpenStreamOptions.SpoolToDisk"/> when probing requires a full file on disk.
    /// </summary>
    public static MediaContainerDecoder OpenStream(
        Stream stream,
        bool isSeekable = false,
        string? probeHintName = null,
        VideoDecoderOpenOptions? videoOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("media stream must be readable.", nameof(stream));

        FFmpegRuntime.EnsureInitialized();

        var shared = MediaContainerSharedDemux.Open(stream, isSeekable, probeHintName, videoOptions ?? new VideoDecoderOpenOptions());
        return new MediaContainerDecoder(shared);
    }

    /// <summary>Opens a stream with explicit <see cref="MediaContainerOpenStreamOptions"/>.</summary>
    public static MediaContainerDecoder OpenStream(Stream stream, MediaContainerOpenStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        return options.SpoolToDisk
            ? OpenStreamSpooled(stream, options.ProbeHintName, options.VideoOptions)
            : OpenStream(stream, options.IsSeekable, options.ProbeHintName, options.VideoOptions);
    }

    /// <summary>
    /// Opens a finite media stream by spooling it to a temporary file owned by the decoder.
    /// Prefer <see cref="OpenStream"/> for in-memory AVIO when the full stream is already available.
    /// </summary>
    public static MediaContainerDecoder OpenStreamSpooled(
        Stream stream,
        string? inputName = null,
        VideoDecoderOpenOptions? videoOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("media stream must be readable.", nameof(stream));

        FFmpegRuntime.EnsureInitialized();

        var tempPath = BuildTempInputPath(inputName);
        try
        {
            using (var file = File.Create(tempPath))
                stream.CopyTo(file);

            return OpenInput(tempPath, videoOptions, validateLocalFile: true, ownedTempPath: tempPath);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static MediaContainerDecoder OpenInput(
        string input,
        VideoDecoderOpenOptions? videoOptions,
        bool validateLocalFile,
        string? ownedTempPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        if (validateLocalFile && !File.Exists(input))
            throw new FileNotFoundException("media file not found", input);

        FFmpegRuntime.EnsureInitialized();

        var opt = videoOptions ?? new VideoDecoderOpenOptions();
        var shared = MediaContainerSharedDemux.Open(input, opt);
        return new MediaContainerDecoder(shared, ownedTempPath);
    }

    /// <summary>Seeks both streams to the same presentation timestamp.</summary>
    public void SeekPresentation(TimeSpan position) => _shared.SeekPresentation(position);

    /// <summary>
    /// Cooperatively aborts an in-flight <see cref="SeekPresentation"/> (or coordinated seek) whose
    /// decode-to-target prime is running long. The seek returns at its best-effort position rather than
    /// blocking the caller for the full internal deadline. Wire a host seek <see cref="CancellationToken"/>
    /// to this (see <c>MediaContainerSession.SeekCoordinated</c>) so a UI seek timeout can actually stop the
    /// native work instead of orphaning the thread. A no-op when no seek is running.
    /// </summary>
    public void CancelInFlightSeek() => _shared.CancelInFlightSeek();

    /// <summary>
    /// Re-syncs libav decoders and the demuxer to the current mux playhead without changing the
    /// logical timeline: <see cref="SeekPresentation"/> at <c>max(</c><see cref="Video.Position"/><c>, </c>
    /// <see cref="Audio.Position"/><c>)</c>.
    /// </summary>
    /// <remarks>
    /// Call only when no concurrent discrete-frame reads are in flight (same contract as <see cref="SeekPresentation"/>).
    /// Prefer this name at pause boundaries when the intent is “drain internal delay” rather than “jump to a new time”.
    /// </remarks>
    public void FlushCodecPipelines()
    {
        var vp = Video is ISeekableSource vs ? vs.Position : TimeSpan.Zero;
        var ap = Audio is ISeekableSource ais ? ais.Position : TimeSpan.Zero;
        var resume = vp >= ap ? vp : ap;
        if (resume < TimeSpan.Zero)
            resume = TimeSpan.Zero;
        SeekPresentation(resume);
    }

    /// <summary>When Windows D3D11VA NV12 shared-handle decode is active, libav's <c>ID3D11Device</c> COM pointer for GL upload.</summary>
    public bool TryGetHardwareD3D11DeviceForWin32Gl(out nint deviceComPtr) =>
        _shared.TryGetHardwareD3D11DeviceForWin32Gl(out deviceComPtr);

    /// <summary>When Windows D3D11VA NV12 shared-handle decode is active, DXGI adapter LUID (packed) for diagnostics.</summary>
    public bool TryGetHardwareD3D11AdapterLuid(out long adapterLuidPacked) =>
        _shared.TryGetHardwareD3D11AdapterLuid(out adapterLuidPacked);

    public void Dispose()
    {
        _shared.Dispose();
        if (_ownedTempPath is not null)
            TryDeleteTempFile(_ownedTempPath);
    }

    private static string BuildTempInputPath(string? inputName)
    {
        var ext = string.IsNullOrWhiteSpace(inputName) ? ".media" : Path.GetExtension(inputName);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 16 || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            ext = ".media";

        return Path.Combine(Path.GetTempPath(), $"mf_stream_{Guid.NewGuid():N}{ext}");
    }

    private static void TryDeleteTempFile(string path)
    {
        MediaDiagnostics.SwallowDisposeErrors(() =>
        {
            if (File.Exists(path))
                File.Delete(path);
        }, "MediaContainerDecoder: temp stream delete");
    }
}
