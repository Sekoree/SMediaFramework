/*
 * mfp_plugin.h - MFPlayer native plugin ABI ("Tier B" / S.Abi).
 *
 * ============================  DRAFT - ABI v1 NOT YET TAGGED  ============================
 * The inbound twin of the outbound s_media_player.h: where s_media_player lets other languages *drive*
 * MFPlayer, this lets other languages *extend* it. A plugin (NativeAOT C#, C, C++, Rust, Zig, …) is any
 * shared library that exports mfp_plugin_register() and fills capability vtables. The S.Abi host
 * dlopen()s it and adapts each vtable to the same managed registry interface a first-party module uses;
 * after registration the engine cannot tell a plugin backend from a built-in one. The host stays 100%
 * NativeAOT - NativeLibrary.Load + GetExport + delegate* unmanaged, no reflection. See
 * Next/05-Plugin-Model.md and Next/09-Phase-Checklists.md (Phase 6).
 *
 * This struct set is the "forever-surface": review it hard before tagging ABI v1 (OQ1/D9). Open
 * questions surfaced inline: OQ1 (per-kind frame union), OQ2 (negotiated GPU sync).
 *
 * Conventions (ABI hygiene - keep all of these):
 *   - C ABI only; every callable is `extern "C"`, no C++ types cross the boundary.
 *   - Versioning: every public struct/vtable begins with `abi_version` + `struct_size`; the host checks the *major* and
 *     ignores unknown trailing fields, so the ABI evolves *append-only* - never reorder/resize/remove an
 *     existing field; only append (incl. appending optional function pointers to a vtable's end). On a
 *     major mismatch the host rejects + logs, never crashes.
 *   - Optional capabilities = a NULL function pointer. A plugin leaves an optional vtable entry NULL when
 *     it doesn't support it; the host checks for NULL and falls back (e.g. an output with a NULL
 *     output_played_frames simply can't be a clock master). This keeps capabilities discoverable and the
 *     vtable append-only - new optional capabilities are added as new trailing function pointers.
 *   - Errors: functions return `int` (an MfpStatus; 0 ok, negative error). Never throw/longjmp across the
 *     boundary. After a negative return the host may read a thread-local string via MfpHostApi.set_last_error.
 *   - Time: 100-ns ticks everywhere (matches s_media_player and the managed clock).
 *   - Ownership: a source/subtitle producer owns each returned frame and keeps all CPU/GPU handles valid until
 *     the host calls that capability's release_frame. Output submit is synchronous; an output that queues a frame
 *     must copy/retain what it needs before returning. GPU-handle frames carry negotiated MfpSync metadata.
 *   - Threading: each vtable call documents its thread. Real-time calls (audio submit/read, output submit,
 *     layer render) run on the engine's clock/compositor thread and MUST return promptly - push slow work
 *     to the plugin's own thread. The (potentially slow) asset load for a layer happens in create/configure.
 *   - Trust: plugins run in-process with full rights (like any .so). Loading is opt-in from an explicit
 *     trusted directory/allowlist - "only load plugins you trust," same as VST/OBS.
 *
 * ============================  NORMATIVE CONTRACT (PLUG-01)  ============================
 * The rules above are conventions; the rules below are REQUIREMENTS a conforming plugin and host both obey.
 * "MUST" / "MUST NOT" are binding; violating them is undefined behaviour.
 *
 *  1. Per-instance call serialization (concurrency).
 *       - The host MUST NOT invoke two methods of the SAME capability instance concurrently. Every vtable
 *         call on a given instance is serialized (happens-before the next), so a plugin needs no internal
 *         lock for its own per-instance state - EXCEPT the release_frame family and destroy, which observe
 *         the ordering rules in (4)/(5) below.
 *       - The host MAY call DIFFERENT instances (even of the same vtable) concurrently on different threads.
 *         Any state a plugin shares across instances (globals, caches) MUST be synchronized by the plugin.
 *       - A plugin's own background threads are the plugin's responsibility: they MUST NOT call host methods
 *         except where a method's doc explicitly permits it, and MUST be quiesced by destroy (see (5)).
 *
 *  2. Re-entrancy.
 *       - A plugin MUST NOT call back into the engine (any MfpHostApi method other than set_last_error, and
 *         any capability it obtained) from within a REAL-TIME callback (audio submit/read, output submit,
 *         layer render). set_last_error is always safe and is the only host call permitted from a failing
 *         real-time path.
 *       - The host guarantees it will not re-enter a plugin instance from within that instance's own callback:
 *         a plugin method may assume it holds no host lock and may safely block on its own resources (but
 *         MUST still return promptly from real-time calls - offload slow work to its own thread).
 *
 *  3. Pointer + buffer lifetime.
 *       - Pointers the host passes INTO a call (formats, option structs, control args, string arguments) are
 *         borrowed and valid ONLY for the duration of that call. A plugin that needs them later MUST copy.
 *       - Frames/handles a plugin returns are owned by the plugin and MUST stay valid until the host calls the
 *         owning capability's release_frame; the host MUST call release_frame exactly once per produced frame.
 *       - Strings a plugin returns to the host (names, error text) MUST remain valid until the next call on the
 *         same instance, or be copied by the host before then - the host copies immediately.
 *
 *  4. release_frame ordering.
 *       - release_frame for a given instance MAY be called on a thread different from the producing call, but
 *         the host guarantees it is never concurrent with another call on that same instance, and always
 *         happens-before destroy for that instance. A producer therefore frees a frame's backing knowing no
 *         other method of that instance runs concurrently.
 *
 *  5. destroy vs. in-flight work (no destroy-vs-work race).
 *       - Before calling destroy on an instance the host QUIESCES it: it stops issuing real-time calls, waits
 *         for any in-flight call on that instance to return, and releases all outstanding frames. destroy is
 *         therefore never concurrent with any other method of that instance.
 *       - destroy MUST join/stop the plugin's own worker threads and free all resources before returning. After
 *         destroy the host never touches that instance again; the plugin MUST NOT use the host handle afterwards.
 *       - The mirror on the outbound side (s_media_player.h) is enforced in code: mfp_session_destroy/shutdown
 *         wait for in-flight ABI calls to drain before releasing the session.
 */
#ifndef MFP_PLUGIN_H
#define MFP_PLUGIN_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define MFP_MAKE_ABI_VERSION(major, minor) ((((uint32_t)(major)) << 16) | ((uint32_t)(minor) & 0xffffu))
#define MFP_ABI_VERSION_MAJOR(version) ((uint32_t)(version) >> 16)
#define MFP_ABI_VERSION_MINOR(version) ((uint32_t)(version) & 0xffffu)
#define MFP_PLUGIN_ABI_VERSION MFP_MAKE_ABI_VERSION(1, 0)

#define MFP_STRUCT_HEADER \
    uint32_t abi_version; \
    uint32_t struct_size

/* ------------------------------------------------------------------ status + logging ---------- */

typedef enum MfpStatus {
    MFP_OK              =  0,
    MFP_ERR_UNSUPPORTED = -1,   /* capability/format/operation not provided */
    MFP_ERR_INVALID_ARG = -2,
    MFP_ERR_NOT_FOUND   = -3,   /* e.g. unknown device id */
    MFP_ERR_AGAIN       = -4,   /* no data ready / no room yet (non-blocking) - not an error */
    MFP_ERR_END         = -5,   /* source exhausted */
    MFP_ERR_INTERNAL    = -6,
    MFP_ERR_ABI_MISMATCH = -7
} MfpStatus;

typedef enum MfpLogLevel {
    MFP_LOG_TRACE = 0, MFP_LOG_DEBUG = 1, MFP_LOG_INFO = 2, MFP_LOG_WARN = 3, MFP_LOG_ERROR = 4
} MfpLogLevel;

/* Capability bitset (MfpPluginInfo.capabilities). One plugin may advertise several. */
typedef enum MfpCapability {
    MFP_CAP_AUDIO_BACKEND   = 1u << 0,
    MFP_CAP_VIDEO_SOURCE    = 1u << 1,   /* a media-source provider that opens video */
    MFP_CAP_AUDIO_SOURCE    = 1u << 2,   /* a media-source provider that opens audio */
    MFP_CAP_VIDEO_OUTPUT    = 1u << 3,
    MFP_CAP_LAYER_SURFACE   = 1u << 4,
    MFP_CAP_SUBTITLE        = 1u << 5,
    MFP_CAP_CONTROL_DECODER = 1u << 6,
    MFP_CAP_AUDIO_EFFECT    = 1u << 7,   /* an insertable audio bus effect (per-kind factory) */
    MFP_CAP_VIDEO_EFFECT    = 1u << 8    /* an insertable video bus effect (per-kind factory) */
} MfpCapability;

/* ------------------------------------------------------------------ formats ------------------- */

/* Mirrors S.Media.Core.Video.PixelFormat - keep in sync (the managed adapter maps 1:1). GPU-backed
 * frames may report MFP_PF_UNKNOWN and carry a fourcc/dxgi_format in their kind struct. */
typedef enum MfpPixelFormat {
    MFP_PF_UNKNOWN = 0,
    MFP_PF_BGRA32, MFP_PF_RGBA32, MFP_PF_ARGB32, MFP_PF_BGR24, MFP_PF_RGB24,
    MFP_PF_RGBA16, MFP_PF_RGBA16F,
    MFP_PF_I420, MFP_PF_YV12, MFP_PF_NV12, MFP_PF_NV21,
    MFP_PF_YUYV, MFP_PF_UYVY, MFP_PF_YUV422P, MFP_PF_YUV444P, MFP_PF_YUV422P10LE,
    MFP_PF_P010, MFP_PF_P016, MFP_PF_P216, MFP_PF_PA16
} MfpPixelFormat;

typedef enum MfpAudioSampleFormat {
    MFP_AF_F32_INTERLEAVED = 0   /* the engine's canonical audio exchange format */
} MfpAudioSampleFormat;

typedef struct MfpVideoFormat {
    uint32_t       width;
    uint32_t       height;
    MfpPixelFormat pixel_format;
    uint32_t       fps_num;       /* 0/0 = unspecified / variable */
    uint32_t       fps_den;
} MfpVideoFormat;

typedef struct MfpAudioFormat {
    uint32_t             sample_rate;
    uint32_t             channels;
    MfpAudioSampleFormat sample_format;
} MfpAudioFormat;

/* Open-time audio options (↔ AudioBackendOptions). */
typedef struct MfpAudioOpts {
    double   suggested_latency_seconds;   /* requested device latency; backend rounds to what it supports; <=0 = default */
    uint32_t prebuffer_frames;            /* frames to prebuffer before starting the stream (0 = backend default) */
} MfpAudioOpts;

/* Row-major 2x3 affine (normalized canvas space), matching the managed VideoPlacementSpec transform. */
typedef struct MfpTransform2D {
    float a, b, c, d, tx, ty;
} MfpTransform2D;

/* ----------------------------------------------------- cross-boundary GPU sync (OQ2) ---------- */
/* Negotiated per consumer via capability query; CPU frames use MFP_SYNC_NONE. */
typedef enum MfpSyncKind {
    MFP_SYNC_NONE = 0,            /* CPU frame, or already synchronized */
    MFP_SYNC_KEYED_MUTEX,        /* D3D11 keyed mutex; `value` = key */
    MFP_SYNC_SEMAPHORE_BINARY,   /* binary semaphore (NT handle / fd in `handle`) */
    MFP_SYNC_SEMAPHORE_TIMELINE, /* timeline semaphore; `value` = wait/signal point */
    MFP_SYNC_FENCE               /* fence (sync_file fd / D3D12 fence) */
} MfpSyncKind;

typedef struct MfpSync {
    MfpSyncKind kind;
    uint64_t    handle;          /* OS sync object (NT handle / fd / pointer); 0 if none */
    uint64_t    value;           /* timeline value or keyed-mutex key */
} MfpSync;

/* --------------------------------------------------- frames: tagged union, one per kind (OQ1) -- */
/* One uint64 "gpu handle" is not enough - each backing needs its own fields. These mirror the
 * S.Media.Core HW backings so the managed adapter is a near-direct map. CPU-only plugins use only
 * MFP_FRAME_CPU and may ignore the GPU kinds entirely. */
typedef enum MfpFrameKind {
    MFP_FRAME_CPU = 0,
    MFP_FRAME_DMABUF,            /* Linux  - mirrors Dmabuf{Nv12,P010,P016}Backing */
    MFP_FRAME_D3D11,             /* Windows - mirrors Win32SharedNv12Backing */
    MFP_FRAME_GL_TEXTURE         /* same-GL-context only (layer surfaces); never cross-process */
} MfpFrameKind;

#define MFP_FRAME_KIND_BIT(kind) (1u << (uint32_t)(kind))
#define MFP_SYNC_KIND_BIT(kind)  (1u << (uint32_t)(kind))

typedef struct MfpCpuFrame {
    void*   planes[4];
    int32_t strides[4];
    int32_t plane_count;
} MfpCpuFrame;

typedef struct MfpDmaBufFrame {
    int32_t  plane_count;
    int32_t  fds[4];             /* dma-buf fds; the host imports, the producer keeps ownership until release */
    int32_t  offsets[4];
    int32_t  strides[4];
    uint64_t modifiers[4];       /* DRM_FORMAT_MOD_* per plane/object */
    uint32_t fourcc;             /* DRM FourCC */
} MfpDmaBufFrame;

typedef struct MfpD3D11Frame {
    uint64_t luma_nt_shared_handle;
    uint64_t chroma_nt_shared_handle; /* may equal luma_nt_shared_handle */
    uint32_t dxgi_format;
    uint32_t array_slice;        /* 0 for non-array textures */
    int32_t  y_stride;
    int32_t  uv_stride;
} MfpD3D11Frame;

typedef struct MfpGlTextureFrame {
    uint32_t texture_id;
    uint32_t target;             /* e.g. GL_TEXTURE_2D */
    uint64_t context_id;         /* must equal the consuming MfpGlContext.context_id */
} MfpGlTextureFrame;

typedef struct MfpVideoFrame {
    /* common header */
    MfpFrameKind   kind;
    uint32_t       width;
    uint32_t       height;
    MfpPixelFormat pixel_format;
    int64_t        pts_ticks;    /* 100-ns ticks */
    MfpSync        sync;         /* GPU kinds only; MFP_SYNC_NONE for CPU */
    void*          opaque;       /* producer cookie, echoed back to its release_frame */
    /* kind-specific payload */
    union {
        MfpCpuFrame       cpu;
        MfpDmaBufFrame    dmabuf;
        MfpD3D11Frame     d3d11;
        MfpGlTextureFrame gl;
    } u;
} MfpVideoFrame;

/* GL context handed to a layer surface. render() always runs on this context's (current) thread. */
typedef struct MfpGlContext {
    uint64_t context_id;
    void* (*get_proc_address)(const char* name);
} MfpGlContext;

typedef struct MfpAudioDeviceInfo {
    char     id[128];
    char     name[128];
    uint32_t max_channels;
    uint32_t default_sample_rate;
} MfpAudioDeviceInfo;

/* ------------------------------------------------------ host services handed to the plugin ----- */
typedef struct MfpHostApi {
    MFP_STRUCT_HEADER;
    void    (*log)(MfpLogLevel level, const char* msg);
    void    (*set_last_error)(const char* msg);          /* thread-local; host reads after a negative status */
    int64_t (*now_ticks)(void);                          /* host clock, 100-ns ticks */
    uint32_t supported_frame_kinds;                      /* MFP_FRAME_KIND_BIT mask */
    uint32_t supported_sync_kinds;                       /* MFP_SYNC_KIND_BIT mask */
    /* … append-only … */
} MfpHostApi;

/* ==================================================================== capability vtables ====== *
 * Each vtable shadows one managed interface. The first parameter (`self`/`handle`/`src`/`surface`) is
 * the plugin instance the matching add_* / open / create returned. Trailing function pointers marked
 * "optional" may be NULL - the host then uses a fallback. */

/* Audio backend ↔ IAudioBackend. submit/read_into run on the audio clock thread and MUST NOT block. */
typedef struct MfpAudioBackendVTable {
    MFP_STRUCT_HEADER;
    int   (*enumerate_outputs)(void* self, MfpAudioDeviceInfo* out, int cap, int* count);
    int   (*enumerate_inputs )(void* self, MfpAudioDeviceInfo* out, int cap, int* count);
    void* (*open_output)(void* self, const char* device_id, const MfpAudioFormat* fmt, const MfpAudioOpts* opts);
    void* (*open_input )(void* self, const char* device_id, const MfpAudioFormat* fmt, const MfpAudioOpts* opts);
    int   (*output_submit)(void* handle, const float* interleaved, int float_count);   /* push; MFP_ERR_AGAIN if full */
    int   (*input_read_into)(void* handle, float* dst, int float_count);               /* pull → floats written; negative MfpStatus */
    void  (*close_handle)(void* handle);
    void  (*destroy)(void* self);
    /* --- optional capabilities (NULL if unsupported) --- */
    int64_t (*output_played_frames)(void* handle);    /* total frames physically PLAYED - the master clock
                                                       * (↔ IAudioOutputPlaybackStats.PlayedSamples / IClockedOutput).
                                                       * NULL ⇒ this output cannot be the clock master. */
    int     (*output_writable_frames)(void* handle);  /* room (frames) before output_submit would block, for
                                                       * backpressure (↔ WaitForCapacity). NULL ⇒ assume writable. */
} MfpAudioBackendVTable;

/* A pull audio source instance (e.g. NDI audio, a file's audio track, a synth). */
typedef struct MfpAudioSourceVTable {
    MFP_STRUCT_HEADER;
    int  (*native_format)(void* src, MfpAudioFormat* out);
    int  (*read_into)(void* src, float* dst, int float_count);    /* → floats written; MFP_ERR_AGAIN / MFP_ERR_END */
    int  (*is_exhausted)(void* src);
    int  (*seek)(void* src, int64_t position_ticks);              /* MFP_ERR_UNSUPPORTED if not seekable */
    void (*destroy)(void* src);
} MfpAudioSourceVTable;

/* A video source instance ↔ IVideoSource. try_read_frame may run on a decode thread. */
typedef struct MfpVideoSourceVTable {
    MFP_STRUCT_HEADER;
    uint32_t supported_frame_kinds;  /* kinds try_read_frame may return */
    uint32_t supported_sync_kinds;   /* sync kinds it may attach */
    int  (*native_pixel_formats)(void* src, MfpPixelFormat* out, int cap, int* count);
    int  (*select_output_format)(void* src, MfpPixelFormat chosen);
    int  (*get_format)(void* src, MfpVideoFormat* out);       /* resolved output format (w/h/pixel_format); host queries it before the first read (↔ IVideoSource.Format) */
    int  (*try_read_frame)(void* src, MfpVideoFrame* out);    /* MFP_OK / MFP_ERR_AGAIN / MFP_ERR_END */
    void (*release_frame)(void* src, MfpVideoFrame* frame);   /* return a frame this source produced */
    int  (*is_exhausted)(void* src);
    int  (*seek)(void* src, int64_t position_ticks);          /* MFP_ERR_UNSUPPORTED if not seekable */
    void (*destroy)(void* src);
} MfpVideoSourceVTable;

/* What a provider's open() yields for a URI: a video and/or audio source, both valid only with the
 * provider's matching *_source_vtable. Either may be NULL. A single open() keeps any shared underlying
 * connection (e.g. NDI delivers correlated A/V over ONE receiver - open once, get both). */
typedef struct MfpMediaSource {
    void* video;   /* → MfpVideoSourceVTable instance, or NULL */
    void* audio;   /* → MfpAudioSourceVTable instance, or NULL */
} MfpMediaSource;

/* Media source provider ↔ IMediaDecoderProvider (opens a URI → its sources). Registered with a scheme. */
typedef struct MfpMediaSourceProviderVTable {
    MFP_STRUCT_HEADER;
    int (*can_open)(void* self, const char* uri);             /* 1 = this provider handles `uri` */
    int (*open)(void* self, const char* uri, MfpMediaSource* out);  /* fills out->video/audio (NULL where N/A) */
    const MfpVideoSourceVTable* video_source_vtable;          /* NULL if this provider never opens video */
    const MfpAudioSourceVTable* audio_source_vtable;          /* NULL if this provider never opens audio */
    void (*destroy)(void* self);
} MfpMediaSourceProviderVTable;

/* Video output ↔ IVideoOutput. submit() runs on the clock/compositor thread; return promptly. */
typedef struct MfpVideoOutputVTable {
    MFP_STRUCT_HEADER;
    uint32_t accepted_frame_kinds;   /* MFP_FRAME_KIND_BIT mask */
    uint32_t accepted_sync_kinds;    /* MFP_SYNC_KIND_BIT mask */
    int  (*accepted_pixel_formats)(void* self, MfpPixelFormat* out, int cap, int* count);
    int  (*configure)(void* self, const MfpVideoFormat* fmt);
    int  (*submit)(void* self, const MfpVideoFrame* frame);
    void (*destroy)(void* self);
    /* --- optional queue control (↔ IVideoOutputQueueControl); NULL if the output has no internal queue --- */
    void (*abandon_queued)(void* self);                       /* drop queued frames on seek/flush */
    int  (*wait_for_idle)(void* self, int32_t timeout_ms);    /* block until queued frames present; → 1 idle / 0 timeout */
} MfpVideoOutputVTable;

/* A created compositor layer-surface instance ↔ IVideoCompositorLayerSurface. render() on the compositor thread. */
typedef struct MfpLayerSurfaceVTable {
    MFP_STRUCT_HEADER;
    int  (*configure_gl)(void* surface, const MfpGlContext* ctx, uint32_t canvas_w, uint32_t canvas_h);
    int  (*render)(void* surface, const MfpGlContext* ctx, uint32_t target_fbo,
                   int64_t master_ticks, const MfpTransform2D* placement, float opacity);
    void (*destroy)(void* surface);
} MfpLayerSurfaceVTable;

/* Layer-surface FACTORY, registered per `kind` (e.g. "mmd", "shadertoy"). Carries the GL context + target
 * FBO across the boundary so a native plugin renders straight into the canvas. */
typedef struct MfpLayerSurfaceFactoryVTable {
    MFP_STRUCT_HEADER;
    /* Create a surface instance configured by `config_json` - an opaque, plugin-defined blob taken
     * verbatim from the cue/composition layer spec, e.g. for an "mmd" layer:
     *   {"models":["miku.pmx","stage.pmx"],"motion":"rolling-girl.vmd","camera":"cam.vmd"}
     * The (possibly slow) asset load happens here / in configure_gl - never in render. → instance or NULL. */
    void* (*create)(void* self, const char* config_json);
    const MfpLayerSurfaceVTable* surface_vtable;              /* vtable for instances `create` returns */
    void  (*destroy)(void* self);                            /* destroy the factory itself */
} MfpLayerSurfaceFactoryVTable;

/* A created subtitle/overlay source instance ↔ ISubtitleSource / IVideoOverlaySource. */
typedef struct MfpSubtitleVTable {
    MFP_STRUCT_HEADER;
    /* MFP_OK = filled `out`; MFP_ERR_AGAIN = nothing visible at `position`; <0 = error. */
    int  (*render_at)(void* self, int64_t position_ticks, MfpVideoFrame* out);
    void (*release_frame)(void* self, MfpVideoFrame* frame);
    void (*destroy)(void* self);
} MfpSubtitleVTable;

/* Subtitle provider ↔ ISubtitleProvider (opens a sidecar/stream → an overlay source at canvas size). */
typedef struct MfpSubtitleProviderVTable {
    MFP_STRUCT_HEADER;
    int   (*can_open)(void* self, const char* uri);
    void* (*open)(void* self, const char* uri, uint32_t canvas_w, uint32_t canvas_h);  /* → subtitle instance or NULL */
    const MfpSubtitleVTable* subtitle_vtable;
    void  (*destroy)(void* self);
} MfpSubtitleProviderVTable;

/* Control feedback decoder ↔ IControlFeedbackDecoder - the inbound twin of MeterBlobDecoder, for
 * device-profile entries like "decoder": "x32.meters". Decodes an OSC/MIDI/binary feedback payload
 * into numeric cache readings. (NOTE: this is the only control surface a plugin extends - the OSC/MIDI
 * transport and the Mond engine stay in the framework; a new device = a profile + maybe a decoder.) */
typedef struct MfpControlReading {
    char   address[160];
    double value;
} MfpControlReading;

typedef enum MfpControlArgKind {
    MFP_CONTROL_ARG_UNSUPPORTED = 0,
    MFP_CONTROL_ARG_INT32,
    MFP_CONTROL_ARG_FLOAT32,
    MFP_CONTROL_ARG_STRING,
    MFP_CONTROL_ARG_BLOB,
    MFP_CONTROL_ARG_INT64,
    MFP_CONTROL_ARG_DOUBLE64,
    MFP_CONTROL_ARG_TRUE,
    MFP_CONTROL_ARG_FALSE,
    MFP_CONTROL_ARG_NIL,
    MFP_CONTROL_ARG_IMPULSE
} MfpControlArgKind;

/* `data` is UTF-8 for STRING and raw bytes for BLOB; it remains valid only for the decode call. */
typedef struct MfpControlArg {
    MfpControlArgKind kind;
    int32_t           data_len;
    int64_t           int_value;
    double            float_value;
    const uint8_t*    data;
} MfpControlArg;

typedef struct MfpControlDecoderVTable {
    MFP_STRUCT_HEADER;
    int  (*decode)(void* self,
                   const char* osc_address,
                   const MfpControlArg* args, int arg_count, int blob_arg_index,
                   MfpControlReading* out, int out_cap, int* out_count);
    void (*destroy)(void* self);
} MfpControlDecoderVTable;

/* Audio bus effect ↔ IAudioBusEffect - one in-place processing stage hosted by an output insert or
 * send/return bus. REAL-TIME CONTRACT: `process` runs on the audio pull/pump path - bounded work per
 * chunk, no allocation, no locking, no host reentry (the general RT rules above apply). The host never
 * invokes two methods of the same instance concurrently. */
typedef struct MfpAudioEffectVTable {
    MFP_STRUCT_HEADER;
    /* Called once before the first process and again when the host reconfigures. Sample format is
     * always f32 interleaved (MfpAudioFormat.sample_format = 0). */
    int  (*configure)(void* effect, const MfpAudioFormat* format);
    /* Process `count` interleaved floats IN PLACE. `frame_position` is the running per-channel sample
     * count since the insert started (for LFOs/automation). */
    int  (*process)(void* effect, float* interleaved, int32_t count, int64_t frame_position);
    void (*destroy)(void* effect);
} MfpAudioEffectVTable;

/* Audio-effect FACTORY, registered per `kind` (e.g. "acme.compressor") - the same shape as the layer-
 * surface factory: `create` builds one effect instance from the host's opaque per-insert JSON config
 * (may be NULL), instances use the shared `effect_vtable`. Slow setup belongs in create/configure,
 * never in process. → instance or NULL (set_last_error first). */
typedef struct MfpAudioEffectFactoryVTable {
    MFP_STRUCT_HEADER;
    void* (*create)(void* self, const char* config_json);
    const MfpAudioEffectVTable* effect_vtable;               /* vtable for instances `create` returns */
    void  (*destroy)(void* self);                            /* destroy the factory itself */
} MfpAudioEffectFactoryVTable;

/* Video bus effect ↔ IVideoBusEffect - one processing stage on an output's pump drain thread (off the
 * clock path). V1 CONTRACT: CPU frames only, mutated IN PLACE - the host passes an MfpVideoFrame whose
 * CPU planes are writable for the duration of the call; the plugin must not retain the pointers.
 * Hardware-backed frames (dmabuf/D3D11/GL) bypass plugin effects unchanged in v1. The host never
 * invokes two methods of the same instance concurrently. */
typedef struct MfpVideoEffectVTable {
    MFP_STRUCT_HEADER;
    int  (*configure)(void* effect, const MfpVideoFormat* format);
    /* Mutate the frame's CPU planes in place. `pts_ticks` is the presentation time in 100-ns ticks. */
    int  (*process)(void* effect, const MfpVideoFrame* frame, int64_t pts_ticks);
    void (*destroy)(void* effect);
} MfpVideoEffectVTable;

/* Video-effect FACTORY, registered per `kind` - identical shape to the audio-effect factory. */
typedef struct MfpVideoEffectFactoryVTable {
    MFP_STRUCT_HEADER;
    void* (*create)(void* self, const char* config_json);
    const MfpVideoEffectVTable* effect_vtable;
    void  (*destroy)(void* self);
} MfpVideoEffectFactoryVTable;

/* ============================================================== registration + entry point ===== */

/* The host hands this in; the plugin registers each capability through it. `ctx` is opaque host state
 * passed back to every add_*; `self` is the plugin instance forwarded to that capability's vtable calls.
 * Each add_* returns an MfpStatus. */
typedef struct MfpRegistrar {
    MFP_STRUCT_HEADER;
    void*    ctx;
    int (*add_audio_backend)(void* ctx, const char* id, const MfpAudioBackendVTable* vt, void* self);
    int (*add_media_source_provider)(void* ctx, const char* scheme, const MfpMediaSourceProviderVTable* vt, void* self);
    int (*add_video_output)(void* ctx, const char* id, const MfpVideoOutputVTable* vt, void* self);
    int (*add_layer_surface)(void* ctx, const char* kind, const MfpLayerSurfaceFactoryVTable* vt, void* self);
    int (*add_subtitle_provider)(void* ctx, const char* ext, const MfpSubtitleProviderVTable* vt, void* self);
    int (*add_control_decoder)(void* ctx, const char* id, const MfpControlDecoderVTable* vt, void* self);
    int (*add_audio_effect)(void* ctx, const char* kind, const MfpAudioEffectFactoryVTable* vt, void* self);
    int (*add_video_effect)(void* ctx, const char* kind, const MfpVideoEffectFactoryVTable* vt, void* self);
    /* … append-only … */
} MfpRegistrar;

typedef struct MfpPluginInfo {
    MFP_STRUCT_HEADER;
    const char* id;               /* e.g. "com.acme.webcam" */
    const char* display_name;
    uint32_t    capabilities;     /* bitset of MfpCapability */
} MfpPluginInfo;

/*
 * THE entry point every plugin exports (extern "C", default visibility). Called once on load, on the
 * host loader thread. The plugin should:
 *   1. validate host->abi_version (reject if the major differs from MFP_PLUGIN_ABI_VERSION),
 *   2. fill *out_info,
 *   3. register its capability vtables via reg->add_*,
 *   4. return MFP_OK, or a negative MfpStatus (after host->set_last_error) on failure.
 */
int mfp_plugin_register(const MfpHostApi* host, MfpPluginInfo* out_info, MfpRegistrar* reg);

/* Optional: called once on unload to free plugin-global state. Safe to omit. */
void mfp_plugin_unregister(void);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* MFP_PLUGIN_H */
