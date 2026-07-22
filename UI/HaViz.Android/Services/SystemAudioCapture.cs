using Android.App;
using Android.Content;
using Android.Media.Projection;

namespace HaViz.Android.Services;

/// <summary>
/// Android 10+ system-audio capture front end: RECORD_AUDIO runtime permission, the
/// MediaProjection consent dialog, then <see cref="CaptureForegroundService"/> owns the actual
/// AudioRecord (the projection may only be obtained inside a mediaProjection foreground service).
/// PCM flows service -> sink on the capture thread.
/// </summary>
public sealed class SystemAudioCapture : ISystemAudioCapture
{
    private readonly MainActivity _activity;
    private readonly IPcmSink _sink;

    public SystemAudioCapture(MainActivity activity, IPcmSink sink)
    {
        _activity = activity;
        _sink = sink;
        // The service can die on its own (projection revoked, system kill) - track that here so
        // IsCapturing does not go stale.
        CaptureForegroundService.Stopped += OnServiceStopped;
    }

    public bool IsCapturing { get; private set; }

    public event Action? Stopped;

    public async Task<bool> StartAsync()
    {
        if (IsCapturing)
            return true;
        if (!await _activity.EnsurePermissionAsync(global::Android.Manifest.Permission.RecordAudio))
            return false;
        if (_activity.GetSystemService(Context.MediaProjectionService) is not MediaProjectionManager manager)
            return false;

        var consent = await _activity.StartActivityForResultAsync(manager.CreateScreenCaptureIntent()!);
        if (consent.ResultCode != Result.Ok || consent.Data is null)
            return false;

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureForegroundService.Pending =
            new CaptureForegroundService.HandoffData(consent.ResultCode, consent.Data, _sink, started);
        _activity.StartForegroundService(new Intent(_activity, typeof(CaptureForegroundService)));

        // Guard against the service never reaching OnStartCommand (e.g. FGS start denial).
        var completed = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        IsCapturing = completed == started.Task && started.Task.Result;
        return IsCapturing;
    }

    public void Stop()
    {
        IsCapturing = false;
        // Unconditionally, even when IsCapturing never went true: a service that starts after the
        // 5 s timeout has already consumed the handoff and captures regardless, so the stop must
        // always reach it. The service's OnDestroy stops the thread and releases the
        // record/projection; stopping a service that is not running is a no-op.
        _activity.StopService(new Intent(_activity, typeof(CaptureForegroundService)));
    }

    private void OnServiceStopped()
    {
        IsCapturing = false;
        Stopped?.Invoke();
    }

    public void Dispose()
    {
        CaptureForegroundService.Stopped -= OnServiceStopped;
        Stop();
    }
}
