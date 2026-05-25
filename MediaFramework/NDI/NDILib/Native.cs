using System.Runtime.InteropServices;
using NDILib.Runtime;

namespace NDILib;

internal static partial class Native
{
    private const string LibraryName = NDILibraryNames.Default;

    // ------------------------------------------------------------------
    // Initialisation
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_initialize();

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_destroy();

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_version();

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_is_supported_CPU();

    // ------------------------------------------------------------------
    // Find
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_find_create_v2(in NDIFindCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_find_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_find_get_current_sources(nint p_instance, out uint p_no_sources);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_find_wait_for_sources(nint p_instance, uint timeout_in_ms);

    // ------------------------------------------------------------------
    // Receive
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_create_v3(in NDIRecvCreateV3 p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_connect(nint p_instance, in NDISourceRef p_src);

    // Separate entry point used to pass NULL (disconnect)
    [LibraryImport(LibraryName, EntryPoint = "NDIlib_recv_connect")]
    internal static partial void NDIlib_recv_connect_null(nint p_instance, nint p_src);

    [LibraryImport(LibraryName)]
    internal static partial NDIFrameType NDIlib_recv_capture_v3(
        nint p_instance,
        out NDIVideoFrameV2 p_video_data,
        out NDIAudioFrameV3 p_audio_data,
        out NDIMetadataFrame p_metadata,
        uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_free_video_v2(nint p_instance, in NDIVideoFrameV2 p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_free_audio_v3(nint p_instance, in NDIAudioFrameV3 p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_free_metadata(nint p_instance, in NDIMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_free_string(nint p_instance, nint p_string);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_recv_get_no_connections(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_send_metadata(nint p_instance, in NDIMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_set_tally(nint p_instance, in NDITally p_tally);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_get_performance(
        nint p_instance,
        out NDIRecvPerformance p_total,
        out NDIRecvPerformance p_dropped);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_get_queue(nint p_instance, out NDIRecvQueue p_total);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_clear_connection_metadata(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_add_connection_metadata(nint p_instance, in NDIMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_get_web_control(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_get_source_name(
        nint p_instance,
        out nint p_source_name,
        uint timeout_in_ms);

    // ------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_create(in NDISendCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_video_v2(nint p_instance, in NDIVideoFrameV2 p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_video_async_v2(nint p_instance, in NDIVideoFrameV2 p_video_data);

    // Used to flush a pending async frame — maps to the same entry point with a zeroed struct
    [LibraryImport(LibraryName, EntryPoint = "NDIlib_send_send_video_async_v2")]
    internal static partial void NDIlib_send_flush_async(nint p_instance, nint p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_audio_v3(nint p_instance, in NDIAudioFrameV3 p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_send_metadata(nint p_instance, in NDIMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial NDIFrameType NDIlib_send_capture(
        nint p_instance,
        out NDIMetadataFrame p_metadata,
        uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_free_metadata(nint p_instance, in NDIMetadataFrame p_metadata);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_send_get_no_connections(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_send_get_tally(nint p_instance, out NDITally p_tally, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_set_failover(nint p_instance, in NDISourceRef p_failover_source);

    // Separate entry point used to clear failover (pass NULL)
    [LibraryImport(LibraryName, EntryPoint = "NDIlib_send_set_failover")]
    internal static partial void NDIlib_send_clear_failover(nint p_instance, nint p_failover_source);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_get_source_name(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_clear_connection_metadata(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_add_connection_metadata(nint p_instance, in NDIMetadataFrame p_metadata);

    // ------------------------------------------------------------------
    // FrameSync
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_framesync_create(nint p_receiver);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_capture_video(
        nint p_instance,
        out NDIVideoFrameV2 p_video_data,
        NDIFrameFormatType field_type);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_free_video(nint p_instance, in NDIVideoFrameV2 p_video_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_capture_audio_v2(
        nint p_instance,
        out NDIAudioFrameV3 p_audio_data,
        int sample_rate,
        int no_channels,
        int no_samples);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_framesync_free_audio_v2(nint p_instance, in NDIAudioFrameV3 p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_framesync_audio_queue_depth(nint p_instance);

    // ------------------------------------------------------------------
    // Routing
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_routing_create(in NDIRoutingCreate p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_routing_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_routing_change(nint p_instance, in NDISourceRef p_source);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_routing_clear(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial int NDIlib_routing_get_no_connections(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_routing_get_source_name(nint p_instance);

    // ------------------------------------------------------------------
    // Utility — interleaved audio send helpers
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_send_send_audio_interleaved_16s(
        nint p_instance, in NDIAudioInterleaved16s p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_send_send_audio_interleaved_32s(
        nint p_instance, in NDIAudioInterleaved32s p_audio_data);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_send_send_audio_interleaved_32f(
        nint p_instance, in NDIAudioInterleaved32f p_audio_data);

    // ------------------------------------------------------------------
    // Utility — audio format conversions (v3 / FLTP)
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_to_interleaved_16s_v3(
        in NDIAudioFrameV3 p_src, ref NDIAudioInterleaved16s p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_from_interleaved_16s_v3(
        in NDIAudioInterleaved16s p_src, ref NDIAudioFrameV3 p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_to_interleaved_32s_v3(
        in NDIAudioFrameV3 p_src, ref NDIAudioInterleaved32s p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_from_interleaved_32s_v3(
        in NDIAudioInterleaved32s p_src, ref NDIAudioFrameV3 p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_to_interleaved_32f_v3(
        in NDIAudioFrameV3 p_src, ref NDIAudioInterleaved32f p_dst);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_util_audio_from_interleaved_32f_v3(
        in NDIAudioInterleaved32f p_src, ref NDIAudioFrameV3 p_dst);

    // ------------------------------------------------------------------
    // Utility — video format conversions
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_V210_to_P216(
        in NDIVideoFrameV2 p_src_v210, ref NDIVideoFrameV2 p_dst_p216);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_util_P216_to_V210(
        in NDIVideoFrameV2 p_src_p216, ref NDIVideoFrameV2 p_dst_v210);

    // ------------------------------------------------------------------
    // PTZ Control (Processing.NDI.Recv.ex.h)
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_is_supported(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_zoom(nint p_instance, float zoom_value);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_zoom_speed(nint p_instance, float zoom_speed);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_pan_tilt(nint p_instance, float pan_value, float tilt_value);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_pan_tilt_speed(nint p_instance, float pan_speed, float tilt_speed);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_store_preset(nint p_instance, int preset_no);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_recall_preset(nint p_instance, int preset_no, float speed);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_auto_focus(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_focus(nint p_instance, float focus_value);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_focus_speed(nint p_instance, float focus_speed);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_white_balance_auto(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_white_balance_indoor(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_white_balance_outdoor(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_white_balance_oneshot(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_white_balance_manual(nint p_instance, float red, float blue);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_exposure_auto(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_exposure_manual(nint p_instance, float exposure_level);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_ptz_exposure_manual_v2(nint p_instance, float iris, float gain, float shutter_speed);

    // ------------------------------------------------------------------
    // Receiver Advertiser (Processing.NDI.RecvAdvertiser.h)
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_advertiser_create(in NDIRecvAdvertiserCreate p_create_settings);

    [LibraryImport(LibraryName, EntryPoint = "NDIlib_recv_advertiser_create")]
    internal static partial nint NDIlib_recv_advertiser_create_default(nint p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_advertiser_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_advertiser_add_receiver(
        nint p_instance, nint p_receiver,
        [MarshalAs(UnmanagedType.I1)] bool allow_controlling,
        [MarshalAs(UnmanagedType.I1)] bool allow_monitoring,
        nint p_input_group_name);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_advertiser_del_receiver(nint p_instance, nint p_receiver);

    // ------------------------------------------------------------------
    // Receiver Listener (Processing.NDI.RecvListener.h)
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_listener_create(in NDIRecvListenerCreate p_create_settings);

    [LibraryImport(LibraryName, EntryPoint = "NDIlib_recv_listener_create")]
    internal static partial nint NDIlib_recv_listener_create_default(nint p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_listener_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_listener_is_connected(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_listener_get_server_url(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_listener_get_receivers(nint p_instance, out uint p_num_receivers);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_listener_wait_for_receivers(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_listener_subscribe_events(nint p_instance, nint p_receiver_uuid);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_listener_unsubscribe_events(nint p_instance, nint p_receiver_uuid);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_recv_listener_get_events(nint p_instance, out uint p_num_events, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_recv_listener_free_events(nint p_instance, nint p_events);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_recv_listener_send_connect(nint p_instance, nint p_receiver_uuid, nint p_source_name);

    // ------------------------------------------------------------------
    // Sender Advertiser (Processing.NDI.SendAdvertiser.h)
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_advertiser_create(in NDISendAdvertiserCreate p_create_settings);

    [LibraryImport(LibraryName, EntryPoint = "NDIlib_send_advertiser_create")]
    internal static partial nint NDIlib_send_advertiser_create_default(nint p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_advertiser_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_send_advertiser_add_sender(
        nint p_instance, nint p_sender,
        [MarshalAs(UnmanagedType.I1)] bool allow_monitoring);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_send_advertiser_del_sender(nint p_instance, nint p_sender);

    // ------------------------------------------------------------------
    // Sender Listener (Processing.NDI.SendListener.h)
    // ------------------------------------------------------------------

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_listener_create(in NDISendListenerCreate p_create_settings);

    [LibraryImport(LibraryName, EntryPoint = "NDIlib_send_listener_create")]
    internal static partial nint NDIlib_send_listener_create_default(nint p_create_settings);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_listener_destroy(nint p_instance);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_send_listener_is_connected(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_listener_get_server_url(nint p_instance);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_listener_get_senders(nint p_instance, out uint p_num_senders);

    [LibraryImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static partial bool NDIlib_send_listener_wait_for_senders(nint p_instance, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_listener_subscribe_events(nint p_instance, nint p_sender_uuid);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_listener_unsubscribe_events(nint p_instance, nint p_sender_uuid);

    [LibraryImport(LibraryName)]
    internal static partial nint NDIlib_send_listener_get_events(nint p_instance, out uint p_num_events, uint timeout_in_ms);

    [LibraryImport(LibraryName)]
    internal static partial void NDIlib_send_listener_free_events(nint p_instance, nint p_events);
}
