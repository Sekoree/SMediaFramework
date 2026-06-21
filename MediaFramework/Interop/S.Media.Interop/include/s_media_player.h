/*
 * s_media_player.h — C ABI for the MFPlayer media framework (NativeAOT shared library).
 *
 * Build the library with:
 *     dotnet publish MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj \
 *         -c Release -r <rid> -p:PublishAot=true
 * which emits s_media_player.so (Linux), s_media_player.dll (Windows) or
 * s_media_player.dylib (macOS). dlopen / LoadLibrary it from any language and bind the
 * symbols below. The .NET garbage collector lives entirely behind this boundary — it never
 * touches the host program's memory.
 *
 * Conventions:
 *   - Call mfp_initialize() once before anything else; mfp_shutdown() once at the end.
 *   - A player is an opaque handle (mfp_player). Pair every successful mfp_open_file()
 *     with exactly one mfp_close().
 *   - Functions returning int return 0 (MFP_OK) on success or a negative MFP_ERR_* code.
 *     A human-readable message for the last failure on the calling thread is available via
 *     mfp_last_error(). No function throws or aborts across the boundary.
 *   - Time is in 100-nanosecond "ticks": 10,000,000 ticks == 1 second.
 */
#ifndef S_MEDIA_PLAYER_H
#define S_MEDIA_PLAYER_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Status codes. */
#define MFP_OK                  0
#define MFP_ERR_GENERIC        -1
#define MFP_ERR_INVALID_ARG    -2
#define MFP_ERR_INVALID_HANDLE -3
#define MFP_ERR_OPEN_FAILED    -4
#define MFP_ERR_NOT_INITIALIZED -5

/* Playback state, as returned by mfp_get_state(). */
#define MFP_STATE_IDLE     0
#define MFP_STATE_PLAYING  1
#define MFP_STATE_PAUSED   2
#define MFP_STATE_ENDED    3
#define MFP_STATE_ERROR    4

/* audio_device_index sentinels for mfp_open_file(). Any other value is a global PortAudio device index. */
#define MFP_AUDIO_DEFAULT  -1   /* system default output device */
#define MFP_AUDIO_NONE     -2   /* do not wire audio */

/* Opaque player handle. */
typedef void* mfp_player;

/* Lifecycle. */
int  mfp_initialize(void);
void mfp_shutdown(void);

/* Open a local file. with_video_window != 0 opens an SDL window when the file has video.
 * On success writes the handle to *out_player and returns MFP_OK. */
int  mfp_open_file(const char* utf8_path, int with_video_window, int audio_device_index, mfp_player* out_player);
void mfp_close(mfp_player player);

/* Transport. */
int  mfp_play(mfp_player player);
int  mfp_pause(mfp_player player);
int  mfp_seek(mfp_player player, int64_t position_ticks);

/* Queries. Return -1 on an invalid handle. */
int64_t mfp_get_position_ticks(mfp_player player);
int64_t mfp_get_duration_ticks(mfp_player player);
int     mfp_get_state(mfp_player player);   /* one of MFP_STATE_* */
int     mfp_is_ended(mfp_player player);    /* 1 = ended, 0 = not, -1 = bad handle */

/* Copies the calling thread's last error (UTF-8, NUL-terminated) into buffer and returns the
 * byte length needed (excluding NUL). Pass buffer=NULL / buffer_len=0 to query the size first. */
int  mfp_last_error(char* buffer, int buffer_len);

#ifdef __cplusplus
}
#endif

#endif /* S_MEDIA_PLAYER_H */
