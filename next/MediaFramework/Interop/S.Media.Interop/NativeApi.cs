using System.Diagnostics.CodeAnalysis;
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

    // Session handles are opaque, monotonically-increasing tokens into a synchronized table — NEVER raw
    // GCHandle pointers handed back by (untrusted) C callers. A stale, random, or double-freed token simply
    // isn't in the table, so it is rejected without ever dereferencing caller-supplied memory (NXT-08). Ids
    // are never reused (64-bit monotonic), so there is no ABA window that a separate generation would guard.
    private static readonly Lock s_handleGate = new();
    private static readonly Dictionary<nint, SessionBox> s_handles = new();
    private static nint s_nextHandle; // 0 is reserved as the null/invalid handle; first issued is 1

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

    /// <summary>Destroys every live session deterministically, then closes the runtime. The old behaviour only
    /// flipped a flag — after which destruction was refused, so live sessions and their native resources leaked
    /// (NXT-08). No-throw across the boundary.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_shutdown")]
    private static void Shutdown()
    {
        try
        {
            SessionBox[] live;
            lock (s_handleGate)
            {
                live = s_handles.Values.ToArray();
                s_handles.Clear();
            }
            foreach (var box in live)
            {
                try { box.Session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* teardown best-effort */ }
            }
            s_initialized = false;
            FreeLastErrorNative();
        }
        catch { /* no-throw boundary across the ABI */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_abi_version")]
    private static uint AbiVersion() => 1u;

    /// <summary>The last error string for the calling thread, or "" if none. The returned pointer is owned by
    /// the library and is valid <strong>only until the next <c>mfp_*</c> call on this thread</strong> (it is
    /// freed and re-issued on each call) — C callers must copy it immediately (NXT-17). Never throws.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_last_error")]
    private static byte* LastError()
    {
        try
        {
            FreeLastErrorNative();
            s_lastErrorNative = Marshal.StringToCoTaskMemUTF8(s_lastError ?? string.Empty);
            return (byte*)s_lastErrorNative;
        }
        catch
        {
            return null;
        }
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
            return RegisterSession(box);
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
        try
        {
            if (!TryRemove(session, out var box))
                return; // unknown / already-destroyed handle → idempotent no-op (no throw, no double-free)
            try { box.Session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* teardown is best-effort */ }
        }
        catch { /* no-throw boundary across the ABI */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_load_show")]
    private static int SessionLoadShow(nint session, byte* showJson)
    {
        if (!TryResolve(session, out var box))
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
        if (!TryResolve(session, out var box))
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
        if (!TryResolve(session, out var box))
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
        if (!TryResolve(session, out var box))
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
        if (!TryResolve(session, out var box))
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
        if (!TryResolve(session, out var box))
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

    private static nint RegisterSession(SessionBox box)
    {
        lock (s_handleGate)
        {
            var handle = ++s_nextHandle; // monotonic, never reused → a freed token can never alias a live one
            s_handles[handle] = box;
            return handle;
        }
    }

    /// <summary>Resolves a caller-supplied handle to its session by table lookup. Never dereferences the token
    /// as a pointer, so a stale/garbage/double-freed handle is rejected safely instead of throwing across the
    /// unmanaged boundary (NXT-08).</summary>
    private static bool TryResolve(nint session, [NotNullWhen(true)] out SessionBox? box)
    {
        box = null;
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
        lock (s_handleGate)
            if (s_handles.TryGetValue(session, out box))
                return true;
        SetLastError("invalid or stale session handle.");
        return false;
    }

    /// <summary>Atomically removes a handle from the table (for destruction). Returns false for an unknown or
    /// already-removed handle, so double-destroy is a safe no-op. Does not gate on initialization so a session
    /// can always be torn down.</summary>
    private static bool TryRemove(nint session, [NotNullWhen(true)] out SessionBox? box)
    {
        box = null;
        if (session == 0)
            return false;
        lock (s_handleGate)
            return s_handles.Remove(session, out box);
    }

    private static string Utf8(byte* p) => p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;
    private static void SetLastError(string message) => s_lastError = message;
    private static void ClearLastError() => s_lastError = null;

    private static void FreeLastErrorNative()
    {
        if (s_lastErrorNative != 0)
        {
            Marshal.FreeCoTaskMem(s_lastErrorNative);
            s_lastErrorNative = 0;
        }
    }
}
