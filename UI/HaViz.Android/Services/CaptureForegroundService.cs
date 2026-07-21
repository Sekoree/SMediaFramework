using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Projection;
using Android.OS;

namespace HaViz.Android.Services;

/// <summary>
/// Foreground service required by MediaProjection playback capture: on Android 10+ the projection
/// may only be obtained while a foreground service of type mediaProjection is running, so the
/// AudioRecord lives here. Captures Media/Game/Unknown-usage playback as float32 48 kHz stereo
/// and pushes it into the handed-off <see cref="IPcmSink"/> until stopped or killed.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaProjection,
    Name = "dev.sekoree.haviz.CaptureForegroundService")]
public sealed class CaptureForegroundService : Service
{
    private const string ChannelId = "haviz-capture";
    private const int NotificationId = 0xB0;
    private const int SampleRate = 48_000;
    private const int Channels = 2;

    internal sealed record HandoffData(
        Result ConsentResultCode, Intent ConsentData, IPcmSink Sink, TaskCompletionSource<bool> Started);

    /// <summary>
    /// Static handoff from <see cref="SystemAudioCapture.StartAsync"/>: the consent Intent could
    /// travel as an extra, but the sink is a live object and cannot - so both ride here, written
    /// immediately before StartForegroundService and consumed exactly once in OnStartCommand.
    /// </summary>
    internal static HandoffData? Pending;

    /// <summary>Raised from OnDestroy so <see cref="SystemAudioCapture.IsCapturing"/> cannot go
    /// stale when the system kills the service or the user revokes the projection.</summary>
    internal static event Action? Stopped;

    private MediaProjection? _projection;
    private ProjectionCallback? _projectionCallback;
    private AudioRecord? _record;
    private Thread? _thread;
    private IPcmSink? _sink;
    private volatile bool _running;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var handoff = Interlocked.Exchange(ref Pending, null);
        if (handoff is null)
        {
            // Restarted by the system without a consent token - nothing useful to do.
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        try
        {
            // StartForeground must precede GetMediaProjection or the framework throws SecurityException.
            StartInForeground();
            StartCapture(handoff);
            handoff.Started.TrySetResult(true);
        }
        catch (Exception)
        {
            handoff.Started.TrySetResult(false);
            StopSelf();
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        StopCapture();
        Stopped?.Invoke();
        base.OnDestroy();
    }

    private void StartInForeground()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(
            new NotificationChannel(ChannelId, "System audio capture", NotificationImportance.Low));
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("HaViz")!
            .SetContentText("Capturing system audio")!
            .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)!
            .SetOngoing(true)!
            .Build()!;
        StartForeground(NotificationId, notification, ForegroundService.TypeMediaProjection);
    }

    private void StartCapture(HandoffData handoff)
    {
        var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        _projection = manager.GetMediaProjection((int)handoff.ConsentResultCode, handoff.ConsentData)
                      ?? throw new InvalidOperationException("MediaProjection unavailable");
        // Android 14+ requires a registered callback before the projection is used for capture.
        _projectionCallback = new ProjectionCallback(this);
        _projection.RegisterCallback(_projectionCallback, null);

        var config = new AudioPlaybackCaptureConfiguration.Builder(_projection)
            .AddMatchingUsage(AudioUsageKind.Media)!
            .AddMatchingUsage(AudioUsageKind.Game)!
            .AddMatchingUsage(AudioUsageKind.Unknown)!
            .Build()!;
        var format = new AudioFormat.Builder()
            .SetEncoding(Encoding.PcmFloat)!
            .SetSampleRate(SampleRate)!
            .SetChannelMask(ChannelOut.Stereo)! // CHANNEL_IN_STEREO shares the mask value
            .Build()!;
        _record = new AudioRecord.Builder()
            .SetAudioFormat(format)!
            .SetBufferSizeInBytes(SampleRate * Channels * sizeof(float) / 2)! // ~500 ms of headroom
            .SetAudioPlaybackCaptureConfig(config)!
            .Build()!;
        if (_record.State != State.Initialized)
            throw new InvalidOperationException("AudioRecord init failed");

        _sink = handoff.Sink;
        _running = true;
        _record.StartRecording();
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "haviz-capture" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        // 20 ms blocks: small enough for visualizer latency, big enough to keep JNI overhead low.
        var buffer = new float[SampleRate / 50 * Channels];
        while (_running)
        {
            var read = _record!.Read(buffer, 0, buffer.Length, 0 /* READ_BLOCKING */);
            if (read <= 0)
            {
                if (_running)
                    Thread.Sleep(5); // transient error/stopped record; spin down gently
                continue;
            }

            _sink!.SubmitPcm(new ReadOnlySpan<float>(buffer, 0, read), SampleRate, Channels);
        }
    }

    private void StopCapture()
    {
        _running = false;
        try
        {
            _record?.Stop(); // unblocks the blocking Read so the thread can exit
        }
        catch (Exception)
        {
            // Never started recording.
        }

        _thread?.Join(1_000);
        _thread = null;
        _record?.Release();
        _record = null;
        if (_projectionCallback is not null)
            _projection?.UnregisterCallback(_projectionCallback);
        _projectionCallback = null;
        _projection?.Stop();
        _projection = null;
    }

    /// <summary>The system revoked the projection (or another app claimed it) - shut down.</summary>
    private sealed class ProjectionCallback : MediaProjection.Callback
    {
        private readonly CaptureForegroundService _service;

        public ProjectionCallback(CaptureForegroundService service) => _service = service;

        public override void OnStop() => _service.StopSelf();
    }
}
