#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include "miniaudio.h"

#if defined(_WIN32)
#define SMA_EXPORT __declspec(dllexport)
#define SMA_CALL __cdecl
#else
#define SMA_EXPORT __attribute__((visibility("default")))
#define SMA_CALL
#endif

typedef void(SMA_CALL *sma_data_callback)(void* user_data, float* output, const float* input, uint32_t frame_count);

typedef struct sma_device_handle
{
    ma_device device;
    sma_data_callback callback;
    void* user_data;
} sma_device_handle;

static void sma_data_callback_proxy(ma_device* device, void* output, const void* input, ma_uint32 frame_count)
{
    sma_device_handle* handle = (sma_device_handle*)device->pUserData;
    if (handle == NULL || handle->callback == NULL) {
        if (output != NULL) {
            memset(output, 0, frame_count * device->playback.channels * sizeof(float));
        }
        return;
    }

    handle->callback(handle->user_data, (float*)output, (const float*)input, frame_count);
}

static int sma_hex_value(char c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

static ma_result sma_parse_device_id(const char* hex, ma_device_id* id, ma_bool32* has_id)
{
    size_t expected_len;
    size_t actual_len;
    unsigned char* dst;
    size_t i;

    if (id == NULL || has_id == NULL) return MA_INVALID_ARGS;
    memset(id, 0, sizeof(*id));
    *has_id = MA_FALSE;

    if (hex == NULL || hex[0] == '\0') return MA_SUCCESS;

    expected_len = sizeof(ma_device_id) * 2;
    actual_len = strlen(hex);
    if (actual_len != expected_len) return MA_INVALID_ARGS;

    dst = (unsigned char*)id;
    for (i = 0; i < sizeof(ma_device_id); ++i) {
        int hi = sma_hex_value(hex[i * 2]);
        int lo = sma_hex_value(hex[i * 2 + 1]);
        if (hi < 0 || lo < 0) return MA_INVALID_ARGS;
        dst[i] = (unsigned char)((hi << 4) | lo);
    }

    *has_id = MA_TRUE;
    return MA_SUCCESS;
}

static void sma_write_cstr(const char* value, char* buffer, int buffer_len)
{
    size_t len;
    size_t copy;
    if (buffer == NULL || buffer_len <= 0) return;

    if (value == NULL) {
        buffer[0] = '\0';
        return;
    }

    len = strlen(value);
    copy = len < (size_t)(buffer_len - 1) ? len : (size_t)(buffer_len - 1);
    memcpy(buffer, value, copy);
    buffer[copy] = '\0';
}

static void sma_write_device_id_hex(const ma_device_id* id, char* buffer, int buffer_len)
{
    static const char hex[] = "0123456789abcdef";
    const unsigned char* src;
    size_t required_len;
    size_t i;

    if (buffer == NULL || buffer_len <= 0) return;

    required_len = sizeof(ma_device_id) * 2;
    if ((size_t)buffer_len <= required_len) {
        buffer[0] = '\0';
        return;
    }

    src = (const unsigned char*)id;
    for (i = 0; i < sizeof(ma_device_id); ++i) {
        buffer[i * 2] = hex[(src[i] >> 4) & 0x0F];
        buffer[i * 2 + 1] = hex[src[i] & 0x0F];
    }
    buffer[required_len] = '\0';
}

static void sma_get_device_caps(const ma_device_info* info, uint32_t* max_channels, uint32_t* default_sample_rate)
{
    ma_uint32 max_ch = 0;
    ma_uint32 rate = 0;
    ma_uint32 i;

    if (info != NULL) {
        for (i = 0; i < info->nativeDataFormatCount; ++i) {
            ma_uint32 channels = info->nativeDataFormats[i].channels;
            ma_uint32 sample_rate = info->nativeDataFormats[i].sampleRate;
            if (channels == 0) channels = 2;
            if (channels > max_ch) max_ch = channels;
            if (rate == 0 && sample_rate != 0) rate = sample_rate;
        }
    }

    if (max_ch == 0) max_ch = 2;
    if (rate == 0) rate = 48000;

    if (max_channels != NULL) *max_channels = max_ch;
    if (default_sample_rate != NULL) *default_sample_rate = rate;
}

SMA_EXPORT int SMA_CALL sma_device_id_hex_capacity(void)
{
    return (int)(sizeof(ma_device_id) * 2 + 1);
}

SMA_EXPORT const char* SMA_CALL sma_result_description(int result)
{
    return ma_result_description((ma_result)result);
}

SMA_EXPORT int SMA_CALL sma_context_create(void** out_context)
{
    ma_context* context;
    ma_result result;

    if (out_context == NULL) return MA_INVALID_ARGS;
    *out_context = NULL;

    context = (ma_context*)calloc(1, sizeof(ma_context));
    if (context == NULL) return MA_OUT_OF_MEMORY;

    result = ma_context_init(NULL, 0, NULL, context);
    if (result != MA_SUCCESS) {
        free(context);
        return result;
    }

    *out_context = context;
    return MA_SUCCESS;
}

SMA_EXPORT void SMA_CALL sma_context_destroy(void* context)
{
    if (context == NULL) return;
    ma_context_uninit((ma_context*)context);
    free(context);
}

SMA_EXPORT int SMA_CALL sma_context_device_count(void* context, int device_type, uint32_t* out_count)
{
    ma_device_info* playback_infos = NULL;
    ma_device_info* capture_infos = NULL;
    ma_uint32 playback_count = 0;
    ma_uint32 capture_count = 0;
    ma_result result;

    if (context == NULL || out_count == NULL) return MA_INVALID_ARGS;
    *out_count = 0;

    result = ma_context_get_devices((ma_context*)context, &playback_infos, &playback_count, &capture_infos, &capture_count);
    if (result != MA_SUCCESS) return result;

    if (device_type == ma_device_type_playback) {
        *out_count = playback_count;
    } else if (device_type == ma_device_type_capture) {
        *out_count = capture_count;
    } else {
        return MA_INVALID_ARGS;
    }

    return MA_SUCCESS;
}

SMA_EXPORT int SMA_CALL sma_context_device_get(
    void* context,
    int device_type,
    uint32_t index,
    char* id_buffer,
    int id_len,
    char* name_buffer,
    int name_len,
    uint32_t* out_is_default,
    uint32_t* out_max_channels,
    uint32_t* out_default_sample_rate)
{
    ma_device_info* playback_infos = NULL;
    ma_device_info* capture_infos = NULL;
    ma_uint32 playback_count = 0;
    ma_uint32 capture_count = 0;
    ma_device_info* infos;
    ma_uint32 count;
    ma_device_info info;
    ma_result result;

    if (context == NULL) return MA_INVALID_ARGS;

    result = ma_context_get_devices((ma_context*)context, &playback_infos, &playback_count, &capture_infos, &capture_count);
    if (result != MA_SUCCESS) return result;

    if (device_type == ma_device_type_playback) {
        infos = playback_infos;
        count = playback_count;
    } else if (device_type == ma_device_type_capture) {
        infos = capture_infos;
        count = capture_count;
    } else {
        return MA_INVALID_ARGS;
    }

    if (index >= count) return MA_INVALID_ARGS;

    info = infos[index];
    (void)ma_context_get_device_info((ma_context*)context, (ma_device_type)device_type, &infos[index].id, &info);

    sma_write_device_id_hex(&infos[index].id, id_buffer, id_len);
    sma_write_cstr(infos[index].name, name_buffer, name_len);
    if (out_is_default != NULL) *out_is_default = infos[index].isDefault ? 1u : 0u;
    sma_get_device_caps(&info, out_max_channels, out_default_sample_rate);
    return MA_SUCCESS;
}

SMA_EXPORT int SMA_CALL sma_device_create(
    int device_type,
    const char* device_id_hex,
    uint32_t sample_rate,
    uint32_t channels,
    uint32_t period_size_frames,
    sma_data_callback callback,
    void* user_data,
    void** out_device)
{
    sma_device_handle* handle;
    ma_device_config config;
    ma_device_id id;
    ma_bool32 has_id;
    ma_result result;

    if (out_device == NULL || callback == NULL || sample_rate == 0 || channels == 0) return MA_INVALID_ARGS;
    *out_device = NULL;

    result = sma_parse_device_id(device_id_hex, &id, &has_id);
    if (result != MA_SUCCESS) return result;

    handle = (sma_device_handle*)calloc(1, sizeof(sma_device_handle));
    if (handle == NULL) return MA_OUT_OF_MEMORY;

    handle->callback = callback;
    handle->user_data = user_data;

    config = ma_device_config_init((ma_device_type)device_type);
    config.sampleRate = sample_rate;
    config.periodSizeInFrames = period_size_frames;
    config.dataCallback = sma_data_callback_proxy;
    config.pUserData = handle;

    if (device_type == ma_device_type_playback) {
        config.playback.pDeviceID = has_id ? &id : NULL;
        config.playback.format = ma_format_f32;
        config.playback.channels = channels;
    } else if (device_type == ma_device_type_capture) {
        config.capture.pDeviceID = has_id ? &id : NULL;
        config.capture.format = ma_format_f32;
        config.capture.channels = channels;
    } else {
        free(handle);
        return MA_INVALID_ARGS;
    }

    result = ma_device_init(NULL, &config, &handle->device);
    if (result != MA_SUCCESS) {
        free(handle);
        return result;
    }

    *out_device = handle;
    return MA_SUCCESS;
}

SMA_EXPORT int SMA_CALL sma_device_start(void* device)
{
    if (device == NULL) return MA_INVALID_ARGS;
    return ma_device_start(&((sma_device_handle*)device)->device);
}

SMA_EXPORT int SMA_CALL sma_device_stop(void* device)
{
    if (device == NULL) return MA_INVALID_ARGS;
    return ma_device_stop(&((sma_device_handle*)device)->device);
}

SMA_EXPORT void SMA_CALL sma_device_destroy(void* device)
{
    sma_device_handle* handle = (sma_device_handle*)device;
    if (handle == NULL) return;
    ma_device_uninit(&handle->device);
    free(handle);
}

SMA_EXPORT int SMA_CALL sma_device_is_started(void* device)
{
    if (device == NULL) return 0;
    return ma_device_is_started(&((sma_device_handle*)device)->device) ? 1 : 0;
}

SMA_EXPORT int SMA_CALL sma_device_get_state(void* device)
{
    if (device == NULL) return 0;
    return (int)ma_device_get_state(&((sma_device_handle*)device)->device);
}
