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
/// The outbound C ABI (<c>s_media_player.h</c>) - `[UnmanagedCallersOnly]` exports over the headless
/// <see cref="ShowSession"/>, AOT-published as <c>s_media_player.so</c>/<c>.dll</c>. Each export is sync over the
/// session's async dispatcher (block on the returned task - the dispatcher runs on its own thread, so no deadlock).
/// Nothing throws across the boundary: failures set a thread-local last error (see <see cref="mfp_last_error"/>)
/// and return a negative status / null handle. Handles are opaque monotonic tokens resolved through a guarded table.
/// </summary>
internal static unsafe class NativeApi
{
    private const int MfpOk = 0;
    private const int MfpErrGeneric = -1;
    private const int MfpErrInvalidArg = -2;
    private const int MfpErrInvalidHandle = -3;
    private const int MfpErrLoadFailed = -4;
    private const int MfpErrNotInitialized = -5;
    private const int MfpErrNotFound = -6; // unknown cue id / transport group (matches s_media_player.h)

    private const int MfpStateIdle = 0;
    private const int MfpStatePlaying = 1;
    private const int MfpStatePaused = 2;
    private const int MfpStateEnded = 3;
    private const int MfpStateError = 4;

    /// <summary>Bound on how long <c>mfp_session_destroy</c>/<c>mfp_shutdown</c> wait for in-flight calls on a
    /// session to drain before returning. A timed-out teardown is completed in the background after the final
    /// lease leaves, rather than disposing under a live call - ABI-02.</summary>
    private static readonly TimeSpan CallDrainTimeout = TimeSpan.FromSeconds(30);

    private static volatile bool s_initialized;

    [ThreadStatic] private static string? s_lastError;
    [ThreadStatic] private static nint s_lastErrorNative;

    // Session handles are opaque, monotonically-increasing tokens into a synchronized table - NEVER raw
    // GCHandle pointers handed back by (untrusted) C callers. A stale, random, or double-freed token simply
    // isn't in the table, so it is rejected without ever dereferencing caller-supplied memory (NXT-08). Ids
    // are never reused (64-bit monotonic), so there is no ABA window that a separate generation would guard.
    private static readonly Lock s_handleGate = new();
    private static readonly Dictionary<nint, SessionBox> s_handles = new();
    private static nint s_nextHandle; // 0 is reserved as the null/invalid handle; first issued is 1

    private sealed class SessionBox
    {
        public required ShowSession Session;

        /// <summary>The per-session owning host (NXT-05). Disposing it releases the session's module native-runtime
        /// holds (PortAudio <c>Pa_Terminate</c>/NDI); create/destroy churn no longer ratchets those refs up forever.</summary>
        public required MediaHost Host;

        // ABI-02: per-session call leasing. A call resolves the handle and increments ActiveCalls under
        // s_handleGate; destroy/shutdown flip Closing (rejecting new leases), remove the handle, then wait on
        // Idle (set when ActiveCalls hits 0) so the session is never disposed out from under a live call.
        public int ActiveCalls;     // guarded by s_handleGate
        public bool Closing;        // guarded by s_handleGate
        public readonly ManualResetEventSlim Idle = new(initialState: true);
        public int DisposeStarted;
    }

    // ----------------------------------------------------------------- global lifecycle ------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_initialize")]
    private static int Initialize()
    {
        lock (s_handleGate)
            s_initialized = true; // FFmpeg/PortAudio init lazily on first use; this just gates the handle calls.
        ClearLastError();
        return MfpOk;
    }

    /// <summary>Destroys every live session deterministically, then closes the runtime. The old behaviour only
    /// flipped a flag - after which destruction was refused, so live sessions and their native resources leaked
    /// (NXT-08). No-throw across the boundary.</summary>
    [UnmanagedCallersOnly(EntryPoint = "mfp_shutdown")]
    private static void Shutdown()
    {
        try
        {
            SessionBox[] live;
            lock (s_handleGate)
            {
                // Close this runtime generation before taking the session snapshot. SessionCreate re-checks
                // this flag while registering, so a create that began before shutdown cannot publish a new,
                // untracked handle after the snapshot (ABI-02).
                s_initialized = false;
                live = s_handles.Values.ToArray();
                foreach (var b in live)
                    b.Closing = true; // reject new leases before draining (ABI-02)
                s_handles.Clear();
            }

            // Defer teardown until each session's in-flight calls drain (or the bound expires).
            foreach (var box in live)
                DrainAndDispose(box);
            FreeLastErrorNative();
        }
        catch { /* no-throw boundary across the ABI */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_abi_version")]
    private static uint AbiVersion() => 1u;

    /// <summary>The last error string for the calling thread, or "" if none. The returned pointer is owned by
    /// the library and is valid <strong>only until the next <c>mfp_*</c> call on this thread</strong> (it is
    /// freed and re-issued on each call) - C callers must copy it immediately (NXT-17). Never throws.</summary>
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
            var host = MediaHost.Build(b => b.Use(new FFmpegModule()).Use(new PortAudioModule()));

            // Headless by default - a show runner that drives transport + composition without owning an audio device
            // (CI-safe, no flaky-ALSA/device dependency). Audio-out on a real backend is a later create-with-audio option.
            var session = new ShowSession(host.Registry, audioBackend: null);

            var box = new SessionBox { Session = session, Host = host };
            if (!TryRegisterSession(box, out var handle))
            {
                // Shutdown won while the relatively expensive host/session construction was in progress.
                // The box was never published, so tear it down locally rather than leaking it.
                DisposeBox(box);
                SetLastError("runtime shut down while mfp_session_create was in progress.");
                return 0;
            }

            ClearLastError();
            return handle;
        }
        catch (Exception ex)
        {
            SetLastError($"mfp_session_create failed: {ex}");
            return 0;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_destroy")]
    private static void SessionDestroy(nint session)
    {
        // Marks the session closing, waits for in-flight calls to drain, then disposes (ABI-02). An unknown
        // or already-destroyed handle is an idempotent no-op (no throw, no double-free).
        try { CloseAndDispose(session); }
        catch { /* no-throw boundary across the ABI */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_load_show")]
    private static int SessionLoadShow(nint session, byte* showJson)
    {
        if (!TryResolve(session, out var box, out var resolveError))
            return resolveError;
        try
        {
            var json = Utf8(showJson);
            box.Session.LoadDocument(ShowDocument.FromJson(json));
            ClearLastError();
            return MfpOk;
        }
        catch (Exception ex)
        {
            SetLastError($"mfp_session_load_show failed: {ex}");
            return MfpErrLoadFailed;
        }
        finally
        {
            Release(box);
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
        if (!TryResolve(session, out var box, out var resolveError))
            return resolveError;
        try
        {
            var id = Utf8(cueId);
            if (string.IsNullOrEmpty(id))
            {
                SetLastError("cue id must not be null or empty.");
                return MfpErrInvalidArg;
            }

            // ABI-01: an unknown cue id is MFP_ERR_NOT_FOUND (the header's documented code), not a generic error.
            var cues = box.Session.GetCueDefinitionsAsync().GetAwaiter().GetResult();
            if (!cues.Any(c => string.Equals(c.Id, id, StringComparison.Ordinal)))
            {
                SetLastError($"cue '{id}' not found.");
                return MfpErrNotFound;
            }

            box.Session.FireCueAsync(id).GetAwaiter().GetResult();
            ClearLastError();
            return MfpOk;
        }
        catch (Exception ex)
        {
            SetLastError(ex.ToString());
            return MfpErrGeneric;
        }
        finally
        {
            Release(box);
        }
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
        Snapshot(session, groupId, static s => s.ClipPosition.Ticks);

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_duration_ticks")]
    private static long SessionDurationTicks(nint session, byte* groupId) =>
        Snapshot(session, groupId, static s => s.ClipDuration.Ticks);

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_state")]
    private static int SessionState(nint session, byte* groupId) =>
        (int)Snapshot(session, groupId, static s => (long)MapState(s));

    /// <summary>ABI-01: derive the transport state from the snapshot. A group holding a clip is PLAYING when
    /// its clock advances, else PAUSED (paused / frozen / held). No clip held ⇒ IDLE. ENDED and ERROR are
    /// reserved - the headless snapshot does not distinguish a played-through cue from idle, and carries no
    /// error flag (see s_media_player.h).</summary>
    private static int MapState(TransportSnapshot s) =>
        !s.IsActive ? MfpStateIdle : s.IsRunning ? MfpStatePlaying : MfpStatePaused;

    // ----------------------------------------------------------------- cues ------------------------

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_cue_count")]
    private static int SessionCueCount(nint session)
    {
        if (!TryResolve(session, out var box, out var resolveError))
            return resolveError;
        try
        {
            var count = box.Session.GetCueDefinitionsAsync().GetAwaiter().GetResult().Count;
            ClearLastError();
            return count;
        }
        catch (Exception ex)
        {
            SetLastError(ex.ToString());
            return MfpErrGeneric;
        }
        finally
        {
            Release(box);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "mfp_session_cue_id")]
    private static int SessionCueId(nint session, int index, byte* outBuf, int outCapacity)
    {
        if (!TryResolve(session, out var box, out var resolveError))
            return resolveError;
        try
        {
            if (outBuf == null || outCapacity <= 0)
            {
                SetLastError("output buffer must be non-null with a positive capacity.");
                return MfpErrInvalidArg;
            }

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
            SetLastError(ex.ToString());
            return MfpErrGeneric;
        }
        finally
        {
            Release(box);
        }
    }

    // ----------------------------------------------------------------- helpers ---------------------

    private static int Run(nint session, byte* groupId, Action<ShowSession, string?> action)
    {
        if (!TryResolve(session, out var box, out var resolveError))
            return resolveError;
        try
        {
            var g = Utf8(groupId);
            return Run(box, () => action(box.Session, string.IsNullOrEmpty(g) ? null : g));
        }
        finally
        {
            Release(box);
        }
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
            SetLastError(ex.ToString());
            return MfpErrGeneric;
        }
    }

    private static long Snapshot(nint session, byte* groupId, Func<TransportSnapshot, long> pick)
    {
        if (!TryResolve(session, out var box, out var resolveError))
            return resolveError;
        try
        {
            var g = Utf8(groupId);
            var snaps = box.Session.SnapshotAsync().GetAwaiter().GetResult();
            TransportSnapshot? snap;
            if (string.IsNullOrEmpty(g))
            {
                snap = snaps.Count > 0 ? snaps[0] : null; // default group: first, or "nothing loaded" → 0
            }
            else
            {
                snap = snaps.FirstOrDefault(x => x.GroupId == g);
                if (snap is null)
                {
                    SetLastError($"transport group '{g}' not found."); // ABI-01
                    return MfpErrNotFound;
                }
            }

            ClearLastError();
            return snap is null ? 0 : pick(snap);
        }
        catch (Exception ex)
        {
            SetLastError(ex.ToString());
            return MfpErrGeneric;
        }
        finally
        {
            Release(box);
        }
    }

    private static bool TryRegisterSession(SessionBox box, out nint handle)
    {
        lock (s_handleGate)
        {
            if (!s_initialized)
            {
                handle = 0;
                return false;
            }

            handle = ++s_nextHandle; // monotonic, never reused → a freed token can never alias a live one
            s_handles[handle] = box;
            return true;
        }
    }

    /// <summary>Resolves a caller-supplied handle to its session by table lookup <strong>and acquires a call
    /// lease</strong> (ABI-02): the caller MUST pair a <c>true</c> result with <see cref="Release"/> in a
    /// finally, so a concurrent destroy/shutdown waits for the call to drain instead of disposing under it.
    /// Never dereferences the token as a pointer, so a stale/garbage/double-freed/closing handle is rejected
    /// safely instead of throwing across the unmanaged boundary (NXT-08).</summary>
    private static bool TryResolve(
        nint session,
        [NotNullWhen(true)] out SessionBox? box,
        out int failureCode)
    {
        box = null;
        lock (s_handleGate)
        {
            if (!s_initialized)
            {
                failureCode = MfpErrNotInitialized;
                SetLastError("mfp_initialize() has not been called, or mfp_shutdown() is in progress.");
                return false;
            }
            if (session == 0)
            {
                failureCode = MfpErrInvalidHandle;
                SetLastError("null session handle.");
                return false;
            }
            if (s_handles.TryGetValue(session, out box) && !box.Closing)
            {
                if (box.ActiveCalls++ == 0)
                    box.Idle.Reset();
                failureCode = MfpOk;
                return true;
            }

            box = null;
        }

        SetLastError("invalid, stale, or closing session handle.");
        failureCode = MfpErrInvalidHandle;
        return false;
    }

    /// <summary>Releases a call lease taken by <see cref="TryResolve"/> (ABI-02).</summary>
    private static void Release(SessionBox box)
    {
        lock (s_handleGate)
        {
            if (--box.ActiveCalls <= 0)
            {
                box.ActiveCalls = 0;
                box.Idle.Set();
            }
        }
    }

    /// <summary>Marks a resolved box closing + removes it from the table, waits for in-flight calls to drain
    /// (bounded), then disposes it. On drain timeout teardown continues in the background after the last call
    /// leaves - a wedged call must not cause use-after-dispose across the ABI (ABI-02).</summary>
    private static void CloseAndDispose(nint session)
    {
        SessionBox? box;
        lock (s_handleGate)
        {
            if (!s_handles.TryGetValue(session, out box))
                return; // unknown / already destroyed → idempotent no-op
            box.Closing = true;      // reject new leases
            s_handles.Remove(session); // future TryResolve can't find it
        }

        DrainAndDispose(box);
    }

    private static void DrainAndDispose(SessionBox box)
    {
        if (!box.Idle.Wait(CallDrainTimeout))
        {
            // Return to the native caller at the documented bound, but retain the box until the final lease
            // exits and then finish teardown. This avoids both use-after-dispose and a permanent native leak.
            SetLastError("session had in-flight calls that did not drain within the timeout; cleanup is deferred.");
            _ = Task.Run(() =>
            {
                box.Idle.Wait();
                DisposeBox(box);
            });
            return;
        }

        DisposeBox(box);
    }

    private static void DisposeBox(SessionBox box)
    {
        if (Interlocked.Exchange(ref box.DisposeStarted, 1) != 0)
            return;
        try { box.Session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch { /* teardown best-effort */ }
        try { box.Host.Dispose(); } // release the session's PortAudio/NDI runtime holds (NXT-05)
        catch { /* teardown best-effort */ }
        try { box.Idle.Dispose(); }
        catch { /* teardown best-effort */ }
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
