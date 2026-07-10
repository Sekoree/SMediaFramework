namespace HaPlay.ViewModels;

public enum ToastSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// App-wide toast bus (UI rewrite P1, plan §1). Any component posts; <see cref="MainViewModel"/>
/// owns the visible queue and renders it as a top-right overlay in <c>MainView</c> - toasts never
/// participate in layout, so transient errors can't move controls under the operator's finger
/// (the misclick problem the rewrite bans app-wide).
/// </summary>
/// <remarks>
/// Thread-safe: <see cref="Post"/> may be called from any thread; the subscriber marshals to the
/// UI thread. When nothing is subscribed (headless tests, very early startup) posts are dropped -
/// persistent state belongs on a status line, not in a toast.
/// </remarks>
public static class ToastCenter
{
    /// <summary>
    /// The single receiver (the app shell's <see cref="MainViewModel"/>). Assignment replaces any
    /// previous sink - last-wins matches the one-shell reality and keeps test-created shells from
    /// stacking up behind a static event.
    /// </summary>
    public static Action<ToastSeverity, string>? Sink { get; set; }

    public static void Post(ToastSeverity severity, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        Sink?.Invoke(severity, message);
    }

    public static void Info(string message) => Post(ToastSeverity.Info, message);
    public static void Warn(string message) => Post(ToastSeverity.Warning, message);
    public static void Error(string message) => Post(ToastSeverity.Error, message);
}
