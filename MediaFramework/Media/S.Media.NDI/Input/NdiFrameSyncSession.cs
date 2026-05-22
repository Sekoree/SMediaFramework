using NDILib;
using S.Media.NDI.Clock;

namespace S.Media.NDI.Input;

/// <summary>One NDI receiver + <see cref="NDIFrameSync"/> for pull-mode A/V aligned to the NDI timebase.</summary>
public sealed class NdiFrameSyncSession : IDisposable
{
    private readonly NDIRuntime _runtime;
    private readonly NDIReceiver _receiver;
    private readonly NDIFrameSync _frameSync;
    private readonly NDIIngestPlaybackClock _ingestClock;
    private bool _disposed;

    public NdiFrameSyncSession(
        NDIDiscoveredSource source,
        string? receiverName = null,
        NDIIngestPlaybackClock? ingestClock = null)
    {
        _ingestClock = ingestClock ?? new NDIIngestPlaybackClock();
        _ingestClock.AttachReceiver();

        var rc = NDIRuntime.Create(out var rt);
        if (rc != 0 || rt is null)
            throw new NDIException(rc, "NDIRuntime.Create");
        _runtime = rt;

        try
        {
            var settings = new NDIReceiverSettings
            {
                ReceiverName = receiverName,
                ColorFormat = NDIRecvColorFormat.BgrxBgra,
            };
            rc = NDIReceiver.Create(out var recv, settings);
            if (rc != 0 || recv is null)
                throw new NDIException(rc, "NDIReceiver.Create");
            _receiver = recv;
            _receiver.Connect(source);

            rc = NDIFrameSync.Create(out var fs, _receiver);
            if (rc != 0 || fs is null)
                throw new NDIException(rc, "NDIFrameSync.Create");
            _frameSync = fs;
        }
        catch
        {
            try { _runtime.Dispose(); } catch { /* best effort */ }
            throw;
        }

        Video = new NdiFrameSyncVideoSource(_frameSync, _ingestClock);
        Audio = new NdiFrameSyncAudioSource(_frameSync, _ingestClock);
    }

    public NDIIngestPlaybackClock IngestClock => _ingestClock;

    public NdiFrameSyncVideoSource Video { get; }

    public NdiFrameSyncAudioSource Audio { get; }

    internal NDIFrameSync FrameSync => _frameSync;

    /// <summary>Re-anchor ingest timeline when transport starts.</summary>
    public void ResetForPlay() => _ingestClock.AttachReceiver();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { Video.Dispose(); } catch { /* best effort */ }
        try { Audio.Dispose(); } catch { /* best effort */ }
        try { _ingestClock.NotifyCaptureStopped(); } catch { /* best effort */ }
        try { _frameSync.Dispose(); } catch { /* best effort */ }
        try { _receiver.Dispose(); } catch { /* best effort */ }
        try { _runtime.Dispose(); } catch { /* best effort */ }
    }
}
