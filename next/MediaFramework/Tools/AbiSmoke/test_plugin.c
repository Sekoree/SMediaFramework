/*
 * test_plugin.c — a minimal native MFPlayer plugin (the Phase-6 gate fixture). Compiled to a .so by AbiSmoke and
 * loaded through S.Abi.AbiPluginHost. It exports mfp_plugin_register and registers a media-source provider AND a
 * control decoder. The capability vtables are stubs here — this gate proves the *load + registration* path (the
 * host records the vtable pointers); the managed adapters that actually call the vtables are the next layer.
 *
 * Build: gcc -shared -fPIC -I<S.Abi/include> test_plugin.c -o mfp_test_plugin.so
 */
#include "mfp_plugin.h"
#include <stdio.h>    /* snprintf */
#include <string.h>   /* memset */
#include <stdlib.h>   /* atoi */

static int provider_can_open(void* self, const char* uri) { (void)self; (void)uri; return 1; }
static int provider_open(void* self, const char* uri, MfpMediaSource* out) {
    (void)uri;
    out->video = self;
    out->audio = 0;
    return MFP_OK;
}
/* a tiny 4x4 BGRA test source: emits one frame (every pixel B=10 G=20 R=30 A=255), then is exhausted. */
#define MFP_TEST_W 4
#define MFP_TEST_H 4
static int g_src_emitted = 0;
static unsigned char g_src_pixels[MFP_TEST_W * MFP_TEST_H * 4];

static int src_native_pixel_formats(void* src, MfpPixelFormat* out, int cap, int* count) {
    (void)src;
    if (cap < 1) { *count = 0; return MFP_OK; }
    out[0] = MFP_PF_BGRA32;
    *count = 1;
    return MFP_OK;
}
static int src_select_output_format(void* src, MfpPixelFormat chosen) {
    (void)src;
    return chosen == MFP_PF_BGRA32 ? MFP_OK : MFP_ERR_UNSUPPORTED;
}
static int src_get_format(void* src, MfpVideoFormat* out) {
    (void)src;
    out->width = MFP_TEST_W; out->height = MFP_TEST_H; out->pixel_format = MFP_PF_BGRA32;
    out->fps_num = 30; out->fps_den = 1;
    return MFP_OK;
}
static int src_try_read_frame(void* src, MfpVideoFrame* out) {
    int i;
    (void)src;
    if (g_src_emitted) return MFP_ERR_END;
    for (i = 0; i < MFP_TEST_W * MFP_TEST_H; i++) {
        g_src_pixels[i*4+0] = 10; g_src_pixels[i*4+1] = 20; g_src_pixels[i*4+2] = 30; g_src_pixels[i*4+3] = 255;
    }
    memset(out, 0, sizeof(*out));
    out->kind = MFP_FRAME_CPU;
    out->width = MFP_TEST_W; out->height = MFP_TEST_H; out->pixel_format = MFP_PF_BGRA32;
    out->pts_ticks = 0;
    out->u.cpu.planes[0] = g_src_pixels;
    out->u.cpu.strides[0] = MFP_TEST_W * 4;
    out->u.cpu.plane_count = 1;
    g_src_emitted = 1;
    return MFP_OK;
}
static void src_release_frame(void* src, MfpVideoFrame* frame) { (void)src; (void)frame; }
static int src_is_exhausted(void* src) { (void)src; return g_src_emitted; }
static int src_seek(void* src, int64_t pos) { (void)src; (void)pos; g_src_emitted = 0; return MFP_OK; }
static void src_destroy(void* src) { (void)src; }

static const MfpVideoSourceVTable g_video_vt = {
    src_native_pixel_formats, src_select_output_format, src_get_format,
    src_try_read_frame, src_release_frame, src_is_exhausted, src_seek, src_destroy
};
static const MfpMediaSourceProviderVTable g_provider_vt = {
    provider_can_open, provider_open, &g_video_vt, 0, 0
};

static int decoder_decode(void* self, const char* osc_address, const uint8_t* blob, int blob_len,
                          MfpControlReading* out, int out_cap, int* out_count) {
    (void)self;
    if (out_cap < 1) { *out_count = 0; return MFP_OK; }
    /* one reading: echo the address with a suffix + map the first blob byte to [0,1] (proves data flows in). */
    snprintf(out[0].address, sizeof(out[0].address), "%s/decoded", osc_address ? osc_address : "");
    out[0].value = blob_len > 0 ? (double)blob[0] / 255.0 : -1.0;
    *out_count = 1;
    return MFP_OK;
}
static const MfpControlDecoderVTable g_decoder_vt = { decoder_decode, 0 };

/* --- audio backend: 1 fake output device; output_submit accumulates frames, output_played_frames returns them. --- */
static int g_ab_channels = 2;
static long g_ab_played = 0;
static int ab_enumerate_outputs(void* self, MfpAudioDeviceInfo* out, int cap, int* count) {
    (void)self;
    if (cap < 1) { *count = 0; return MFP_OK; }
    memset(&out[0], 0, sizeof(out[0]));
    snprintf(out[0].id, sizeof(out[0].id), "plugin.out.0");
    snprintf(out[0].name, sizeof(out[0].name), "Plugin Output");
    out[0].max_channels = 2;
    out[0].default_sample_rate = 48000;
    *count = 1;
    return MFP_OK;
}
static int ab_enumerate_inputs(void* self, MfpAudioDeviceInfo* out, int cap, int* count) { (void)self;(void)out;(void)cap; *count = 0; return MFP_OK; }
static void* ab_open_output(void* self, const char* device_id, const MfpAudioFormat* fmt, const MfpAudioOpts* opts) {
    (void)self; (void)device_id; (void)opts;
    g_ab_channels = (fmt && fmt->channels > 0) ? (int)fmt->channels : 2;
    g_ab_played = 0;
    return (void*)&g_ab_played; /* a non-null handle */
}
static void* ab_open_input(void* self, const char* device_id, const MfpAudioFormat* fmt, const MfpAudioOpts* opts) { (void)self;(void)device_id;(void)fmt;(void)opts; return 0; }
static int ab_output_submit(void* handle, const float* interleaved, int float_count) {
    (void)handle; (void)interleaved;
    g_ab_played += float_count / (g_ab_channels > 0 ? g_ab_channels : 1);
    return MFP_OK;
}
static int ab_input_read_into(void* handle, float* dst, int float_count) { (void)handle;(void)dst;(void)float_count; return 0; }
static void ab_close_handle(void* handle) { (void)handle; }
static void ab_destroy(void* self) { (void)self; }
static long ab_output_played_frames(void* handle) { (void)handle; return g_ab_played; }
static int ab_output_writable_frames(void* handle) { (void)handle; return 4096; }
static const MfpAudioBackendVTable g_audio_vt = {
    ab_enumerate_outputs, ab_enumerate_inputs, ab_open_output, ab_open_input,
    ab_output_submit, ab_input_read_into, ab_close_handle, ab_destroy,
    ab_output_played_frames, ab_output_writable_frames
};

/* --- video output: validates the submitted 4x4 BGRA frame + reports the result through host->log. --- */
static const MfpHostApi* g_host = 0;
static int vo_accepted(void* self, MfpPixelFormat* out, int cap, int* count) {
    (void)self;
    if (cap < 1) { *count = 0; return MFP_OK; }
    out[0] = MFP_PF_BGRA32;
    *count = 1;
    return MFP_OK;
}
static int vo_configure(void* self, const MfpVideoFormat* fmt) { (void)self; (void)fmt; return MFP_OK; }
static int vo_submit(void* self, const MfpVideoFrame* frame) {
    int ok;
    (void)self;
    ok = frame && frame->kind == MFP_FRAME_CPU && frame->width == 4 && frame->height == 4
         && frame->pixel_format == MFP_PF_BGRA32;
    if (ok) {
        const unsigned char* p = (const unsigned char*)frame->u.cpu.planes[0];
        ok = p && p[0] == 10 && p[1] == 20 && p[2] == 30 && p[3] == 255;
    }
    if (g_host && g_host->log) g_host->log(MFP_LOG_INFO, ok ? "vout:ok" : "vout:bad");
    return MFP_OK;
}
static void vo_destroy(void* self) { (void)self; }
static void vo_abandon_queued(void* self) { (void)self; }
static int vo_wait_for_idle(void* self, int timeout_ms) { (void)self; (void)timeout_ms; return 1; }
static const MfpVideoOutputVTable g_vout_vt = {
    vo_accepted, vo_configure, vo_submit, vo_destroy, vo_abandon_queued, vo_wait_for_idle
};

/* --- subtitle provider: opens any uri -> an instance rendering a 4x4 BGRA overlay (px0 = 99,99,99,255). --- */
static unsigned char g_sub_pixels[4 * 4 * 4];
static int sub_render_at(void* self, int64_t position_ticks, MfpVideoFrame* out) {
    int i;
    (void)self; (void)position_ticks;
    for (i = 0; i < 16; i++) { g_sub_pixels[i*4+0]=99; g_sub_pixels[i*4+1]=99; g_sub_pixels[i*4+2]=99; g_sub_pixels[i*4+3]=255; }
    memset(out, 0, sizeof(*out));
    out->kind = MFP_FRAME_CPU;
    out->width = 4; out->height = 4; out->pixel_format = MFP_PF_BGRA32;
    out->u.cpu.planes[0] = g_sub_pixels;
    out->u.cpu.strides[0] = 16;
    out->u.cpu.plane_count = 1;
    return MFP_OK;
}
static void sub_release_frame(void* self, MfpVideoFrame* frame) { (void)self; (void)frame; }
static void sub_destroy(void* self) { (void)self; }
static const MfpSubtitleVTable g_sub_vt = { sub_render_at, sub_release_frame, sub_destroy };
static int sub_can_open(void* self, const char* uri) { (void)self; (void)uri; return 1; }
static void* sub_open(void* self, const char* uri, uint32_t w, uint32_t h) { (void)uri; (void)w; (void)h; return self; }
static void sub_provider_destroy(void* self) { (void)self; }
static const MfpSubtitleProviderVTable g_sub_provider_vt = { sub_can_open, sub_open, &g_sub_vt, sub_provider_destroy };

/* --- GL layer surface: the config_json (a number string) drives the clear-colour red channel; render clears the
   target FBO to (r,0,0,1). Loads GL entry points through the ABI's get_proc_address (bridged to the host GL ctx). */
typedef void (*pfn_glClearColor)(float, float, float, float);
typedef void (*pfn_glClear)(unsigned int);
typedef void (*pfn_glBindFramebuffer)(unsigned int, unsigned int);
static pfn_glClearColor p_glClearColor = 0;
static pfn_glClear p_glClear = 0;
static pfn_glBindFramebuffer p_glBindFramebuffer = 0;
static float g_layer_r = 0.0f;
static int ls_configure_gl(void* surface, const MfpGlContext* ctx, uint32_t w, uint32_t h) {
    (void)surface; (void)w; (void)h;
    if (!ctx || !ctx->get_proc_address) return MFP_ERR_UNSUPPORTED;
    p_glClearColor = (pfn_glClearColor)ctx->get_proc_address("glClearColor");
    p_glClear = (pfn_glClear)ctx->get_proc_address("glClear");
    p_glBindFramebuffer = (pfn_glBindFramebuffer)ctx->get_proc_address("glBindFramebuffer");
    return (p_glClearColor && p_glClear) ? MFP_OK : MFP_ERR_UNSUPPORTED;
}
static int ls_render(void* surface, const MfpGlContext* ctx, uint32_t target_fbo, int64_t master_ticks,
                     const MfpTransform2D* placement, float opacity) {
    (void)surface; (void)ctx; (void)master_ticks; (void)placement; (void)opacity;
    if (!p_glClearColor || !p_glClear) return MFP_ERR_UNSUPPORTED;
    if (p_glBindFramebuffer) p_glBindFramebuffer(0x8D40u /*GL_FRAMEBUFFER*/, target_fbo);
    p_glClearColor(g_layer_r, 0.0f, 0.0f, 1.0f);
    p_glClear(0x00004000u /*GL_COLOR_BUFFER_BIT*/);
    return MFP_OK;
}
static void ls_destroy(void* surface) { (void)surface; }
static const MfpLayerSurfaceVTable g_ls_vt = { ls_configure_gl, ls_render, ls_destroy };
static void* ls_create(void* self, const char* config_json) {
    (void)self;
    g_layer_r = config_json ? (float)atoi(config_json) / 255.0f : 0.0f;
    return (void*)&g_layer_r; /* a non-null surface instance handle */
}
static void ls_factory_destroy(void* self) { (void)self; }
static const MfpLayerSurfaceFactoryVTable g_ls_factory_vt = { ls_create, &g_ls_vt, ls_factory_destroy };

int mfp_plugin_register(const MfpHostApi* host, MfpPluginInfo* out_info, MfpRegistrar* reg) {
    if (host->abi_version != MFP_PLUGIN_ABI_VERSION)
        return MFP_ERR_ABI_MISMATCH;

    g_host = host;
    out_info->abi_version  = MFP_PLUGIN_ABI_VERSION;
    out_info->id           = "com.example.testplugin";
    out_info->display_name = "Test Plugin";
    out_info->capabilities = MFP_CAP_VIDEO_SOURCE | MFP_CAP_CONTROL_DECODER | MFP_CAP_AUDIO_BACKEND
                           | MFP_CAP_VIDEO_OUTPUT | MFP_CAP_SUBTITLE | MFP_CAP_LAYER_SURFACE;

    reg->add_media_source_provider(reg->ctx, "testsrc", &g_provider_vt, (void*)&g_provider_vt);
    reg->add_control_decoder(reg->ctx, "test.decoder", &g_decoder_vt, (void*)&g_decoder_vt);
    reg->add_audio_backend(reg->ctx, "testaudio", &g_audio_vt, (void*)&g_audio_vt);
    reg->add_video_output(reg->ctx, "testvout", &g_vout_vt, (void*)&g_vout_vt);
    reg->add_subtitle_provider(reg->ctx, "testsub", &g_sub_provider_vt, (void*)&g_sub_provider_vt);
    reg->add_layer_surface(reg->ctx, "testlayer", &g_ls_factory_vt, (void*)&g_ls_factory_vt);
    return MFP_OK;
}
