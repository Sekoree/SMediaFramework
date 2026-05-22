using System.Runtime.InteropServices;
using NDILib;
using S.Media.Core.Audio;
using S.Media.NDI.Clock;

namespace S.Media.NDI.Input;

/// <summary><see cref="IAudioSource"/> that pulls samples from <see cref="NDIFrameSync"/> (NDI clock path).</summary>
public sealed class NdiFrameSyncAudioSource : IAudioSource, IDisposable
{
    private readonly NDIFrameSync _frameSync;
    private readonly NDIIngestPlaybackClock _ingestClock;
    private AudioFormat _format;
    private bool _hasFormat;
    private bool _disposed;

    internal NdiFrameSyncAudioSource(NDIFrameSync frameSync, NDIIngestPlaybackClock ingestClock)
    {
        _frameSync = frameSync ?? throw new ArgumentNullException(nameof(frameSync));
        _ingestClock = ingestClock ?? throw new ArgumentNullException(nameof(ingestClock));
    }

    public bool IsConnected => _hasFormat;

    public AudioFormat Format =>
        _hasFormat
            ? _format
            : throw new InvalidOperationException(
                "NDI frame-sync audio has not delivered a frame yet — wait until IsConnected is true");

    public bool IsExhausted => _disposed;

    public int ReadInto(Span<float> dst)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (dst.Length == 0)
            return 0;

        if (!_hasFormat)
        {
            _frameSync.CaptureAudio(out var probe, sampleRate: 0, channels: 0, samples: 0);
            try
            {
                if (probe.SampleRate <= 0 || probe.NoChannels <= 0)
                    return 0;
                _format = new AudioFormat(probe.SampleRate, probe.NoChannels);
                _hasFormat = true;
            }
            finally
            {
                _frameSync.FreeAudio(in probe);
            }
        }

        var channels = _format.Channels;
        if (dst.Length % channels != 0)
            throw new ArgumentException(
                $"dst length {dst.Length} is not a multiple of channel count {channels}", nameof(dst));

        var samples = dst.Length / channels;
        _frameSync.CaptureAudio(out var audio, _format.SampleRate, channels, samples);
        try
        {
            if (audio.NoSamples <= 0 || audio.NoChannels <= 0)
            {
                dst.Clear();
                return dst.Length;
            }

            _ingestClock.NotifyAudioFrame(in audio);
            return NdiAudioFrameConverter.CopyInterleaved32f(in audio, dst);
        }
        finally
        {
            _frameSync.FreeAudio(in audio);
        }
    }

    public void Dispose() => _disposed = true;
}
