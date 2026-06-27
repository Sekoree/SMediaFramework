/*
 * test_plugin.c — a minimal native MFPlayer plugin (the Phase-6 gate fixture). Compiled to a .so by AbiSmoke and
 * loaded through S.Abi.AbiPluginHost. It exports mfp_plugin_register and registers a media-source provider AND a
 * control decoder. The capability vtables are stubs here — this gate proves the *load + registration* path (the
 * host records the vtable pointers); the managed adapters that actually call the vtables are the next layer.
 *
 * Build: gcc -shared -fPIC -I<S.Abi/include> test_plugin.c -o mfp_test_plugin.so
 */
#include "mfp_plugin.h"

static int provider_can_open(void* self, const char* uri) { (void)self; (void)uri; return 1; }
static int provider_open(void* self, const char* uri, MfpMediaSource* out) {
    (void)uri;
    out->video = self;
    out->audio = 0;
    return MFP_OK;
}
static const MfpVideoSourceVTable g_video_vt; /* zeroed stub */
static const MfpMediaSourceProviderVTable g_provider_vt = {
    provider_can_open, provider_open, &g_video_vt, 0, 0
};

static int decoder_decode(void* self, const char* osc_address, const uint8_t* blob, int blob_len,
                          MfpControlReading* out, int out_cap, int* out_count) {
    (void)self; (void)osc_address; (void)blob; (void)blob_len; (void)out; (void)out_cap;
    *out_count = 0;
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
