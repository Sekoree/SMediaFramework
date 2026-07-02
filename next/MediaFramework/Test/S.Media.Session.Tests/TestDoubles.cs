namespace S.Media.Session.Tests;

/// <summary>A registry decoder that opens any URI as a short synthetic silent audio clip (no FFmpeg/device).</summary>
internal sealed class FakeAudioDecoderProvider : IMediaDecoderProvider
{
    public string Name => "fake-audio";

    public double Probe(string uri, MediaKind kind) => kind == MediaKind.Audio ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) =>
        throw new NotSupportedException("fake provider is audio-only");

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) => new SyntheticSilentSource();

    public static IMediaRegistry Registry() => MediaRegistry.Build(b => b.AddDecoder(new FakeAudioDecoderProvider()));
}

/// <summary>A provider whose atomic open BLOCKS until the token is cancelled — to verify a STOP/abort preempts
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

/// <summary>A finite stereo source that yields a few chunks of silence then exhausts — enough for a clip
/// to open and Play() without a real decoder or device.</summary>
internal sealed class SyntheticSilentSource : IAudioSource, ISeekableSource
{
    private int _remaining = 8;
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

/// <summary>A fake audio backend that records every output it creates (channel count + device id) — lets a
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

/// <summary>An <see cref="IAudioOutput"/> that also counts Dispose calls — lets a test prove the session did
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
    public bool IsExhausted => false; // held frame — never runs out
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

internal sealed class FakeVideoDecoderProvider : IMediaDecoderProvider
{
    public string Name => "fake-video";

    public double Probe(string uri, MediaKind kind) => kind == MediaKind.Video ? 1.0 : 0.0;

    public IVideoSource OpenVideo(string uri, VideoSourceOpenOptions? options) => new SyntheticVideoSource();

    public IAudioSource OpenAudio(string uri, AudioSourceOpenOptions? options) =>
        throw new NotSupportedException("fake provider is video-only");

    public static IMediaRegistry Registry() => MediaRegistry.Build(b => b.AddDecoder(new FakeVideoDecoderProvider()));
}

internal sealed class SyntheticVideoSource : IVideoSource, ISeekableSource
{
    private const int FrameCount = 30;
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
