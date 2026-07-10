namespace S.Media.Session.Tests;

/// <summary>A registry decoder that opens any URI as a short synthetic silent audio clip (no FFmpeg/device).
/// <paramref name="chunks"/> sizes the source (default 8 reads ≈ instant EOF - tests that need the clip to
/// STAY ALIVE through pauses/fades must pass a large count, since natural EOF now legitimately flips the
/// player to not-running).</summary>
internal sealed class FakeAudioDecoderProvider(int chunks = 8) : IMediaDecoderProvider
{
    public string Name => "fake-audio";

    public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) =>
        throw new NotSupportedException("fake provider is audio-only");

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new SyntheticSilentSource(chunks);

    public static IMediaRegistry Registry(int chunks = 8) =>
        MediaRegistry.Build(b => b.AddDecoder(new FakeAudioDecoderProvider(chunks)));
}

/// <summary>A provider whose atomic open BLOCKS until the token is cancelled - to verify a STOP/abort preempts
/// an in-flight cold clip open (NXT-03). Probes only the <c>blocking://</c> scheme.</summary>
internal sealed class BlockingOpenProvider : IMediaDecoderProvider
{
    public string Name => "blocking";
    public double Probe(string uri, MediaKind kind) => uri.StartsWith("blocking://", StringComparison.Ordinal) ? 1.0 : 0.0;
    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => throw new NotSupportedException();
    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => throw new NotSupportedException();

    public async ValueTask<MediaOpenResult> OpenAsync(
        MediaOpenRequest request, IProgress<MediaPrepareProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); // blocks until cancelled
        throw new InvalidOperationException("unreachable");
    }

    public static IMediaRegistry Registry() => MediaRegistry.Build(b => b.AddDecoder(new BlockingOpenProvider()));
}

/// <summary>A finite stereo source that yields a few chunks of silence then exhausts - enough for a clip
/// to open and Play() without a real decoder or device.</summary>
internal sealed class SyntheticSilentSource(int chunks = 8) : IAudioSource, ISeekableSource
{
    private int _remaining = chunks;
    private TimeSpan _position;

    public AudioFormat Format { get; } = new(48_000, 2);

    public bool IsExhausted => Volatile.Read(ref _remaining) <= 0;

    public int ReadInto(Span<float> destination)
    {
        if (Interlocked.Decrement(ref _remaining) < 0)
            return 0;
        destination.Clear();
        return destination.Length - (destination.Length % Format.Channels);
    }

    // Seekable so transport tests can drive SeekAsync/SeekCoordinated against the fake (a real file decoder
    // implements this; without it a session-level seek test throws before reaching the code under test).
    public TimeSpan Duration => TimeSpan.FromSeconds(10);
    public TimeSpan Position => _position;
    public void Seek(TimeSpan position) => _position = position;
}

/// <summary>A fake audio backend that records every output it creates (channel count + device id) - lets a
/// headless test assert the routing scene's N→M channel counts and the per-group multi-output fan-out.</summary>
internal sealed class RecordingAudioBackend : IAudioBackend
{
    public string Name => "recording";
    private readonly List<(int Channels, string? DeviceId, int SampleRate)> _created = [];

    public IReadOnlyList<(int Channels, string? DeviceId, int SampleRate)> Created => _created;
    public int OutputCount => _created.Count;
    public int LastOutputChannels => _created.Count > 0 ? _created[^1].Channels : 0;

    public IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices() =>
        [new AudioDeviceInfo("dev0", "Recording Output", MaxChannels: 8, DefaultSampleRate: 48_000, IsDefault: true)];

    public IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices() => [];

    public IAudioOutput CreateOutput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null)
    {
        _created.Add((format.Channels, deviceId, format.SampleRate));
        return new SinkAudioOutput(format);
    }

    public IAudioSource CreateInput(string? deviceId, AudioFormat format, AudioBackendOptions? options = null) =>
        throw new NotSupportedException();
}

internal sealed class SinkAudioOutput(AudioFormat format) : IAudioOutput
{
    public AudioFormat Format => format;
    public void Submit(ReadOnlySpan<float> packedSamples) { }
}

/// <summary>An <see cref="IAudioOutput"/> that also counts Dispose calls - lets a test prove the session did
/// (or did NOT) dispose a host-provided output, i.e. the borrowed-output ownership contract of the audio-output
/// factory seam (an NDI carrier's audio side must never be disposed by the session).</summary>
internal sealed class TrackingAudioOutput(AudioFormat format) : IAudioOutput, IDisposable
{
    private int _disposes;
    public int DisposeCount => Volatile.Read(ref _disposes);
    public AudioFormat Format => format;
    public void Submit(ReadOnlySpan<float> packedSamples) { }
    public void Dispose() => Interlocked.Increment(ref _disposes);
}

/// <summary>A video source that reports a finite Duration but NEVER exhausts (like a rendered text / still held
/// frame). Used to prove <c>EndAtDuration</c> stops the clip via the time-based end-monitor, not source EOF.</summary>
internal sealed class UnboundedHeldVideoSource(TimeSpan duration) : IVideoSource, ISeekableSource
{
    private long _next;

    public VideoFormat Format { get; } = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
    public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
    public bool IsExhausted => false; // held frame - never runs out
    public TimeSpan Duration { get; } = duration;
    public TimeSpan Position => TimeSpan.FromTicks(TimeSpan.TicksPerSecond * Volatile.Read(ref _next) / 30);

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != PixelFormat.Bgra32)
            throw new NotSupportedException();
    }

    public void Seek(TimeSpan position) =>
        Volatile.Write(ref _next, Math.Max(0, (long)(position.TotalSeconds * 30)));

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        var index = Interlocked.Increment(ref _next) - 1;
        var bytes = new byte[4 * 4 * 4];
        for (var i = 3; i < bytes.Length; i += 4)
            bytes[i] = 255;
        frame = new VideoFrame(TimeSpan.FromTicks(TimeSpan.TicksPerSecond * index / 30), Format, [bytes], [4 * 4]);
        return true;
    }
}

internal sealed class UnboundedHeldProvider(TimeSpan duration) : IMediaDecoderProvider
{
    public string Name => "held";
    public double Probe(string uri, MediaKind kind) =>
        kind == MediaKind.Video && uri.StartsWith("held://", StringComparison.Ordinal) ? 1.0 : 0.0;
    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new UnboundedHeldVideoSource(duration);
    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => throw new NotSupportedException();

    public static IMediaRegistry Registry(TimeSpan duration) =>
        MediaRegistry.Build(b => b.AddDecoder(new UnboundedHeldProvider(duration)));
}

internal sealed class FakeVideoDecoderProvider(int frameCount = 30) : IMediaDecoderProvider
{
    public string Name => "fake-video";

    public double Probe(string uri, MediaKind kind) => kind == MediaKind.Video ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new SyntheticVideoSource(frameCount);

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException("fake provider is video-only");

    /// <summary>A registry whose fake video clips run <paramref name="frameCount"/>/30 seconds. Tests that
    /// STOP a playing clip need runway (a slow CI runner can otherwise reach the clip's natural fade-out
    /// window first, whose claim then beats the stop's - the stop returns without ramping).</summary>
    public static IMediaRegistry Registry(int frameCount = 30) =>
        MediaRegistry.Build(b => b.AddDecoder(new FakeVideoDecoderProvider(frameCount)));
}

internal sealed class SyntheticVideoSource(int frameCount = 30) : IVideoSource, ISeekableSource
{
    private readonly int FrameCount = frameCount;
    private int _next;

    public VideoFormat Format { get; } = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
    public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
    public bool IsExhausted => Volatile.Read(ref _next) >= FrameCount;
    public TimeSpan Duration => TimeSpan.FromSeconds(FrameCount / 30.0);
    public TimeSpan Position => TimeSpan.FromTicks(TimeSpan.TicksPerSecond * Volatile.Read(ref _next) / 30);

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != PixelFormat.Bgra32)
            throw new NotSupportedException();
    }

    public void Seek(TimeSpan position) =>
        Volatile.Write(ref _next, Math.Clamp((int)(position.TotalSeconds * 30), 0, FrameCount));

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        var index = Interlocked.Increment(ref _next) - 1;
        if (index >= FrameCount)
        {
            frame = null!;
            return false;
        }

        var bytes = new byte[4 * 4 * 4];
        for (var i = 3; i < bytes.Length; i += 4)
            bytes[i] = 255; // opaque black: placement tests can distinguish content from the clear canvas
        frame = new VideoFrame(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond * index / 30),
            Format,
            [bytes],
            [4 * 4]);
        return true;
    }
}

/// <summary>A video provider whose sources fault on every <c>Seek</c> - models a decoder that throws
/// mid-coordinated-seek (observed live: <c>avcodec_send_packet</c> EINVAL). Playback itself is normal, so
/// tests can pin the session's restore-play-state-on-failed-seek contract.</summary>
internal sealed class ThrowingSeekVideoProvider(int frameCount = 300) : IMediaDecoderProvider
{
    public string Name => "throwing-seek";

    public double Probe(string uri, MediaKind kind) => kind == MediaKind.Video ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new ThrowingSeekVideoSource(frameCount);

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException("throwing-seek provider is video-only");

    public static IMediaRegistry Registry(int frameCount = 300) =>
        MediaRegistry.Build(b => b.AddDecoder(new ThrowingSeekVideoProvider(frameCount)));
}

internal sealed class ThrowingSeekVideoSource(int frameCount) : IVideoSource, ISeekableSource
{
    private readonly SyntheticVideoSource _inner = new(frameCount);

    public VideoFormat Format => _inner.Format;
    public IReadOnlyList<PixelFormat> NativePixelFormats => _inner.NativePixelFormats;
    public bool IsExhausted => _inner.IsExhausted;
    public TimeSpan Duration => _inner.Duration;
    public TimeSpan Position => _inner.Position;
    public void SelectOutputFormat(PixelFormat format) => _inner.SelectOutputFormat(format);
    public bool TryReadNextFrame(out VideoFrame frame) => _inner.TryReadNextFrame(out frame);

    public void Seek(TimeSpan position) =>
        throw new InvalidOperationException("synthetic seek fault (ThrowingSeekVideoSource)");
}

internal sealed class SyntheticLiveVideoSource : ILiveVideoSource
{
    private long _next;
    private long _baseTicks;

    public VideoFormat Format { get; } = new(4, 4, PixelFormat.Bgra32, new Rational(30, 1));
    public IReadOnlyList<PixelFormat> NativePixelFormats { get; } = [PixelFormat.Bgra32];
    public bool IsExhausted => false;

    public void SelectOutputFormat(PixelFormat format)
    {
        if (format != PixelFormat.Bgra32)
            throw new NotSupportedException();
    }

    public void RebaseToLatest(TimeSpan playClockNow)
    {
        Volatile.Write(ref _baseTicks, playClockNow.Ticks);
        Interlocked.Exchange(ref _next, 0);
    }

    public bool TryReadNextFrame(out VideoFrame frame)
    {
        var index = Interlocked.Increment(ref _next) - 1;
        var pts = TimeSpan.FromTicks(
            Volatile.Read(ref _baseTicks) + TimeSpan.TicksPerSecond * index / 30);
        frame = new VideoFrame(pts, Format, [new byte[4 * 4 * 4]], [4 * 4]);
        return true;
    }
}

internal sealed class FakeLiveVideoDecoderProvider : IMediaDecoderProvider
{
    public string Name => "fake-live-video";
    public double Probe(string uri, MediaKind kind) =>
        kind == MediaKind.Video && uri.StartsWith("live://", StringComparison.Ordinal) ? 1.0 : 0.0;
    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new SyntheticLiveVideoSource();
    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => throw new NotSupportedException();

    public static IMediaRegistry Registry() =>
        MediaRegistry.Build(b => b.AddDecoder(new FakeLiveVideoDecoderProvider()));
}
