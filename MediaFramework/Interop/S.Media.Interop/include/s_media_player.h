/*
 * s_media_player.h — MFPlayer outbound C ABI ("drive MFPlayer from any language").
 *
 * The twin of include/mfp_plugin.h: where mfp_plugin lets other languages *extend* MFPlayer, this lets them
 * *drive* it. The managed side is a NativeAOT shared library (s_media_player.so / .dll) over the new headless
 * `ShowSession`: build a media registry (FFmpeg + audio backends + …), load a `ShowDocument` (JSON), then fire
 * cues / go / seek / stop and query transport — a whole show run with no managed host.
 *
 * ============================  PHASE 7 — FIRST SLICE (core lifecycle)  ============================
 * This header is the stable, append-only contract. Conventions (kept from the V1 ABI):
 *   - C ABI only; every entry point is `extern "C"`. Nothing throws/aborts across the boundary.
 *   - Functions returning `int` return MFP_OK (0) or a negative MFP_ERR_*. A human-readable message for the last
 *     failure on the calling thread is available via mfp_last_error() (thread-local; valid until the next call).
 *   - Time is in 100-nanosecond "ticks": 10,000,000 ticks == 1 second (matches the managed clock + mfp_plugin.h).
 *   - Handles (mfp_session) are opaque. The owner frees them (mfp_session_destroy). NULL is never a valid handle.
 *   - Strings are UTF-8, NUL-terminated; the caller owns its inputs, the library owns its outputs (valid until the
 *     next call on that thread).
 *   - Call mfp_initialize() once before anything else, mfp_shutdown() once at the end.
 */
#ifndef S_MEDIA_PLAYER_H
#define S_MEDIA_PLAYER_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define S_MEDIA_PLAYER_ABI_VERSION 1

/* Status codes (int-returning functions). */
#define MFP_OK                   0
#define MFP_ERR_GENERIC         -1
#define MFP_ERR_INVALID_ARG     -2
#define MFP_ERR_INVALID_HANDLE  -3
#define MFP_ERR_LOAD_FAILED     -4   /* show JSON parse / document load failed */
#define MFP_ERR_NOT_INITIALIZED -5
#define MFP_ERR_NOT_FOUND       -6   /* unknown cue id / transport group */

/* Transport state of a group, as returned by mfp_session_state().
 * The headless runner reports IDLE / PLAYING / PAUSED: a group holding a clip is PLAYING while its clock
 * advances, otherwise PAUSED (paused / frozen / held); no clip held is IDLE. ENDED and ERROR are RESERVED
 * — the headless transport snapshot does not distinguish a played-through cue from idle (it reports IDLE
 * once the clip releases) and carries no error flag. Do not rely on ENDED/ERROR being produced. */
#define MFP_STATE_IDLE     0
#define MFP_STATE_PLAYING  1
#define MFP_STATE_PAUSED   2
#define MFP_STATE_ENDED    3   /* reserved — not currently emitted */
#define MFP_STATE_ERROR    4   /* reserved — not currently emitted */

/* An opaque show session — one running show (its registry + ShowSession + transport groups). */
typedef void* mfp_session;

/* ------------------------------------------------------------------ global lifecycle ------------ */

/* Process-wide init/teardown. mfp_initialize() is idempotent and must precede everything; returns a status. */
int  mfp_initialize(void);
void mfp_shutdown(void);

/* The ABI version this library implements (== S_MEDIA_PLAYER_ABI_VERSION at build time). */
uint32_t mfp_abi_version(void);

/* Last error message for the calling thread (UTF-8), or "" if none. Valid until the next ABI call on this thread. */
const char* mfp_last_error(void);

/* ------------------------------------------------------------------ session ---------------------- */

/*
 * Create a headless show session: builds the media registry (FFmpeg decode + the audio backend modules) and a
 * ShowSession over it. The session drives transport + composition but does NOT open an audio output device by
 * default (headless — CI-safe, no device dependency); audio-out on a real backend is a future create-with-audio
 * variant. Returns an opaque handle, or NULL on failure (see mfp_last_error). Destroy with mfp_session_destroy.
 *
 * Concurrency: mfp_session_destroy() (and mfp_shutdown()) wait for any in-flight calls on the session to return
 * before releasing it, so a destroy racing an in-progress go/seek/query will not tear state out from under it.
 */
mfp_session mfp_session_create(void);

/* Tear down a session: stops transport, releases the registry + outputs. The handle is invalid after this. */
void mfp_session_destroy(mfp_session session);

/* Load (replace) the show from a ShowDocument JSON string. MFP_ERR_LOAD_FAILED on a parse/validation error. */
int mfp_session_load_show(mfp_session session, const char* show_json);

/* ------------------------------------------------------------------ transport -------------------- */
/* `group_id` selects a transport group; NULL or "" = the default group. */

/* Advance to + fire the next cue in `group_id` (the GO button). */
int mfp_session_go(mfp_session session, const char* group_id);

/* Fire a specific cue by id (independent of GO order). MFP_ERR_NOT_FOUND for an unknown id. */
int mfp_session_fire_cue(mfp_session session, const char* cue_id);

/* Seek `group_id` to an absolute position in ticks. */
int mfp_session_seek(mfp_session session, int64_t position_ticks, const char* group_id);

/* Stop `group_id` (return to idle). */
int mfp_session_stop(mfp_session session, const char* group_id);

/* ------------------------------------------------------------------ query ------------------------ */

/* Current playhead of `group_id` in ticks, or a negative MFP_ERR_* (check mfp_last_error). */
int64_t mfp_session_position_ticks(mfp_session session, const char* group_id);

/* Total duration of the active cue in `group_id` in ticks (0 if none/unknown), or a negative MFP_ERR_*. */
int64_t mfp_session_duration_ticks(mfp_session session, const char* group_id);

/* Transport state of `group_id` (an MFP_STATE_*), or a negative MFP_ERR_*. */
int mfp_session_state(mfp_session session, const char* group_id);

/* ------------------------------------------------------------------ cues ------------------------- */

/* Number of cues in the loaded show, or a negative MFP_ERR_*. */
int mfp_session_cue_count(mfp_session session);

/* Copy the id of the cue at `index` (UTF-8, NUL-terminated) into `out` (capacity `out_capacity` bytes). Returns
 * MFP_OK, or MFP_ERR_INVALID_ARG for a bad index or a too-small buffer. */
int mfp_session_cue_id(mfp_session session, int index, char* out, int out_capacity);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* S_MEDIA_PLAYER_H */
