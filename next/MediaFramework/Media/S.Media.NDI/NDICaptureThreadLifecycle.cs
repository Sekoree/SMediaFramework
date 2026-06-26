using Microsoft.Extensions.Logging;
using S.Media.Core.Diagnostics;
using S.Media.Core.Threading;

namespace S.Media.NDI;

internal static class NDICaptureThreadLifecycle
{
    public static bool StopAndDispose(
        string ownerName,
        Thread captureThread,
        CancellationTokenSource cancellation,
        TimeSpan joinTimeout,
        Action notifyCaptureStopped,
        Action disposeNativeResources,
        Action releaseQueuedBuffers,
        Action wakeReaders,
        Action<Exception> markCaptureStuck,
        ILogger? logger = null)
    {
        MediaDiagnostics.SwallowDisposeErrors(cancellation.Cancel, $"{ownerName}.Dispose: Cancel");
        MediaDiagnostics.SwallowDisposeErrors(
            () => CooperativePlaybackJoin.JoinThread(captureThread, joinTimeout),
            $"{ownerName}.Dispose: JoinThread");

        var captureStopped = !captureThread.IsAlive;
        if (captureStopped)
        {
            MediaDiagnostics.SwallowDisposeErrors(notifyCaptureStopped, $"{ownerName}.Dispose: NotifyCaptureStopped");
            MediaDiagnostics.SwallowDisposeErrors(cancellation.Dispose, $"{ownerName}.Dispose: CancellationTokenSource.Dispose");
        }
        else
        {
            var ex = new TimeoutException($"{ownerName} capture thread did not exit during Dispose; native receiver/runtime were intentionally leaked.");
            markCaptureStuck(ex);
            NativeResourceHealth.ReportStuck(
                ownerName,
                "NDI capture thread",
                captureThread.Name,
                joinTimeout,
                ex);
            if (logger is { } l)
                l.LogError(ex, "{Owner}.Dispose: capture thread still alive after join cap; leaking native receiver/runtime and CTS to avoid use-after-dispose.", ownerName);
            else
                MediaDiagnostics.LogError(ex, $"{ownerName}.Dispose: capture thread still alive after join cap; leaking native receiver/runtime and CTS to avoid use-after-dispose.");
        }

        releaseQueuedBuffers();
        wakeReaders();

        if (captureStopped)
            MediaDiagnostics.SwallowDisposeErrors(disposeNativeResources, $"{ownerName}.Dispose: native resources");

        return captureStopped;
    }
}
