using Microsoft.Extensions.Logging;

namespace S.Media.Audio.PortAudio;

/// <summary>
/// Reference-counted lifetime for the PortAudio library. Each
/// <see cref="PortAudioOutput"/> / <see cref="PortAudioInput"/> calls
/// <see cref="Acquire"/> in its constructor and <see cref="Release"/> on
/// dispose; the underlying <c>Pa_Terminate</c> only runs when the last
/// holder lets go.
/// </summary>
public static class PortAudioRuntime
{
    private static readonly ILogger Trace = MediaDiagnostics.CreateLogger("S.Media.PortAudio.PortAudioRuntime");

    private static readonly Lock Gate = new();
    private static int _refCount;

    /// <summary>Initialize PortAudio (idempotent, ref-counted).</summary>
    public static void Acquire()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioRuntime.Acquire", slowWarningMs: 500);
        lock (Gate)
        {
            if (_refCount == 0)
            {
                PortAudioException.ThrowIfError(Native.Pa_Initialize(), nameof(Native.Pa_Initialize));
                Trace.LogDebug("Acquire: Pa_Initialize completed");
            }
            _refCount++;
            timing?.SetOutcome($"refCount={_refCount}");
        }
    }

    /// <summary>Release one ref; tear down PortAudio when the count hits zero.</summary>
    public static void Release()
    {
        using var timing = MediaDiagnostics.BeginTimedOperation(Trace, "PortAudioRuntime.Release", slowWarningMs: 500);
        lock (Gate)
        {
            if (_refCount == 0)
            {
                timing?.SetOutcome("refCount=0");
                return;
            }
            _refCount--;
            if (_refCount == 0)
            {
                Native.Pa_Terminate();
                Trace.LogDebug("Release: Pa_Terminate completed");
            }
            timing?.SetOutcome($"refCount={_refCount}");
        }
    }

    public static int DefaultOutputDevice
    {
        get
        {
            Acquire();
            try { return Native.Pa_GetDefaultOutputDevice(); }
            finally { Release(); }
        }
    }

    public static int DefaultInputDevice
    {
        get
        {
            Acquire();
            try { return Native.Pa_GetDefaultInputDevice(); }
            finally { Release(); }
        }
    }

    public static string? VersionText
    {
        get
        {
            Acquire();
            try { return Native.Pa_GetVersionText(); }
            finally { Release(); }
        }
    }
}
