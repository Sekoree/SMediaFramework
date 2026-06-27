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

int mfp_plugin_register(const MfpHostApi* host, MfpPluginInfo* out_info, MfpRegistrar* reg) {
    if (host->abi_version != MFP_PLUGIN_ABI_VERSION)
        return MFP_ERR_ABI_MISMATCH;

    out_info->abi_version  = MFP_PLUGIN_ABI_VERSION;
    out_info->id           = "com.example.testplugin";
    out_info->display_name = "Test Plugin";
    out_info->capabilities = MFP_CAP_VIDEO_SOURCE | MFP_CAP_CONTROL_DECODER;

    reg->add_media_source_provider(reg->ctx, "testsrc", &g_provider_vt, (void*)&g_provider_vt);
    reg->add_control_decoder(reg->ctx, "test.decoder", &g_decoder_vt, (void*)&g_decoder_vt);
    return MFP_OK;
}
