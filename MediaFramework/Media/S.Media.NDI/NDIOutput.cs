using NDILib;
using S.Media.Core.Audio;
using S.Media.NDI.Audio;
using S.Media.NDI.Video;

namespace S.Media.NDI;

/// <summary>
/// One NDI source on the network — owns a single <see cref="NDISender"/> plus
/// the <see cref="NDIRuntime"/> ref-count, and exposes child sinks for audio
/// and video. Receivers see one combined source carrying both streams.
/// </summary>
/// <remarks>
/// <para>
/// Audio-only / video-only is just a matter of which children you enable.
/// <see cref="EnableAudio"/> creates the audio sink; <see cref="VideoSink"/>
/// is lazy and created on first access. Don't touch the side you don't
/// need and the SDK simply won't transmit that stream.
/// </para>
/// <para>
/// Lifetime: child sinks must not outlive this <see cref="NDIOutput"/>.
/// They share the parent's sender; disposing the parent invalidates them.
/// </para>
/// </remarks>
public sealed class NDIOutput : IDisposable
{
    private readonly NDIRuntime _runtime;
    private readonly NDISender _sender;
    private readonly object _gate = new();
    private NDIAudioSink? _audioSink;
    private NDIVideoSender? _videoSink;
    private bool _disposed;

    public string SourceName { get; }
    public int ConnectionCount => _sender.GetConnectionCount();

    /// <summary>
    /// Video sink — always available; format negotiated via
    /// <see cref="Core.Video.IVideoSink.Configure"/>.
    /// </summary>
    public NDIVideoSender VideoSink
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _videoSink ??= CreateVideoSinkLocked();
        }
    }

    /// <summary>
    /// Construct an NDI source. <paramref name="clockVideo"/> /
    /// <paramref name="clockAudio"/> tell the SDK to pace each stream against
    /// its declared rate (typical for a self-driving sender).
    /// </summary>
    public NDIOutput(string sourceName, string? groups = null, bool clockVideo = true, bool clockAudio = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        SourceName = sourceName;

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null) throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            rc = NDISender.Create(out var sender, sourceName, groups, clockVideo: clockVideo, clockAudio: clockAudio);
            if (rc != 0 || sender is null) throw new NDIException(rc, "NDISender.Create");
            _sender = sender;
        }
        catch
        {
            _runtime.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Create the audio sink. Idempotent: returns the same instance on
    /// subsequent calls (an NDI source has at most one audio stream). Throws
    /// if already created with a different format.
    /// </summary>
    public NDIAudioSink EnableAudio(AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_audioSink is not null)
            {
                if (_audioSink.Format != format)
                    throw new InvalidOperationException(
                        $"audio sink already configured with format {_audioSink.Format}; cannot reconfigure to {format}");
                return _audioSink;
            }
            _audioSink = new NDIAudioSink(_sender, format);
            return _audioSink;
        }
    }

    private NDIVideoSender CreateVideoSinkLocked()
    {
        lock (_gate)
        {
            return _videoSink ??= new NDIVideoSender(_sender);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Tear down the video sink first so any in-flight async send is
        // flushed against a still-valid sender.
        try { _videoSink?.Dispose(); } catch { /* best effort */ }
        try { _audioSink?.Dispose(); } catch { /* best effort */ }
        _sender.Dispose();
        _runtime.Dispose();
    }
}
