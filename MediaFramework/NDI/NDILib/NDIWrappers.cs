using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NDILib.Runtime;

namespace NDILib;

// ------------------------------------------------------------------
// NDIFinder
// ------------------------------------------------------------------

/// <summary>Settings for <see cref="NDIFinder"/>.</summary>
public sealed class NDIFinderSettings
{
    /// <summary>Include NDI sources running on the local machine. Default: <see langword="true"/>.</summary>
    public bool ShowLocalSources { get; init; } = true;

    /// <summary>
    /// Filter to a specific NDI group name (e.g. <c>"Public"</c>, <c>"Studio A"</c>).
    /// <see langword="null"/> discovers all groups.
    /// </summary>
    public string? Groups { get; init; }

    /// <summary>
    /// Comma-separated list of extra IP addresses to query for sources on remote subnets.
    /// Example: <c>"192.168.1.10,10.0.0.5"</c>. <see langword="null"/> uses mDNS discovery only.
    /// </summary>
    public string? ExtraIps { get; init; }
}

/// <summary>Discovers NDI sources on the local network.</summary>
public sealed class NDIFinder : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.Finder");
    private nint _instance;

    private NDIFinder(nint instance) => _instance = instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new <see cref="NDIFinder"/>.</summary>
    /// <param name="finder">On success, the created finder. <see langword="null"/> on failure.</param>
    /// <param name="settings">Optional settings. <see langword="null"/> uses defaults.</param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIFinderCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDIFinder? finder, NDIFinderSettings? settings = null)
    {
        finder = null;
        settings ??= new NDIFinderSettings();

        using var groups   = Utf8Buffer.From(settings.Groups);
        using var extraIps = Utf8Buffer.From(settings.ExtraIps);

        var create = new NDIFindCreate
        {
            ShowLocalSources = settings.ShowLocalSources ? (byte)1 : (byte)0,
            PGroups   = groups.Pointer,
            PExtraIps = extraIps.Pointer
        };

        var ptr = Native.NDIlib_find_create_v2(create);
        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDIFinderCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIFinder created (showLocal={ShowLocal}, groups={Groups}, ptr={Ptr})",
                settings.ShowLocalSources, settings.Groups ?? "(all)", NDILibLogging.PtrMeta(ptr));

        finder = new NDIFinder(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    /// <summary>
    /// Blocks until the number of available NDI sources changes, or <paramref name="timeoutMs"/> elapses.
    /// </summary>
    /// <returns><see langword="true"/> if the source list changed; <see langword="false"/> on timeout.</returns>
    public bool WaitForSources(uint timeoutMs) => Native.NDIlib_find_wait_for_sources(_instance, timeoutMs);

    /// <summary>Returns the current snapshot of discovered NDI sources.</summary>
    public NDIDiscoveredSource[] GetCurrentSources()
    {
        var ptr = Native.NDIlib_find_get_current_sources(_instance, out var count);
        if (ptr == nint.Zero || count == 0)
            return [];

        var sourceSize = Marshal.SizeOf<NDISourceRef>();
        var result     = new NDIDiscoveredSource[count];

        for (var i = 0; i < count; i++)
        {
            var sourcePtr = nint.Add(ptr, i * sourceSize);
            var source    = Marshal.PtrToStructure<NDISourceRef>(sourcePtr);
            result[i]     = new NDIDiscoveredSource(source.NDIName ?? string.Empty, source.UrlAddress);
        }

        return result;
    }

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIFinder disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_find_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDIReceiver settings (maps to native NDIRecvCreateV3 / NDIlib_recv_create_v3)
// ------------------------------------------------------------------

/// <summary>
/// Optional creation-time settings for <see cref="NDIReceiver.Create"/> - maps field-for-field onto the SDK’s
/// <c>NDIRecvCreateV3</c> block (see vendor <c>Processing.NDI.Lib.h</c> / equivalent).
/// </summary>
/// <remarks>
/// Defaults favor low-latency tool use (<see cref="Bandwidth"/> = highest, <see cref="ColorFormat"/> = fastest).
/// When the NDI SDK adds receiver options, extend this type and <see cref="NDIReceiver.Create"/> together so hosts
/// can opt in without forked native calls.
/// </remarks>
public sealed class NDIReceiverSettings
{
    /// <summary>Receiver video colour conversion preference (native <c>NDIRecvCreateV3.ColorFormat</c>).</summary>
    public NDIRecvColorFormat ColorFormat    { get; init; } = NDIRecvColorFormat.Fastest;
    /// <summary>Receiver bandwidth / quality trade-off (native <c>NDIRecvCreateV3.Bandwidth</c>).</summary>
    public NDIRecvBandwidth   Bandwidth      { get; init; } = NDIRecvBandwidth.Highest;
    /// <summary>
    /// Whether the receiver accepts interlaced/fielded video. §3.47j / N23: defaults
    /// to <see langword="false"/> because the current framesync path always pulls
    /// <c>Progressive</c> frames - allowing fields is pure bandwidth/CPU waste.
    /// Set to <see langword="true"/> only when integrating directly with the raw
    /// receiver and consuming fielded content.
    /// </summary>
    public bool               AllowVideoFields { get; init; } = false;
    /// <summary>Optional UTF-8 receiver instance name for logs and SDK registration (native <c>NDIRecvCreateV3.p_ndi_recv_name</c>).</summary>
    public string?            ReceiverName   { get; init; }
}

// ------------------------------------------------------------------
// NDIReceiver
// ------------------------------------------------------------------

/// <summary>Receives NDI video, audio, and metadata from a discovered source.</summary>
public sealed class NDIReceiver : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.Receiver");
    private nint _instance;

    private NDIReceiver(nint instance) => _instance = instance;

    /// <summary>The underlying native instance pointer. Used by <see cref="NDIFrameSync"/>.</summary>
    internal nint Instance => _instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new <see cref="NDIReceiver"/>.</summary>
    /// <param name="receiver">On success, the created receiver. <see langword="null"/> on failure.</param>
    /// <param name="settings">Optional settings. <see langword="null"/> uses defaults.</param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIReceiverCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDIReceiver? receiver, NDIReceiverSettings? settings = null)
    {
        receiver = null;
        settings ??= new NDIReceiverSettings();

        using var recvName = Utf8Buffer.From(settings.ReceiverName);

        var create = new NDIRecvCreateV3
        {
            SourceToConnectTo  = default,
            ColorFormat        = settings.ColorFormat,
            Bandwidth          = settings.Bandwidth,
            AllowVideoFields   = settings.AllowVideoFields ? (byte)1 : (byte)0,
            PNDIRecvName       = recvName.Pointer
        };

        var ptr = Native.NDIlib_recv_create_v3(create);
        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDIReceiverCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIReceiver created (name={Name}, colorFmt={ColorFormat}, bw={Bandwidth}, ptr={Ptr})",
                settings.ReceiverName ?? "(default)", settings.ColorFormat, settings.Bandwidth, NDILibLogging.PtrMeta(ptr));

        receiver = new NDIReceiver(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Connection
    // ------------------------------------------------------------------

    /// <summary>Connects to the specified NDI source.</summary>
    public void Connect(in NDIDiscoveredSource source)
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIReceiver connecting to source={Name}", source.Name);

        using var ndiName = Utf8Buffer.From(source.Name);
        using var url     = Utf8Buffer.From(source.UrlAddress);

        var nativeSource = new NDISourceRef { PNDIName = ndiName.Pointer, PUrlAddress = url.Pointer };
        Native.NDIlib_recv_connect(_instance, nativeSource);
    }

    /// <summary>Disconnects from the current source.</summary>
    public void Disconnect()
    {
        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIReceiver disconnecting (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_recv_connect_null(_instance, nint.Zero);
    }

    // ------------------------------------------------------------------
    // Capture
    // ------------------------------------------------------------------

    /// <summary>Captures the next available frame.</summary>
    public NDIFrameType Capture(
        out NDIVideoFrameV2  video,
        out NDIAudioFrameV3  audio,
        out NDIMetadataFrame metadata,
        uint                 timeoutMs,
        bool                 receiveVideo = true,
        bool                 receiveAudio = true,
        bool                 receiveMetadata = true)
    {
        video = default;
        audio = default;
        metadata = default;

        unsafe
        {
            fixed (NDIVideoFrameV2* pVideo = &video)
            fixed (NDIAudioFrameV3* pAudio = &audio)
            fixed (NDIMetadataFrame* pMeta = &metadata)
            {
                var frameType = Native.NDIlib_recv_capture_v3_ptrs(
                    _instance,
                    receiveVideo ? (nint)pVideo : nint.Zero,
                    receiveAudio ? (nint)pAudio : nint.Zero,
                    receiveMetadata ? (nint)pMeta : nint.Zero,
                    timeoutMs);

                if (frameType == NDIFrameType.Error && Logger.IsEnabled(LogLevel.Debug))
                    Logger.LogDebug("NDIReceiver capture returned Error (ptr={Ptr}) - possible stream disconnect",
                        NDILibLogging.PtrMeta(_instance));

                return frameType;
            }
        }
    }

    /// <summary>
    /// Captures the next frame and returns a scope that automatically frees the captured buffer on disposal.
    /// </summary>
    /// <remarks>
    /// Always check <see cref="NDICaptureScope.FrameType"/> before accessing frame data.
    /// </remarks>
    public NDICaptureScope CaptureScoped(uint timeoutMs)
    {
        var frameType = Capture(out var video, out var audio, out var metadata, timeoutMs);
        return new NDICaptureScope(this, frameType, video, audio, metadata);
    }

    // ------------------------------------------------------------------
    // Free buffers
    // ------------------------------------------------------------------

    public void FreeVideo(in NDIVideoFrameV2    frame) => Native.NDIlib_recv_free_video_v2(_instance, frame);
    public void FreeAudio(in NDIAudioFrameV3    frame) => Native.NDIlib_recv_free_audio_v3(_instance, frame);
    public void FreeMetadata(in NDIMetadataFrame frame) => Native.NDIlib_recv_free_metadata(_instance, frame);

    // ------------------------------------------------------------------
    // Metadata upstream
    // ------------------------------------------------------------------

    /// <summary>Sends a metadata message upstream to the connected sender.</summary>
    /// <returns><see langword="true"/> if currently connected; <see langword="false"/> otherwise.</returns>
    public bool SendMetadata(in NDIMetadataFrame frame)
        => Native.NDIlib_recv_send_metadata(_instance, frame);

    // ------------------------------------------------------------------
    // Tally
    // ------------------------------------------------------------------

    /// <summary>
    /// Sets the tally state for this receiver and forwards it to the connected sender.
    /// </summary>
    /// <returns><see langword="true"/> if currently connected.</returns>
    public bool SetTally(bool onProgram, bool onPreview)
    {
        var tally = new NDITally
        {
            OnProgram = onProgram  ? (byte)1 : (byte)0,
            OnPreview = onPreview  ? (byte)1 : (byte)0
        };
        return Native.NDIlib_recv_set_tally(_instance, tally);
    }

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    /// <summary>Returns the number of currently connected senders.</summary>
    public int GetConnectionCount() => Native.NDIlib_recv_get_no_connections(_instance);

    /// <summary>Retrieves total and dropped frame counts since the receiver was created.</summary>
    public void GetPerformance(out NDIRecvPerformance total, out NDIRecvPerformance dropped)
        => Native.NDIlib_recv_get_performance(_instance, out total, out dropped);

    /// <summary>Returns the current internal queue depths.</summary>
    public NDIRecvQueue GetQueue()
    {
        Native.NDIlib_recv_get_queue(_instance, out var queue);
        return queue;
    }

    // ------------------------------------------------------------------
    // Connection metadata
    // ------------------------------------------------------------------

    /// <summary>Clears all connection metadata strings registered for this receiver.</summary>
    public void ClearConnectionMetadata()
        => Native.NDIlib_recv_clear_connection_metadata(_instance);

    /// <summary>
    /// Adds a connection metadata string sent automatically to every new connected sender.
    /// If a sender is already connected it receives this string immediately.
    /// </summary>
    public void AddConnectionMetadata(in NDIMetadataFrame metadata)
        => Native.NDIlib_recv_add_connection_metadata(_instance, metadata);

    // ------------------------------------------------------------------
    // Status queries
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the web control URL for the connected source (e.g. a PTZ camera UI),
    /// or <see langword="null"/> if none is available. Available after <see cref="NDIFrameType.StatusChange"/>.
    /// </summary>
    public string? GetWebControl()
    {
        var ptr = Native.NDIlib_recv_get_web_control(_instance);
        if (ptr == nint.Zero) return null;
        var result = Marshal.PtrToStringUTF8(ptr);
        Native.NDIlib_recv_free_string(_instance, ptr);
        return result;
    }

    /// <summary>
    /// Returns the name of the currently connected NDI source,
    /// or <see langword="null"/> if not connected or unchanged since last call.
    /// </summary>
    /// <param name="timeoutMs">
    /// Time to wait for a source-name change. Use <c>0</c> to poll immediately.
    /// </param>
    public string? GetSourceName(uint timeoutMs = 0)
    {
        if (!Native.NDIlib_recv_get_source_name(_instance, out var ptr, timeoutMs))
            return null;
        if (ptr == nint.Zero)
            return null;
        var result = Marshal.PtrToStringUTF8(ptr);
        Native.NDIlib_recv_free_string(_instance, ptr);
        return result;
    }

    // ------------------------------------------------------------------
    // PTZ Control
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns <see langword="true"/> if the connected source supports PTZ control.
    /// May take a second after connection to stabilize - check after <see cref="NDIFrameType.StatusChange"/>.
    /// </summary>
    public bool IsPtzSupported() => Native.NDIlib_recv_ptz_is_supported(_instance);

    /// <summary>Zoom to an absolute value. 0.0 = zoomed in, 1.0 = zoomed out.</summary>
    public bool PtzZoom(float zoomValue) => Native.NDIlib_recv_ptz_zoom(_instance, zoomValue);

    /// <summary>Zoom at a particular speed. −1.0 = zoom outwards, +1.0 = zoom inwards.</summary>
    public bool PtzZoomSpeed(float zoomSpeed) => Native.NDIlib_recv_ptz_zoom_speed(_instance, zoomSpeed);

    /// <summary>
    /// Set pan and tilt to absolute values.
    /// Pan: −1.0 (left) … 0.0 (center) … +1.0 (right).
    /// Tilt: −1.0 (bottom) … 0.0 (center) … +1.0 (top).
    /// </summary>
    public bool PtzPanTilt(float panValue, float tiltValue)
        => Native.NDIlib_recv_ptz_pan_tilt(_instance, panValue, tiltValue);

    /// <summary>
    /// Set pan and tilt speed.
    /// Pan: −1.0 (moving right) … 0.0 (stopped) … +1.0 (moving left).
    /// Tilt: −1.0 (down) … 0.0 (stopped) … +1.0 (up).
    /// </summary>
    public bool PtzPanTiltSpeed(float panSpeed, float tiltSpeed)
        => Native.NDIlib_recv_ptz_pan_tilt_speed(_instance, panSpeed, tiltSpeed);

    /// <summary>Store the current position, focus, etc. as a preset (0–99).</summary>
    public bool PtzStorePreset(int presetNo) => Native.NDIlib_recv_ptz_store_preset(_instance, presetNo);

    /// <summary>Recall a preset (0–99) at the given speed (0.0 = slow, 1.0 = fast).</summary>
    public bool PtzRecallPreset(int presetNo, float speed)
        => Native.NDIlib_recv_ptz_recall_preset(_instance, presetNo, speed);

    /// <summary>Put the camera in auto-focus mode.</summary>
    public bool PtzAutoFocus() => Native.NDIlib_recv_ptz_auto_focus(_instance);

    /// <summary>Focus to an absolute value. 0.0 = infinity, 1.0 = closest.</summary>
    public bool PtzFocus(float focusValue) => Native.NDIlib_recv_ptz_focus(_instance, focusValue);

    /// <summary>Focus at a particular speed. −1.0 = focus outwards, +1.0 = focus inwards.</summary>
    public bool PtzFocusSpeed(float focusSpeed) => Native.NDIlib_recv_ptz_focus_speed(_instance, focusSpeed);

    /// <summary>Put the camera in auto white balance mode.</summary>
    public bool PtzWhiteBalanceAuto() => Native.NDIlib_recv_ptz_white_balance_auto(_instance);

    /// <summary>Put the camera in indoor white balance.</summary>
    public bool PtzWhiteBalanceIndoor() => Native.NDIlib_recv_ptz_white_balance_indoor(_instance);

    /// <summary>Put the camera in outdoor white balance.</summary>
    public bool PtzWhiteBalanceOutdoor() => Native.NDIlib_recv_ptz_white_balance_outdoor(_instance);

    /// <summary>Use the current brightness to automatically set the current white balance.</summary>
    public bool PtzWhiteBalanceOneshot() => Native.NDIlib_recv_ptz_white_balance_oneshot(_instance);

    /// <summary>
    /// Set the manual camera white balance.
    /// Red: 0.0 (not red) … 1.0 (very red). Blue: 0.0 (not blue) … 1.0 (very blue).
    /// </summary>
    public bool PtzWhiteBalanceManual(float red, float blue)
        => Native.NDIlib_recv_ptz_white_balance_manual(_instance, red, blue);

    /// <summary>Put the camera in auto-exposure mode.</summary>
    public bool PtzExposureAuto() => Native.NDIlib_recv_ptz_exposure_auto(_instance);

    /// <summary>Manually set camera exposure. 0.0 = dark, 1.0 = light.</summary>
    public bool PtzExposureManual(float exposureLevel)
        => Native.NDIlib_recv_ptz_exposure_manual(_instance, exposureLevel);

    /// <summary>
    /// Manually set camera exposure with individual control over iris, gain, and shutter speed.
    /// All values range from 0.0 (dark/slow) to 1.0 (light/fast).
    /// </summary>
    public bool PtzExposureManual(float iris, float gain, float shutterSpeed)
        => Native.NDIlib_recv_ptz_exposure_manual_v2(_instance, iris, gain, shutterSpeed);

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIReceiver disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_recv_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDICaptureScope  (top-level - promoted from NDIReceiver nested class)
// ------------------------------------------------------------------

/// <summary>
/// Represents a single captured NDI frame. The captured buffer is automatically freed when this scope is disposed.
/// </summary>
/// <remarks>
/// <b>Always</b> check <see cref="FrameType"/> before accessing frame data:
/// <list type="bullet">
///   <item><see cref="NDIFrameType.Video"/>    → <see cref="Video"/> is valid.</item>
///   <item><see cref="NDIFrameType.Audio"/>    → <see cref="Audio"/> is valid.</item>
///   <item><see cref="NDIFrameType.Metadata"/> → <see cref="Metadata"/> is valid.</item>
///   <item><see cref="NDIFrameType.None"/>, <see cref="NDIFrameType.Error"/>,
///         <see cref="NDIFrameType.StatusChange"/>, <see cref="NDIFrameType.SourceChange"/>
///         → no frame data; nothing to free.</item>
/// </list>
/// </remarks>
public sealed class NDICaptureScope : IDisposable
{
    private readonly NDIReceiver _receiver;
    private bool _disposed;

    internal NDICaptureScope(
        NDIReceiver    receiver,
        NDIFrameType   frameType,
        NDIVideoFrameV2  video,
        NDIAudioFrameV3  audio,
        NDIMetadataFrame metadata)
    {
        _receiver  = receiver;
        FrameType  = frameType;
        Video      = video;
        Audio      = audio;
        Metadata   = metadata;
    }

    public NDIFrameType      FrameType { get; }
    public NDIVideoFrameV2   Video     { get; }
    public NDIAudioFrameV3   Audio     { get; }
    public NDIMetadataFrame  Metadata  { get; }

    public void Dispose()
    {
        if (_disposed) return;
        switch (FrameType)
        {
            case NDIFrameType.Video:    _receiver.FreeVideo(Video);       break;
            case NDIFrameType.Audio:    _receiver.FreeAudio(Audio);       break;
            case NDIFrameType.Metadata: _receiver.FreeMetadata(Metadata); break;
        }
        _disposed = true;
    }
}

// ------------------------------------------------------------------
// NDISender
// ------------------------------------------------------------------

/// <summary>Sends NDI video, audio, and metadata to the network.</summary>
public sealed class NDISender : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.Sender");
    private nint _instance;

    private NDISender(nint instance) => _instance = instance;

    /// <summary>Internal accessor for <see cref="NDIAudioUtils"/>.</summary>
    internal nint InstanceInternal => _instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new <see cref="NDISender"/>.</summary>
    /// <param name="sender">On success, the created sender. <see langword="null"/> on failure.</param>
    /// <param name="senderName">Human-readable source name visible to NDI receivers. <see langword="null"/> uses the application name.</param>
    /// <param name="groups">NDI group membership. <see langword="null"/> uses the default group.</param>
    /// <param name="clockVideo">Rate-limit video sends to match the declared frame rate.</param>
    /// <param name="clockAudio">Rate-limit audio sends to match the declared sample rate.</param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDISenderCreateFailed"/></c> on failure.</returns>
    /// <remarks>
    /// Only the clock bits in <see cref="NDISendCreate"/> are passed through today. Hosts that want wall-clock
    /// spacing between video frames instead of SDK pacing usually create the sender with <paramref name="clockVideo"/> set to
    /// <see langword="false"/> and throttle <c>SendVideoAsync</c> in the hosting layer.
    /// </remarks>
    public static int Create(
        out NDISender? sender,
        string?        senderName = null,
        string?        groups     = null,
        bool           clockVideo = true,
        bool           clockAudio = true)
    {
        sender = null;

        using var ndiName   = Utf8Buffer.From(senderName);
        using var groupList = Utf8Buffer.From(groups);

        var create = new NDISendCreate
        {
            PNDIName   = ndiName.Pointer,
            PGroups    = groupList.Pointer,
            ClockVideo = clockVideo ? (byte)1 : (byte)0,
            ClockAudio = clockAudio ? (byte)1 : (byte)0
        };

        var ptr = Native.NDIlib_send_create(create);
        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDISenderCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDISender created (name={Name}, clockVideo={ClockVideo}, clockAudio={ClockAudio}, ptr={Ptr})",
                senderName ?? "(default)", clockVideo, clockAudio, NDILibLogging.PtrMeta(ptr));

        sender = new NDISender(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Send - video
    // ------------------------------------------------------------------

    /// <summary>Sends a video frame synchronously (blocks until clocked if <c>clockVideo</c> is set).</summary>
    public void SendVideo(in NDIVideoFrameV2 frame) => Native.NDIlib_send_send_video_v2(_instance, frame);

    /// <summary>
    /// Schedules a video frame for asynchronous sending. Returns immediately; the frame buffer
    /// must remain valid until the next call to <see cref="SendVideo"/>, <see cref="SendVideoAsync"/>,
    /// <see cref="FlushAsync"/>, or <see cref="Dispose"/>.
    /// </summary>
    public void SendVideoAsync(in NDIVideoFrameV2 frame)
        => Native.NDIlib_send_send_video_async_v2(_instance, frame);

    /// <summary>
    /// Synchronizes a pending asynchronous video send using the documented sync point:
    /// synchronous <c>NDIlib_send_send_video_v2</c> with null video data.
    /// </summary>
    public void SynchronizeAsyncVideo() => Native.NDIlib_send_send_video_v2_null(_instance, nint.Zero);

    /// <summary>Flushes any pending asynchronous video frame submitted via <see cref="SendVideoAsync"/>.</summary>
    public void FlushAsync() => SynchronizeAsyncVideo();

    // ------------------------------------------------------------------
    // Send - audio / metadata
    // ------------------------------------------------------------------

    public void SendAudio(in NDIAudioFrameV3    frame) => Native.NDIlib_send_send_audio_v3(_instance, frame);
    public void SendMetadata(in NDIMetadataFrame frame) => Native.NDIlib_send_send_metadata(_instance, frame);

    // ------------------------------------------------------------------
    // Receive metadata from connected receivers
    // ------------------------------------------------------------------

    /// <summary>
    /// Receives a metadata message sent upstream by a connected receiver (e.g. PTZ commands).
    /// </summary>
    public NDIFrameType CaptureMetadata(out NDIMetadataFrame metadata, uint timeoutMs)
        => Native.NDIlib_send_capture(_instance, out metadata, timeoutMs);

    /// <summary>Frees a metadata frame previously received via <see cref="CaptureMetadata"/>.</summary>
    public void FreeMetadata(in NDIMetadataFrame frame) => Native.NDIlib_send_free_metadata(_instance, frame);

    // ------------------------------------------------------------------
    // Tally
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the current aggregate tally from all connected receivers.
    /// </summary>
    /// <param name="tally">The current tally state on return.</param>
    /// <param name="timeoutMs">Time to wait for a tally change. Use <c>0</c> to poll immediately.</param>
    /// <returns><see langword="true"/> if tally changed; <see langword="false"/> if it timed out.</returns>
    public bool GetTally(out NDITally tally, uint timeoutMs = 0)
        => Native.NDIlib_send_get_tally(_instance, out tally, timeoutMs);

    // ------------------------------------------------------------------
    // Diagnostics
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the number of receivers currently connected to this sender.
    /// Specify a non-zero <paramref name="timeoutMs"/> to wait until at least one receiver connects.
    /// </summary>
    public int GetConnectionCount(uint timeoutMs = 0)
        => Native.NDIlib_send_get_no_connections(_instance, timeoutMs);

    // ------------------------------------------------------------------
    // Source name
    // ------------------------------------------------------------------

    /// <summary>Returns the NDI source name as advertised to the network.</summary>
    public string? GetSourceName()
    {
        var ptr = Native.NDIlib_send_get_source_name(_instance);
        if (ptr == nint.Zero) return null;
        var source = Marshal.PtrToStructure<NDISourceRef>(ptr);
        return source.NDIName;
    }

    // ------------------------------------------------------------------
    // Failover
    // ------------------------------------------------------------------

    /// <summary>
    /// Specifies a fallback NDI source that receivers switch to if this sender goes offline.
    /// </summary>
    public void SetFailover(in NDIDiscoveredSource source)
    {
        using var name = Utf8Buffer.From(source.Name);
        using var url  = Utf8Buffer.From(source.UrlAddress);
        var s = new NDISourceRef { PNDIName = name.Pointer, PUrlAddress = url.Pointer };
        Native.NDIlib_send_set_failover(_instance, s);
    }

    /// <summary>Clears any previously registered failover source.</summary>
    public void ClearFailover() => Native.NDIlib_send_clear_failover(_instance, nint.Zero);

    // ------------------------------------------------------------------
    // Connection metadata
    // ------------------------------------------------------------------

    /// <summary>Clears all connection metadata strings registered for this sender.</summary>
    public void ClearConnectionMetadata()
        => Native.NDIlib_send_clear_connection_metadata(_instance);

    /// <summary>
    /// Adds a connection metadata string sent automatically to every new receiver.
    /// If a receiver is already connected it receives this string immediately.
    /// </summary>
    public void AddConnectionMetadata(in NDIMetadataFrame metadata)
        => Native.NDIlib_send_add_connection_metadata(_instance, metadata);

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDISender disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_send_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDIFrameSync
// ------------------------------------------------------------------

/// <summary>
/// Time-base corrector that converts a push-mode <see cref="NDIReceiver"/> into a
/// pull-mode source, dynamically resampling audio to match the host clock.
/// </summary>
public sealed class NDIFrameSync : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.FrameSync");
    private nint _instance;

    private NDIFrameSync(nint instance) => _instance = instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a frame synchronizer backed by the given receiver.</summary>
    /// <param name="frameSync">On success, the created frame sync. <see langword="null"/> on failure.</param>
    /// <param name="receiver">The receiver to attach the frame sync to.</param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIFrameSyncCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDIFrameSync? frameSync, NDIReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        frameSync = null;

        var ptr = Native.NDIlib_framesync_create(receiver.Instance);
        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDIFrameSyncCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIFrameSync created (ptr={Ptr})", NDILibLogging.PtrMeta(ptr));

        frameSync = new NDIFrameSync(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Video
    // ------------------------------------------------------------------

    /// <summary>
    /// Pulls a video frame, always returning immediately.
    /// If no frame has been received yet, returns an all-zero frame (check <see cref="NDIVideoFrameV2.Xres"/> == 0).
    /// </summary>
    public void CaptureVideo(out NDIVideoFrameV2 frame, NDIFrameFormatType fieldType = NDIFrameFormatType.Progressive)
        => Native.NDIlib_framesync_capture_video(_instance, out frame, fieldType);

    public void FreeVideo(in NDIVideoFrameV2 frame)
        => Native.NDIlib_framesync_free_video(_instance, frame);

    // ------------------------------------------------------------------
    // Audio
    // ------------------------------------------------------------------

    /// <summary>
    /// Pulls audio samples from the frame-sync queue at the requested format.
    /// Always returns immediately, inserting silence if not enough data is available.
    /// Pass <c>0</c> for all parameters to query the current incoming audio format.
    /// </summary>
    public void CaptureAudio(out NDIAudioFrameV3 frame, int sampleRate, int channels, int samples)
        => Native.NDIlib_framesync_capture_audio_v2(_instance, out frame, sampleRate, channels, samples);

    public void FreeAudio(in NDIAudioFrameV3 frame)
        => Native.NDIlib_framesync_free_audio_v2(_instance, frame);

    /// <summary>
    /// Returns the approximate number of audio samples currently available in the queue.
    /// Treat as advisory - the frame-sync dynamically resamples audio.
    /// </summary>
    public int AudioQueueDepth() => Native.NDIlib_framesync_audio_queue_depth(_instance);

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIFrameSync disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_framesync_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDIRouter
// ------------------------------------------------------------------

/// <summary>
/// A virtual NDI source that transparently redirects connected receivers to another source.
/// Useful for router/switcher workflows where the source can be changed without receivers
/// needing to reconnect.
/// </summary>
public sealed class NDIRouter : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.Router");
    private nint _instance;

    private NDIRouter(nint instance) => _instance = instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new NDI routing source.</summary>
    /// <param name="router">On success, the created router. <see langword="null"/> on failure.</param>
    /// <param name="name">The NDI source name to advertise to receivers.</param>
    /// <param name="groups">NDI group membership. <see langword="null"/> uses the default group.</param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIRouterCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDIRouter? router, string name, string? groups = null)
    {
        router = null;

        using var ndiName   = Utf8Buffer.From(name);
        using var groupList = Utf8Buffer.From(groups);

        var create = new NDIRoutingCreate
        {
            PNDIName = ndiName.Pointer,
            PGroups  = groupList.Pointer
        };

        var ptr = Native.NDIlib_routing_create(create);
        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDIRouterCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIRouter created (name={Name}, ptr={Ptr})", name, NDILibLogging.PtrMeta(ptr));

        router = new NDIRouter(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Routing
    // ------------------------------------------------------------------

    /// <summary>
    /// Redirects all connected receivers to <paramref name="source"/>.
    /// </summary>
    /// <returns><see langword="true"/> on success.</returns>
    public bool Change(in NDIDiscoveredSource source)
    {
        using var name = Utf8Buffer.From(source.Name);
        using var url  = Utf8Buffer.From(source.UrlAddress);
        var s = new NDISourceRef { PNDIName = name.Pointer, PUrlAddress = url.Pointer };
        return Native.NDIlib_routing_change(_instance, s);
    }

    /// <summary>Clears the current route, disconnecting all receivers from any source.</summary>
    /// <returns><see langword="true"/> on success.</returns>
    public bool Clear() => Native.NDIlib_routing_clear(_instance);

    /// <summary>
    /// Returns the number of receivers currently connected to this router.
    /// </summary>
    public int GetConnectionCount(uint timeoutMs = 0)
        => Native.NDIlib_routing_get_no_connections(_instance, timeoutMs);

    /// <summary>Returns the NDI source name advertised to the network by this router.</summary>
    public string? GetSourceName()
    {
        var ptr = Native.NDIlib_routing_get_source_name(_instance);
        if (ptr == nint.Zero) return null;
        var source = Marshal.PtrToStructure<NDISourceRef>(ptr);
        return source.NDIName;
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIRouter disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_routing_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDIAudioUtils - static helpers for interleaved audio conversion
// ------------------------------------------------------------------

/// <summary>
/// Utility methods for converting between NDI's native planar FLTP format and common
/// interleaved audio formats. Wraps <c>NDIlib_util_*</c>.
/// </summary>
public static class NDIAudioUtils
{
    // Sender convenience overloads - send interleaved directly without manual conversion

    public static void SendInterleaved16s(NDISender sender, in NDIAudioInterleaved16s frame)
        => Native.NDIlib_util_send_send_audio_interleaved_16s(GetSenderInstance(sender), frame);

    public static void SendInterleaved32s(NDISender sender, in NDIAudioInterleaved32s frame)
        => Native.NDIlib_util_send_send_audio_interleaved_32s(GetSenderInstance(sender), frame);

    public static void SendInterleaved32f(NDISender sender, in NDIAudioInterleaved32f frame)
        => Native.NDIlib_util_send_send_audio_interleaved_32f(GetSenderInstance(sender), frame);

    // Conversion helpers

    /// <summary>Converts a planar FLTP audio frame to interleaved 16-bit signed integer.</summary>
    public static bool ToInterleaved16s(in NDIAudioFrameV3 src, ref NDIAudioInterleaved16s dst)
        => Native.NDIlib_util_audio_to_interleaved_16s_v3(src, ref dst);

    /// <summary>Converts an interleaved 16-bit frame to planar FLTP. Destination <c>FourCC</c> must be <see cref="NDIFourCCAudioType.Fltp"/>.</summary>
    public static bool FromInterleaved16s(in NDIAudioInterleaved16s src, ref NDIAudioFrameV3 dst)
        => Native.NDIlib_util_audio_from_interleaved_16s_v3(src, ref dst);

    /// <summary>Converts a planar FLTP audio frame to interleaved 32-bit signed integer.</summary>
    public static bool ToInterleaved32s(in NDIAudioFrameV3 src, ref NDIAudioInterleaved32s dst)
        => Native.NDIlib_util_audio_to_interleaved_32s_v3(src, ref dst);

    /// <summary>Converts an interleaved 32-bit frame to planar FLTP.</summary>
    public static bool FromInterleaved32s(in NDIAudioInterleaved32s src, ref NDIAudioFrameV3 dst)
        => Native.NDIlib_util_audio_from_interleaved_32s_v3(src, ref dst);

    /// <summary>Converts a planar FLTP audio frame to interleaved 32-bit float.</summary>
    public static bool ToInterleaved32f(in NDIAudioFrameV3 src, ref NDIAudioInterleaved32f dst)
        => Native.NDIlib_util_audio_to_interleaved_32f_v3(src, ref dst);

    /// <summary>Converts an interleaved 32-bit float frame to planar FLTP.</summary>
    public static bool FromInterleaved32f(in NDIAudioInterleaved32f src, ref NDIAudioFrameV3 dst)
        => Native.NDIlib_util_audio_from_interleaved_32f_v3(src, ref dst);

    // Video format conversions

    /// <summary>Converts a 10-bit packed V210 video frame to 16-bit semi-planar P216.</summary>
    public static void V210ToP216(in NDIVideoFrameV2 src, ref NDIVideoFrameV2 dst)
        => Native.NDIlib_util_V210_to_P216(src, ref dst);

    /// <summary>Converts a 16-bit semi-planar P216 video frame to 10-bit packed V210.</summary>
    public static void P216ToV210(in NDIVideoFrameV2 src, ref NDIVideoFrameV2 dst)
        => Native.NDIlib_util_P216_to_V210(src, ref dst);

    // Internal helper to extract the native instance pointer from an NDISender
    // without exposing it publicly on NDISender.
    private static nint GetSenderInstance(NDISender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        return sender.InstanceInternal;
    }
}

// ------------------------------------------------------------------
// NDIRecvAdvertiser - Advertises receivers to an NDI Discovery Server
// ------------------------------------------------------------------

/// <summary>
/// Advertises receivers to an NDI Discovery Server for centralized monitoring and control.
/// Requires an NDI Discovery Server to be running and accessible.
/// </summary>
public sealed class NDIRecvAdvertiser : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.RecvAdvertiser");
    private nint _instance;

    private NDIRecvAdvertiser(nint instance) => _instance = instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new <see cref="NDIRecvAdvertiser"/>.</summary>
    /// <param name="advertiser">On success, the created advertiser. <see langword="null"/> on failure.</param>
    /// <param name="discoveryServerUrl">
    /// URL of the NDI Discovery Server (e.g. <c>"127.0.0.1:5959"</c>).
    /// <see langword="null"/> uses the default Discovery Server.
    /// </param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIRecvAdvertiserCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDIRecvAdvertiser? advertiser, string? discoveryServerUrl = null)
    {
        advertiser = null;

        nint ptr;
        if (discoveryServerUrl is not null)
        {
            using var url = Utf8Buffer.From(discoveryServerUrl);
            var create = new NDIRecvAdvertiserCreate { PUrlAddress = url.Pointer };
            ptr = Native.NDIlib_recv_advertiser_create(create);
        }
        else
        {
            ptr = Native.NDIlib_recv_advertiser_create_default(nint.Zero);
        }

        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDIRecvAdvertiserCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIRecvAdvertiser created (url={Url}, ptr={Ptr})",
                discoveryServerUrl ?? "(default)", NDILibLogging.PtrMeta(ptr));

        advertiser = new NDIRecvAdvertiser(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds a receiver to the list of receivers being advertised on the Discovery Server.
    /// </summary>
    /// <param name="receiver">The receiver to advertise.</param>
    /// <param name="allowControlling">Allow remote control of this receiver.</param>
    /// <param name="allowMonitoring">Allow remote monitoring of this receiver.</param>
    /// <param name="inputGroupName">Optional input group name.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> if already registered.</returns>
    public bool AddReceiver(NDIReceiver receiver, bool allowControlling = true, bool allowMonitoring = true, string? inputGroupName = null)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        using var groupName = Utf8Buffer.From(inputGroupName);
        return Native.NDIlib_recv_advertiser_add_receiver(_instance, receiver.Instance, allowControlling, allowMonitoring, groupName.Pointer);
    }

    /// <summary>
    /// Removes a receiver from the advertised list.
    /// </summary>
    /// <returns><see langword="true"/> on success; <see langword="false"/> if not previously registered.</returns>
    public bool RemoveReceiver(NDIReceiver receiver)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        return Native.NDIlib_recv_advertiser_del_receiver(_instance, receiver.Instance);
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIRecvAdvertiser disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_recv_advertiser_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDIRecvListener - Discovers advertised receivers on the Discovery Server
// ------------------------------------------------------------------

/// <summary>
/// Discovers advertised receivers on the NDI Discovery Server and allows remote control.
/// Requires an NDI Discovery Server to be running and accessible.
/// </summary>
public sealed class NDIRecvListener : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.RecvListener");
    private nint _instance;

    private NDIRecvListener(nint instance) => _instance = instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new <see cref="NDIRecvListener"/>.</summary>
    /// <param name="listener">On success, the created listener. <see langword="null"/> on failure.</param>
    /// <param name="discoveryServerUrl">
    /// URL of the NDI Discovery Server (e.g. <c>"127.0.0.1:5959"</c>).
    /// <see langword="null"/> uses the default Discovery Server.
    /// </param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDIRecvListenerCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDIRecvListener? listener, string? discoveryServerUrl = null)
    {
        listener = null;

        nint ptr;
        if (discoveryServerUrl is not null)
        {
            using var url = Utf8Buffer.From(discoveryServerUrl);
            var create = new NDIRecvListenerCreate { PUrlAddress = url.Pointer };
            ptr = Native.NDIlib_recv_listener_create(create);
        }
        else
        {
            ptr = Native.NDIlib_recv_listener_create_default(nint.Zero);
        }

        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDIRecvListenerCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIRecvListener created (url={Url}, ptr={Ptr})",
                discoveryServerUrl ?? "(default)", NDILibLogging.PtrMeta(ptr));

        listener = new NDIRecvListener(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------------

    /// <summary>Returns <see langword="true"/> if actively connected to the configured NDI Discovery Server.</summary>
    public bool IsConnected => Native.NDIlib_recv_listener_is_connected(_instance);

    /// <summary>Returns the URL of the connected Discovery Server, or <see langword="null"/> if not connected.</summary>
    public string? GetServerUrl()
    {
        var ptr = Native.NDIlib_recv_listener_get_server_url(_instance);
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    // ------------------------------------------------------------------
    // Receiver discovery
    // ------------------------------------------------------------------

    /// <summary>Returns the current list of advertised receivers on the Discovery Server.</summary>
    /// <remarks>
    /// Do not call this concurrently from multiple threads on the same listener instance.
    /// The returned data is copied; the native memory is only valid until the next call.
    /// </remarks>
    public NDIDiscoveredReceiver[] GetReceivers()
    {
        var ptr = Native.NDIlib_recv_listener_get_receivers(_instance, out var count);
        if (ptr == nint.Zero || count == 0)
            return [];

        var structSize = Marshal.SizeOf<NDIReceiverRef>();
        var result = new NDIDiscoveredReceiver[count];

        for (var i = 0; i < count; i++)
        {
            var itemPtr = nint.Add(ptr, i * structSize);
            var r = Marshal.PtrToStructure<NDIReceiverRef>(itemPtr);

            // Read streams array
            var streams = new NDIReceiverType[r.NumStreams];
            for (var s = 0; s < r.NumStreams; s++)
                streams[s] = (NDIReceiverType)Marshal.ReadInt32(r.PStreams, s * sizeof(int));

            // Read commands array
            var commands = new NDIReceiverCommand[r.NumCommands];
            for (var c = 0; c < r.NumCommands; c++)
                commands[c] = (NDIReceiverCommand)Marshal.ReadInt32(r.PCommands, c * sizeof(int));

            result[i] = new NDIDiscoveredReceiver(
                r.Uuid ?? string.Empty, r.Name, r.InputUuid, r.InputName, r.Address,
                streams, commands, r.EventsSubscribed != 0);
        }

        return result;
    }

    /// <summary>
    /// Blocks until the number of advertised receivers changes, or <paramref name="timeoutMs"/> elapses.
    /// </summary>
    /// <returns><see langword="true"/> if the receiver list changed; <see langword="false"/> on timeout.</returns>
    public bool WaitForReceivers(uint timeoutMs) => Native.NDIlib_recv_listener_wait_for_receivers(_instance, timeoutMs);

    // ------------------------------------------------------------------
    // Event subscriptions
    // ------------------------------------------------------------------

    /// <summary>Subscribe to events from a specific receiver identified by UUID.</summary>
    public void SubscribeEvents(string receiverUuid)
    {
        using var uuid = Utf8Buffer.From(receiverUuid);
        Native.NDIlib_recv_listener_subscribe_events(_instance, uuid.Pointer);
    }

    /// <summary>Unsubscribe from events from a specific receiver identified by UUID.</summary>
    public void UnsubscribeEvents(string receiverUuid)
    {
        using var uuid = Utf8Buffer.From(receiverUuid);
        Native.NDIlib_recv_listener_unsubscribe_events(_instance, uuid.Pointer);
    }

    /// <summary>
    /// Returns pending events in the order they were received.
    /// Use <c>0</c> for <paramref name="timeoutMs"/> to poll immediately.
    /// </summary>
    public NDIListenerEvent[] GetEvents(uint timeoutMs = 0)
    {
        var ptr = Native.NDIlib_recv_listener_get_events(_instance, out var count, timeoutMs);
        if (ptr == nint.Zero || count == 0)
            return [];

        var structSize = Marshal.SizeOf<NDIListenerEvent>();
        var result = new NDIListenerEvent[count];
        for (var i = 0; i < count; i++)
        {
            var itemPtr = nint.Add(ptr, i * structSize);
            result[i] = Marshal.PtrToStructure<NDIListenerEvent>(itemPtr);
        }

        Native.NDIlib_recv_listener_free_events(_instance, ptr);
        return result;
    }

    // ------------------------------------------------------------------
    // Remote control
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends a "connect" command to the specified receiver, telling it to connect to <paramref name="sourceName"/>.
    /// Pass <see langword="null"/> for <paramref name="sourceName"/> to disconnect the receiver.
    /// </summary>
    /// <remarks>Only call this for receivers that have <see cref="NDIReceiverCommand.Connect"/> in their commands list.</remarks>
    /// <returns><see langword="true"/> on success.</returns>
    public bool SendConnect(string receiverUuid, string? sourceName)
    {
        using var uuid = Utf8Buffer.From(receiverUuid);
        using var name = Utf8Buffer.From(sourceName);
        return Native.NDIlib_recv_listener_send_connect(_instance, uuid.Pointer, name.Pointer);
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDIRecvListener disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_recv_listener_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDISendAdvertiser - Advertises senders to an NDI Discovery Server
// ------------------------------------------------------------------

/// <summary>
/// Advertises senders to an NDI Discovery Server for centralized monitoring.
/// This is distinct from the normal mDNS/Discovery Server advertisement that
/// <c>NDIlib_send_create</c> does automatically - this is strictly for monitoring purposes.
/// </summary>
public sealed class NDISendAdvertiser : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.SendAdvertiser");
    private nint _instance;

    private NDISendAdvertiser(nint instance) => _instance = instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new <see cref="NDISendAdvertiser"/>.</summary>
    /// <param name="advertiser">On success, the created advertiser. <see langword="null"/> on failure.</param>
    /// <param name="discoveryServerUrl">
    /// URL of the NDI Discovery Server (e.g. <c>"127.0.0.1:5959"</c>).
    /// <see langword="null"/> uses the default Discovery Server.
    /// </param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDISendAdvertiserCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDISendAdvertiser? advertiser, string? discoveryServerUrl = null)
    {
        advertiser = null;

        nint ptr;
        if (discoveryServerUrl is not null)
        {
            using var url = Utf8Buffer.From(discoveryServerUrl);
            var create = new NDISendAdvertiserCreate { PUrlAddress = url.Pointer };
            ptr = Native.NDIlib_send_advertiser_create(create);
        }
        else
        {
            ptr = Native.NDIlib_send_advertiser_create_default(nint.Zero);
        }

        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDISendAdvertiserCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDISendAdvertiser created (url={Url}, ptr={Ptr})",
                discoveryServerUrl ?? "(default)", NDILibLogging.PtrMeta(ptr));

        advertiser = new NDISendAdvertiser(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Operations
    // ------------------------------------------------------------------

    /// <summary>
    /// Adds a sender to the list of senders being advertised on the Discovery Server.
    /// </summary>
    /// <param name="sender">The sender to advertise.</param>
    /// <param name="allowMonitoring">Allow remote monitoring of this sender.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> if already registered.</returns>
    public bool AddSender(NDISender sender, bool allowMonitoring = true)
    {
        ArgumentNullException.ThrowIfNull(sender);
        return Native.NDIlib_send_advertiser_add_sender(_instance, sender.InstanceInternal, allowMonitoring);
    }

    /// <summary>
    /// Removes a sender from the advertised list.
    /// </summary>
    /// <returns><see langword="true"/> on success; <see langword="false"/> if not previously registered.</returns>
    public bool RemoveSender(NDISender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        return Native.NDIlib_send_advertiser_del_sender(_instance, sender.InstanceInternal);
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDISendAdvertiser disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_send_advertiser_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// NDISendListener - Discovers advertised senders on the Discovery Server
// ------------------------------------------------------------------

/// <summary>
/// Discovers advertised senders on the NDI Discovery Server and allows event subscriptions.
/// Requires an NDI Discovery Server to be running and accessible.
/// </summary>
public sealed class NDISendListener : IDisposable
{
    private static readonly ILogger Logger = NDILibLogging.GetLogger("NDILib.SendListener");
    private nint _instance;

    private NDISendListener(nint instance) => _instance = instance;

    // ------------------------------------------------------------------
    // Factory
    // ------------------------------------------------------------------

    /// <summary>Creates a new <see cref="NDISendListener"/>.</summary>
    /// <param name="listener">On success, the created listener. <see langword="null"/> on failure.</param>
    /// <param name="discoveryServerUrl">
    /// URL of the NDI Discovery Server (e.g. <c>"127.0.0.1:5959"</c>).
    /// <see langword="null"/> uses the default Discovery Server.
    /// </param>
    /// <returns><c>0</c> on success; <c>(int)<see cref="NDIErrorCode.NDISendListenerCreateFailed"/></c> on failure.</returns>
    public static int Create(out NDISendListener? listener, string? discoveryServerUrl = null)
    {
        listener = null;

        nint ptr;
        if (discoveryServerUrl is not null)
        {
            using var url = Utf8Buffer.From(discoveryServerUrl);
            var create = new NDISendListenerCreate { PUrlAddress = url.Pointer };
            ptr = Native.NDIlib_send_listener_create(create);
        }
        else
        {
            ptr = Native.NDIlib_send_listener_create_default(nint.Zero);
        }

        if (ptr == nint.Zero)
            return (int)NDIErrorCode.NDISendListenerCreateFailed;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDISendListener created (url={Url}, ptr={Ptr})",
                discoveryServerUrl ?? "(default)", NDILibLogging.PtrMeta(ptr));

        listener = new NDISendListener(ptr);
        return 0;
    }

    // ------------------------------------------------------------------
    // Connection status
    // ------------------------------------------------------------------

    /// <summary>Returns <see langword="true"/> if actively connected to the configured NDI Discovery Server.</summary>
    public bool IsConnected => Native.NDIlib_send_listener_is_connected(_instance);

    /// <summary>Returns the URL of the connected Discovery Server, or <see langword="null"/> if not connected.</summary>
    public string? GetServerUrl()
    {
        var ptr = Native.NDIlib_send_listener_get_server_url(_instance);
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    // ------------------------------------------------------------------
    // Sender discovery
    // ------------------------------------------------------------------

    /// <summary>Returns the current list of advertised senders on the Discovery Server.</summary>
    /// <remarks>
    /// Do not call this concurrently from multiple threads on the same listener instance.
    /// The returned data is copied; the native memory is only valid until the next call.
    /// </remarks>
    public NDIDiscoveredSender[] GetSenders()
    {
        var ptr = Native.NDIlib_send_listener_get_senders(_instance, out var count);
        if (ptr == nint.Zero || count == 0)
            return [];

        var structSize = Marshal.SizeOf<NDISenderRef>();
        var result = new NDIDiscoveredSender[count];

        for (var i = 0; i < count; i++)
        {
            var itemPtr = nint.Add(ptr, i * structSize);
            var s = Marshal.PtrToStructure<NDISenderRef>(itemPtr);

            // Read groups array (array of const char* pointers)
            var groups = new string[s.NumGroups];
            for (var g = 0; g < s.NumGroups; g++)
            {
                var groupStrPtr = Marshal.ReadIntPtr(s.PGroups, g * nint.Size);
                groups[g] = Marshal.PtrToStringUTF8(groupStrPtr) ?? string.Empty;
            }

            result[i] = new NDIDiscoveredSender(
                s.Uuid ?? string.Empty, s.Name, s.Metadata, s.Address, s.Port,
                groups, s.EventsSubscribed != 0);
        }

        return result;
    }

    /// <summary>
    /// Blocks until the number of advertised senders changes, or <paramref name="timeoutMs"/> elapses.
    /// </summary>
    /// <returns><see langword="true"/> if the sender list changed; <see langword="false"/> on timeout.</returns>
    public bool WaitForSenders(uint timeoutMs) => Native.NDIlib_send_listener_wait_for_senders(_instance, timeoutMs);

    // ------------------------------------------------------------------
    // Event subscriptions
    // ------------------------------------------------------------------

    /// <summary>Subscribe to events from a specific sender identified by UUID.</summary>
    public void SubscribeEvents(string senderUuid)
    {
        using var uuid = Utf8Buffer.From(senderUuid);
        Native.NDIlib_send_listener_subscribe_events(_instance, uuid.Pointer);
    }

    /// <summary>Unsubscribe from events from a specific sender identified by UUID.</summary>
    public void UnsubscribeEvents(string senderUuid)
    {
        using var uuid = Utf8Buffer.From(senderUuid);
        Native.NDIlib_send_listener_unsubscribe_events(_instance, uuid.Pointer);
    }

    /// <summary>
    /// Returns pending events in the order they were received.
    /// Use <c>0</c> for <paramref name="timeoutMs"/> to poll immediately.
    /// </summary>
    public NDIListenerEvent[] GetEvents(uint timeoutMs = 0)
    {
        var ptr = Native.NDIlib_send_listener_get_events(_instance, out var count, timeoutMs);
        if (ptr == nint.Zero || count == 0)
            return [];

        var structSize = Marshal.SizeOf<NDIListenerEvent>();
        var result = new NDIListenerEvent[count];
        for (var i = 0; i < count; i++)
        {
            var itemPtr = nint.Add(ptr, i * structSize);
            result[i] = Marshal.PtrToStructure<NDIListenerEvent>(itemPtr);
        }

        Native.NDIlib_send_listener_free_events(_instance, ptr);
        return result;
    }

    // ------------------------------------------------------------------
    // Dispose
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_instance == nint.Zero) return;

        if (Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("NDISendListener disposing (ptr={Ptr})", NDILibLogging.PtrMeta(_instance));

        Native.NDIlib_send_listener_destroy(_instance);
        _instance = nint.Zero;
    }
}

// ------------------------------------------------------------------
// Utf8Buffer (internal helper)
// ------------------------------------------------------------------

internal sealed class Utf8Buffer : IDisposable
{
    private nint _pointer;

    private Utf8Buffer(nint pointer) => _pointer = pointer;

    public nint Pointer => _pointer;

    public static Utf8Buffer From(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return new Utf8Buffer(nint.Zero);
        return new Utf8Buffer(Marshal.StringToCoTaskMemUTF8(value));
    }

    public void Dispose()
    {
        if (_pointer == nint.Zero) return;
        Marshal.FreeCoTaskMem(_pointer);
        _pointer = nint.Zero;
    }
}
