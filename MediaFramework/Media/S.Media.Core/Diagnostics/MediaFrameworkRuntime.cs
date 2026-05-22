namespace S.Media.Core.Diagnostics;

/// <summary>
/// One-time process-wide framework initialization. Chain module hooks
/// (<c>.UseFFmpeg()</c>, <c>.UsePortAudio()</c>, …) then call <see cref="Shutdown"/>
/// in hosted processes or test harnesses that load many clips in one run.
/// </summary>
public static class MediaFrameworkRuntime
{
    private static readonly Lock Gate = new();
    private static MediaFrameworkRuntimeBuilder? _builder;

    /// <summary>Returns the shared init builder (idempotent, thread-safe).</summary>
    public static MediaFrameworkRuntimeBuilder Init()
    {
        lock (Gate)
            return _builder ??= new MediaFrameworkRuntimeBuilder();
    }

    /// <summary>
    /// Runs registered module shutdown hooks in reverse registration order
    /// (NDI → PortAudio → FFmpeg by default). Idempotent.
    /// </summary>
    /// <param name="gracePeriod">Reserved for future drain semantics; ignored today.</param>
    public static void Shutdown(TimeSpan? gracePeriod = null)
    {
        _ = gracePeriod;
        lock (Gate)
        {
            if (_builder is null)
                return;
            _builder.RunShutdown();
            _builder = null;
        }
    }

    /// <summary>Registers a hook invoked by <see cref="Shutdown"/> in reverse order.</summary>
    public static void RegisterShutdown(Action shutdown)
    {
        lock (Gate)
            Init().RegisterShutdownStep(shutdown);
    }
}

/// <summary>Fluent builder returned by <see cref="MediaFrameworkRuntime.Init"/>.</summary>
public sealed class MediaFrameworkRuntimeBuilder
{
    private readonly Lock _gate = new();
    private readonly List<Action> _shutdownSteps = [];

    internal void RegisterShutdownStep(Action shutdown)
    {
        lock (_gate)
            _shutdownSteps.Add(shutdown);
    }

    internal void RunShutdown()
    {
        Action[] steps;
        lock (_gate)
        {
            steps = [.. _shutdownSteps];
            _shutdownSteps.Clear();
        }

        for (var i = steps.Length - 1; i >= 0; i--)
        {
            try
            {
                steps[i]();
            }
            catch (Exception ex)
            {
                MediaDiagnostics.LogWarning("MediaFrameworkRuntime.Shutdown: module teardown failed: {0}", ex.Message);
            }
        }
    }
}
