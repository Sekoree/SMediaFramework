using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using S.Media.Audio.PortAudio;
using S.Media.Core.Audio;
using S.Media.Core.Registry;
using S.Media.Decode.FFmpeg;
using S.Media.Session;

namespace S.Media.Interop;

/// <summary>
/// The outbound C ABI (<c>s_media_player.h</c>) — `[UnmanagedCallersOnly]` exports over the headless
/// <see cref="ShowSession"/>, AOT-published as <c>s_media_player.so</c>/<c>.dll</c>. Each export is sync over the
/// session's async dispatcher (block on the returned task — the dispatcher runs on its own thread, so no deadlock).
/// Nothing throws across the boundary: failures set a thread-local last error (see <see cref="mfp_last_error"/>)
/// and return a negative status / null handle. Handles are <see cref="GCHandle"/>s to a <see cref="SessionBox"/>.
/// </summary>
internal static unsafe class NativeApi
{
    private const int MfpOk = 0;
    private const int MfpErrGeneric = -1;
    private const int MfpErrInvalidArg = -2;
    private const int MfpErrInvalidHandle = -3;
    private const int MfpErrLoadFailed = -4;
    private const int MfpErrNotInitialized = -5;

    private const int MfpStateIdle = 0;
    private const int MfpStatePlaying = 1;

    private static volatile bool s_initialized;

    [ThreadStatic] private static string? s_lastError;
    [ThreadStatic] private static nint s_lastErrorNative;

    private sealed class SessionBox
    {
        public required ShowSession Session;
        public required IMediaRegistry Registry;
    }

    // ----------------------------------------------------------------- global lifecycle ------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_initialize")]
    private static int Initialize()
    {
        s_initialized = true; // FFmpeg/PortAudio init lazily on first use; this just gates the handle calls.
        ClearLastError();
        return MfpOk;
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_shutdown")]
    private static void Shutdown() => s_initialized = false;

    [UnmanagedCallersOnly(EntryPoint = "mfp_abi_version")]
    private static uint AbiVersion() => 1u;

    [UnmanagedCallersOnly(EntryPoint = "mfp_last_error")]
    private static byte* LastError()
    {
        if (s_lastErrorNative != 0)
            Marshal.FreeCoTaskMem(s_lastErrorNative);
        s_lastErrorNative = Marshal.StringToCoTaskMemUTF8(s_lastError ?? string.Empty);
        return (byte*)s_lastErrorNative;
    }

    // ----------------------------------------------------------------- session ---------------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_create")]
    private static nint SessionCreate()
    {
        if (!s_initialized)
        {
            SetLastError("mfp_initialize() has not been called.");
            return 0;
        }

        try
        {
            var registry = MediaRegistry.Build(b => b.Use(new FFmpegModule()).Use(new PortAudioModule()));

            // Headless by default — a show runner that drives transport + composition without owning an audio device
            // (CI-safe, no flaky-ALSA/device dependency). Audio-out on a real backend is a later create-with-audio option.
            var session = new ShowSession(registry, audioBackend: null);

            var box = new SessionBox { Session = session, Registry = registry };
            ClearLastError();
            return GCHandle.ToIntPtr(GCHandle.Alloc(box));
        }
        catch (Exception ex)
        {
            SetLastError($"mfp_session_create failed: {ex.Message}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_destroy")]
    private static void SessionDestroy(nint session)
    {
        if (!TryBox(session, out var handle, out var box))
            return;
        try
        {
            box.Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // teardown is best-effort
        }
        finally
        {
            handle.Free();
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_load_show")]
    private static int SessionLoadShow(nint session, byte* showJson)
    {
        if (!TryBox(session, out _, out var box))
            return MfpErrInvalidHandle;
        try
        {
            var json = Utf8(showJson);
            box.Session.LoadDocument(ShowDocument.FromJson(json));
            ClearLastError();
            return MfpOk;
        }
        catch (Exception ex)
        {
            SetLastError($"mfp_session_load_show failed: {ex.Message}");
            return MfpErrLoadFailed;
        }
    }

    // ----------------------------------------------------------------- transport -------------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_go")]
    private static int SessionGo(nint session, byte* groupId) =>
        Run(session, groupId, static (s, g) =>
            (g is null ? s.GoAsync() : s.GoAsync(g)).GetAwaiter().GetResult());

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_fire_cue")]
    private static int SessionFireCue(nint session, byte* cueId)
    {
        if (!TryBox(session, out _, out var box))
            return MfpErrInvalidHandle;
        var id = Utf8(cueId);
        if (string.IsNullOrEmpty(id))
            return MfpErrInvalidArg;
        return Run(box, () => box.Session.FireCueAsync(id).GetAwaiter().GetResult());
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_seek")]
    private static int SessionSeek(nint session, long positionTicks, byte* groupId) =>
        Run(session, groupId, (s, g) =>
            (g is null ? s.SeekAsync(TimeSpan.FromTicks(positionTicks)) : s.SeekAsync(TimeSpan.FromTicks(positionTicks), g))
            .GetAwaiter().GetResult());

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_stop")]
    private static int SessionStop(nint session, byte* groupId) =>
        Run(session, groupId, static (s, g) =>
            (g is null ? s.StopAsync() : s.StopAsync(g)).GetAwaiter().GetResult());

    // ----------------------------------------------------------------- query -----------------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_position_ticks")]
    private static long SessionPositionTicks(nint session, byte* groupId) =>
        Snapshot(session, groupId, static s => s.ClipPosition.Ticks, MfpErrInvalidHandle);

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_duration_ticks")]
    private static long SessionDurationTicks(nint session, byte* groupId) =>
        Snapshot(session, groupId, static s => s.ClipDuration.Ticks, MfpErrInvalidHandle);

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_state")]
    private static int SessionState(nint session, byte* groupId) =>
        (int)Snapshot(session, groupId, static s => (long)(s.IsRunning ? MfpStatePlaying : MfpStateIdle), MfpErrInvalidHandle);

    // ----------------------------------------------------------------- cues ------------------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_cue_count")]
    private static int SessionCueCount(nint session)
    {
        if (!TryBox(session, out _, out var box))
            return MfpErrInvalidHandle;
        try
        {
            var count = box.Session.GetCueDefinitionsAsync().GetAwaiter().GetResult().Count;
            ClearLastError();
            return count;
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            return MfpErrGeneric;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_cue_id")]
    private static int SessionCueId(nint session, int index, byte* outBuf, int outCapacity)
    {
        if (!TryBox(session, out _, out var box))
            return MfpErrInvalidHandle;
        if (outBuf == null || outCapacity <= 0)
            return MfpErrInvalidArg;
        try
        {
            var cues = box.Session.GetCueDefinitionsAsync().GetAwaiter().GetResult();
            if (index < 0 || index >= cues.Count)
            {
                SetLastError($"cue index {index} out of range [0,{cues.Count}).");
                return MfpErrInvalidArg;
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(cues[index].Id);
            if (bytes.Length + 1 > outCapacity)
            {
                SetLastError($"buffer too small for cue id ({bytes.Length + 1} > {outCapacity}).");
                return MfpErrInvalidArg;
            }
            var dst = new Span<byte>(outBuf, outCapacity);
            bytes.CopyTo(dst);
            dst[bytes.Length] = 0; // NUL-terminate
            ClearLastError();
            return MfpOk;
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            return MfpErrGeneric;
        }
    }

    // ----------------------------------------------------------------- helpers ---------------------

    private static int Run(nint session, byte* groupId, Action<ShowSession, string?> action)
    {
        if (!TryBox(session, out _, out var box))
            return MfpErrInvalidHandle;
        var g = Utf8(groupId);
        return Run(box, () => action(box.Session, string.IsNullOrEmpty(g) ? null : g));
    }

    private static int Run(SessionBox box, Action body)
    {
        try
        {
            body();
            ClearLastError();
            return MfpOk;
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            return MfpErrGeneric;
        }
    }

    private static long Snapshot(nint session, byte* groupId, Func<TransportSnapshot, long> pick, long onBadHandle)
    {
        if (!TryBox(session, out _, out var box))
            return onBadHandle;
        try
        {
            var g = Utf8(groupId);
            var snaps = box.Session.SnapshotAsync().GetAwaiter().GetResult();
            var snap = string.IsNullOrEmpty(g)
                ? (snaps.Count > 0 ? snaps[0] : null)
                : snaps.FirstOrDefault(x => x.GroupId == g);
            ClearLastError();
            return snap is null ? 0 : pick(snap);
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            return MfpErrGeneric;
        }
    }

    private static bool TryBox(nint session, out GCHandle handle, out SessionBox box)
    {
        handle = default;
        box = null!;
        if (!s_initialized)
        {
            SetLastError("mfp_initialize() has not been called.");
            return false;
        }
        if (session == 0)
        {
            SetLastError("null session handle.");
            return false;
        }
        handle = GCHandle.FromIntPtr(session);
        if (handle.Target is not SessionBox b)
        {
            SetLastError("invalid session handle.");
            return false;
        }
        box = b;
        return true;
    }

    private static string Utf8(byte* p) => p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;
    private static void SetLastError(string message) => s_lastError = message;
    private static void ClearLastError() => s_lastError = null;
}
