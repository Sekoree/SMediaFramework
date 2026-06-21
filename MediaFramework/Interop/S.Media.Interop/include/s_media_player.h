/*
 * s_media_player.h — C ABI for the MFPlayer media framework (NativeAOT shared library).
 *
 * Build:
 *     dotnet publish MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj \
 *         -c Release -r <rid> -p:PublishAot=true
 * emits s_media_player.so (Linux) / s_media_player.dll (Windows) / s_media_player.dylib (macOS).
 * dlopen / LoadLibrary it from any language and bind the symbols below. The .NET garbage collector
 * lives entirely behind this boundary — it never touches the host program's memory.
 *
 * Two ways to use it:
 *   1. Batteries-included: mfp_open_file() opens a file AND wires a default audio device + optional
 *      SDL window in one call. Good for a quick player.
 *   2. Graph (build-your-own, like HaPlay): mfp_player_open_*() opens with NO outputs; reach the
 *      player's routers (mfp_player_video_router / _audio_router), create outputs with the factory
 *      functions, attach them, and add routes. Compose any audio/video topology you like.
 *
 * Conventions:
 *   - Call mfp_initialize() once before anything else; mfp_shutdown() once at the end.
 *   - Handles (mfp_player, mfp_video_router, mfp_audio_router, mfp_output) are opaque pointers.
 *   - Functions returning int return MFP_OK (0) or a negative MFP_ERR_*; a human-readable message for
 *     the last failure on the calling thread is available via mfp_last_error(). Nothing throws/aborts
 *     across the boundary.
 *   - Time is in 100-nanosecond "ticks": 10,000,000 ticks == 1 second.
 *   - Ownership: a player owns its decoder + routers (freed by mfp_close). Outputs you create with a
 *     factory you OWN: attach them (the router does not take ownership), and after closing the player /
 *     removing them, free them with mfp_output_destroy. Router handles are borrowed (freed with the player).
 */
#ifndef S_MEDIA_PLAYER_H
#define S_MEDIA_PLAYER_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Status codes. */
#define MFP_OK                   0
#define MFP_ERR_GENERIC         -1
#define MFP_ERR_INVALID_ARG     -2
#define MFP_ERR_INVALID_HANDLE  -3
#define MFP_ERR_OPEN_FAILED     -4
#define MFP_ERR_NOT_INITIALIZED -5

/* Playback state, as returned by mfp_get_state(). */
#define MFP_STATE_IDLE     0
#define MFP_STATE_PLAYING  1
#define MFP_STATE_PAUSED   2
#define MFP_STATE_ENDED    3
#define MFP_STATE_ERROR    4

/* Event types delivered to an mfp_event_callback. */
#define MFP_EVENT_POSITION 0   /* arg = playhead in ticks */
#define MFP_EVENT_ENDED    1   /* arg = 0 */
#define MFP_EVENT_FAULTED  2   /* arg = 0 */

/* audio_device_index sentinels. Any other value is a global PortAudio device index. */
#define MFP_AUDIO_DEFAULT  -1   /* system default output device */
#define MFP_AUDIO_NONE     -2   /* do not wire audio */

/* Opaque handles. */
typedef void* mfp_player;
typedef void* mfp_video_router;
typedef void* mfp_audio_router;
typedef void* mfp_output;

/*
 * Event callback. Fires on a framework thread (clock / decode) — marshal to your own thread; do not
 * call back into transport from inside it. `player` is the handle you registered.
 */
typedef void (*mfp_event_callback)(mfp_player player, int event_type, int64_t arg, void* user_data);

/* ---- Lifecycle ---------------------------------------------------------------------------------- */
int  mfp_initialize(void);
void mfp_shutdown(void);

/* ---- Batteries-included open (file + default audio + optional SDL window) ----------------------- */
int  mfp_open_file(const char* utf8_path, int with_video_window, int audio_device_index, mfp_player* out_player);

/* ---- Graph open (no outputs wired; attach your own via the routers) ----------------------------- */
int  mfp_player_open_file(const char* utf8_path, mfp_player* out_player);
int  mfp_player_open_uri(const char* utf8_uri, mfp_player* out_player);
int  mfp_player_open_stream(const uint8_t* data, int length, mfp_player* out_player);

void mfp_close(mfp_player player);

/* ---- Transport ---------------------------------------------------------------------------------- */
int  mfp_play(mfp_player player);
int  mfp_pause(mfp_player player);
int  mfp_seek(mfp_player player, int64_t position_ticks);

/* ---- Queries (return -1 on an invalid handle) --------------------------------------------------- */
int64_t mfp_get_position_ticks(mfp_player player);
int64_t mfp_get_duration_ticks(mfp_player player);
int     mfp_get_state(mfp_player player);   /* one of MFP_STATE_* */
int     mfp_is_ended(mfp_player player);    /* 1 = ended, 0 = not, -1 = bad handle */

/* ---- Events ------------------------------------------------------------------------------------- */
int  mfp_player_set_event_callback(mfp_player player, mfp_event_callback callback, void* user_data);

/* ---- Graph: routers + ids ----------------------------------------------------------------------- */
mfp_video_router mfp_player_video_router(mfp_player player);   /* NULL on bad handle */
mfp_audio_router mfp_player_audio_router(mfp_player player);   /* NULL if no audio / bad handle */
/* Copy the input/source id (UTF-8, NUL-terminated) into buffer; returns bytes needed (excl. NUL).
 * Pass buffer=NULL/len=0 to query the size. */
int  mfp_player_video_input_id(mfp_player player, char* buffer, int buffer_len);
int  mfp_player_audio_source_id(mfp_player player, char* buffer, int buffer_len);

/* ---- Output factories (you own the returned handle; free with mfp_output_destroy) --------------- */
/* sample_rate must equal mfp_audio_router_sample_rate of the router it will be attached to. */
int  mfp_portaudio_output_create(int device_index, int sample_rate, int channels, mfp_output* out_output);
int  mfp_sdl_window_output_create(const char* utf8_title, int width, int height, mfp_output* out_output);
void mfp_output_destroy(mfp_output output);

/* ---- Video router wiring ------------------------------------------------------------------------ */
/* add_output writes the new output id into out_id_buffer (same copy semantics as the id getters). */
int  mfp_video_router_add_output(mfp_video_router router, mfp_output output, char* out_id_buffer, int id_buffer_len);
int  mfp_video_router_add_route(mfp_video_router router, const char* input_id, const char* output_id);
int  mfp_video_router_remove_output(mfp_video_router router, const char* output_id);

/* ---- Audio router wiring ------------------------------------------------------------------------ */
int  mfp_audio_router_sample_rate(mfp_audio_router router);   /* Hz, or -1 on a bad handle */
int  mfp_audio_router_add_output(mfp_audio_router router, mfp_output output, char* out_id_buffer, int id_buffer_len);
int  mfp_audio_router_connect(mfp_audio_router router, const char* source_id, const char* output_id, float gain);
int  mfp_audio_router_set_gain(mfp_audio_router router, const char* source_id, const char* output_id, float gain);
int  mfp_audio_router_remove_output(mfp_audio_router router, const char* output_id);

/* ---- PortAudio device discovery ----------------------------------------------------------------- */
/* Call *_count first (snapshots the list on the calling thread), then *_get(index, ...). Any out
 * pointer may be NULL. Returns negative on error. */
int  mfp_portaudio_output_device_count(void);
int  mfp_portaudio_output_device_get(int index, int* out_device_index, int* out_max_channels,
                                     double* out_default_sample_rate, char* name_buffer, int name_len);
int  mfp_portaudio_input_device_count(void);
int  mfp_portaudio_input_device_get(int index, int* out_device_index, int* out_max_channels,
                                    double* out_default_sample_rate, char* name_buffer, int name_len);

/* ---- Diagnostics -------------------------------------------------------------------------------- */
int  mfp_last_error(char* buffer, int buffer_len);

#ifdef __cplusplus
}
#endif

#endif /* S_MEDIA_PLAYER_H */
