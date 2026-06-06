# Dependency Reference Implementation Review - 2026-06-06

## Scope

Reviewed the current `MediaFramework` libraries and `UI/HaPlay` against the
new source/reference material under `Reference/`, especially:

- `Reference/portaudio-19.7.0`
- `Reference/portmidi-2.0.8`
- `Reference/NDI SDK for Linux`
- `Reference/SDL3-CS-master`
- `Reference/TreeDataGrid-12.0.0`
- `Reference/AvaloniaEdit-master`
- `Reference/Avalonia-12.0.4`
- `Reference/FFmpeg.AutoGen-main`
- `Reference/Mond-0.11.2`
- `Reference/OSC_Endpoints`
- `Reference/Silk.NET-2.23.0`
- `Reference/Vortice.Windows-1.9.143`

This pass focused on places where dependency behavior or helper APIs make the
current implementation riskier, more complex, or less efficient than it needs
to be. It is a static review; I did not run the full test suite in this pass.

## Resolution Pass - 2026-06-06 (follow-up)

Most highest-priority findings were already addressed in the codebase before this
follow-up pass. Additional fixes landed in the same session:

| Finding | Status |
| --- | --- |
| PortAudio callback uses `Pa_GetStreamTime` | **Resolved** — callback seeds smooth clock from `PaStreamCallbackTimeInfo.currentTime` |
| PortMidi not serialized | **Resolved** — `PmNativeGate` wraps all `Native.Pm_*` entry points |
| FFmpeg mux packets before header | **Resolved** — `WritePacket` throws when header not written; regression test exists |
| TreeDataGrid matrix editors recycle stale rows | **Resolved** — `AotBinding.TwoWayFromDataContext` + recycled matrix templates |
| Mond instruction limit lifetime-based | **Resolved** — `ResetBudget()` before each `ControlScriptModule.Invoke`; regression test added |
| Cue tree manual drag/drop | **Resolved** — `AutoDragDropRows` + `RowDragStarted` / `RowDrop` |
| AvaloniaEdit full-text reset | **Resolved** — per-script `TextDocument` cache; append-only VM updates preserve caret |
| SDL3 global `SDL_Quit` | **Resolved** — `SDL3Runtime.Release` calls `SDL_QuitSubSystem(Video)` only |
| NDI disabled-stream capture | **Resolved** — `NDIReceiver.Capture` passes null pointers when stream type disabled |
| NDI async flush sync point | **Resolved** — `SynchronizeAsyncVideo()` uses documented sync-null send |
| Audio matrix dialog VM leak | **Resolved** — `OnClosed` unsubscribes and clears TreeDataGrid sources |
| PortMidi host error detail | **Resolved** — `PMUtil.DescribeError` + enriched input/output logging |
| X32 meter blobs in cache | **Resolved** — profile-gated `X32MeterCacheDecoder` in `ControlScriptRuntime` |

Still open (lower urgency): cue-tree stock `TextColumn` vs explicit AOT templates (documented
intentional), X32 protocol-aware periodic task routing refinements, NDI bandwidth defaults.

## Verification Pass - 2026-06-06

I rechecked each finding against the current source tree and the referenced
dependency sources. No original finding had to be removed. Two entries needed
tighter wording:

- The NDI async flush issue is specifically that the current null flush maps to
  the async entry point, while the clearly documented null-pointer sync point is
  the synchronous video send call.
- The cue-tree text-column issue is a cleanup opportunity only if the
  NativeAOT-safe binding path stays explicit; stock TreeDataGrid text-column
  constructors compile expressions in the referenced source.

This verification also added one extra finding: `SDL3Runtime.Release` calls
global `SDL_Quit()` even though the wrapper owns only the SDL video subsystem.

## Highest Priority

### PortAudio output callback calls a forbidden PortAudio API

Repo paths:

- `MediaFramework/Media/S.Media.PortAudio/PortAudioOutput.cs`

Reference:

- `Reference/portaudio-19.7.0/include/portaudio.h`

`PortAudioOutput.Callback` calls `Native.Pa_GetStreamTime(self._stream)` during
the realtime PortAudio callback. The PortAudio header explicitly says that,
except for `Pa_GetStreamCpuLoad()`, PortAudio APIs must not be called from the
stream callback. The same header also says the callback receives
`PaStreamCallbackTimeInfo` values synchronized with `Pa_GetStreamTime()`.

Why it matters: calling into PortAudio from the callback can be backend
dependent and may cause glitches or deadlocks on stricter hosts.

Suggested improvement: use the callback's `timeInfo` pointer to seed the smooth
clock (`currentTime` / `outputBufferDacTime`) instead of calling
`Pa_GetStreamTime()` inside the callback. Keep `Pa_GetStreamTime()` only on
control/non-realtime threads, such as `ElapsedSinceStart`.

### PortMidi native calls are not globally serialized

Repo paths:

- `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIInputDevice.cs`
- `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIOutputDevice.cs`
- `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIDevice.cs`
- `MediaFramework/Control/S.Control/ControlSystemMidiDeviceSessions.cs`
- `MediaFramework/Control/S.Control/ControlMidiLibraryLease.cs`

Reference:

- `Reference/portmidi-2.0.8/pm_common/portmidi.h`

PortMidi allows MIDI work on a different thread than initialization, but the
header states that PortMidi is not thread-safe and callers must not let threads
call PortMidi functions concurrently. The current design can have one or more
input polling threads calling `Pm_Read` while output sends, stream close,
enumeration, initialize, or terminate happen on other threads.

Why it matters: this can create rare native races during live MIDI input,
script-triggered MIDI output, device rebinding, and shutdown.

Suggested improvement: introduce one PortMidi native-call gate or a dedicated
MIDI I/O dispatcher thread. Cover initialize, terminate, enumerate, open, close,
read, write, poll, and host-error queries. Do not hold the gate while invoking
managed event handlers.

### FFmpeg muxer can submit packets before the container header is written

Repo paths:

- `MediaFramework/Media/S.Media.FFmpeg.Encode/Internal/FfmpegMuxContext.cs`
- `MediaFramework/Media/S.Media.FFmpeg.Encode/FFmpegMuxFileOutput.cs`

Reference:

- `Reference/FFmpeg.AutoGen-main`

`FfmpegMuxContext.WritePacket` calls `TryWriteHeaderLocked()`, but if both
audio and video are expected and only one leg has been configured, the header is
still not written and `av_interleaved_write_frame` is called anyway.

Why it matters: callers can open an A/V muxer, configure video first, and submit
a frame before audio is configured. That sends packets to libavformat before
`avformat_write_header`, producing native errors or invalid files.

Suggested improvement: if `_headerWritten` is still false after
`TryWriteHeaderLocked()`, throw a clear managed error such as "all expected
streams must be configured before submit", or buffer packets until the expected
legs are configured. Add a regression test with `IncludeAudio=true` and video
submit before audio configure.

### TreeDataGrid recycled audio matrix editors can stay bound to old rows

Repo paths:

- `UI/HaPlay/Views/Dialogs/AudioMatrixDialog.axaml.cs`
- `UI/HaPlay/Views/AotBinding.cs`

Reference:

- `Reference/TreeDataGrid-12.0.0/site/articles/guides/column-template.md`
- `Reference/TreeDataGrid-12.0.0/src/Avalonia.Controls.TreeDataGrid`

`AudioMatrixDialog` builds `TemplateColumn` cells with
`supportsRecycling: true`, but `BuildCellEditor`, `BuildRouteGainEditor`, and
`BuildRouteMuteEditor` call `AotBinding.TwoWay` with a fixed captured row/cell
source. Unlike `AotBinding.OneWayText`, the two-way helper does not re-resolve
the source on `DataContextChanged`.

Why it matters: when TreeDataGrid recycles an editor visual for another row,
the UI can display or write back to the old matrix cell or route.

Suggested improvement: as a short-term correctness fix, set
`supportsRecycling: false` for these editor templates. The better fix is to add
a DataContext-aware two-way AOT binding helper or use TreeDataGrid template
columns with normal binding expressions once the NativeAOT strategy is settled.

## High Priority

### NDI receiver captures streams that the source has disabled

Repo paths:

- `MediaFramework/NDI/NDILib/Native.cs`
- `MediaFramework/NDI/NDILib/NDIWrappers.cs`
- `MediaFramework/Media/S.Media.NDI/NDISource.cs`

Reference:

- `Reference/NDI SDK for Linux/include/Processing.NDI.Recv.h`
- `Reference/NDI SDK for Linux/include/Processing.NDI.deprecated.h`

The NDI SDK documents that any of the capture output pointers may be `NULL`,
and data of that type will not be captured in that call. `NDISource` exposes
`ReceiveAudio` and `ReceiveVideo`, but `NDIReceiver.Capture` always supplies
video, audio, and metadata buffers, then discards frames for disabled streams.

Why it matters: audio-only or video-only usage still asks the NDI SDK to deliver
unwanted frame types, wasting bandwidth, conversion, allocation, and capture
thread time.

Suggested improvement: add capture overloads that can pass null pointers for
disabled streams. Select narrower `NDIRecvBandwidth` defaults where possible,
for example audio-only when video is disabled.

### NDI async video flush should use an explicit documented wrapper

Repo paths:

- `MediaFramework/NDI/NDILib/Native.cs`
- `MediaFramework/NDI/NDILib/NDIWrappers.cs`
- `MediaFramework/Media/S.Media.NDI/Video/NDIVideoSender.cs`

Reference:

- `Reference/NDI SDK for Linux/include/Processing.NDI.Send.h`
- `Reference/NDI SDK for Linux/include/Processing.NDI.deprecated.h`

The SDK says memory passed to `NDIlib_send_send_video_async` remains owned by
the caller until a synchronizing event. The documented sync points include
another async frame, a synchronous `NDIlib_send_send_video_v2` call with
`p_video_data = NULL`, or destroy. The wrapper's `FlushAsync` currently calls
the async entry point with a null pointer through an `nint` P/Invoke, rather
than exposing the documented synchronous-null send as a named operation.

Why it matters: the ownership boundary is too subtle for sender disposal code
that frees staging buffers. A mistaken null-call variant can become a
use-after-free.

Suggested improvement: expose named wrappers for the documented async submit
and synchronous-null synchronization forms, verify them against the installed
SDK header, and make `NDIVideoSender.Dispose` call the documented sync path
before releasing any buffer that was last submitted asynchronously.

### SDL3 video/event calls run on per-output render threads

Repo paths:

- `MediaFramework/Media/S.Media.SDL3/SDL3Runtime.cs`
- `MediaFramework/Media/S.Media.SDL3/SDL3VideoOutput.cs`
- `MediaFramework/Media/S.Media.SDL3/SDL3GLVideoOutput.cs`

Reference:

- `Reference/SDL3-CS-master/SDL3-CS/SDL/Video/video/PInvoke.cs`
- `Reference/SDL3-CS-master/SDL3-CS.Examples`

The SDL3-CS reference annotates many video/window functions as main-thread-only,
including window creation and display/window APIs. The current SDL outputs can
initialize video, create windows, pump events, and poll events from dedicated
per-output render threads. The comments already acknowledge macOS needs a
main-thread harness, but the runtime still permits the default auto-thread path
without a guard.

Why it matters: this is fragile on macOS and can become surprising in
multi-window/multi-output scenarios where multiple render threads poll the same
global SDL event queue.

Suggested improvement: centralize SDL video initialization and event pumping on
one owner thread, or make the host explicitly choose manual mode for UI apps.
At minimum, add runtime guards/logging for macOS and for multiple auto-thread
SDL outputs polling concurrently.

### SDL3 video runtime releases with global `SDL_Quit`

Repo paths:

- `MediaFramework/Media/S.Media.SDL3/SDL3Runtime.cs`

Reference:

- `Reference/SDL3-CS-master/SDL3-CS/SDL/Basics/init/PInvoke.cs`

`SDL3Runtime` documents a reference-counted lifetime for the SDL video
subsystem and says `SDL_QuitSubSystem` runs when the last holder lets go. The
implementation calls `SDL.Quit()` instead. The SDL3-CS reference describes
`SDL_Quit` as cleanup for all initialized subsystems and notes it is not wise
to call from a library or dynamically loaded code; `SDL_QuitSubSystem` is the
subsystem-specific ref-counted shutdown API.

Why it matters: disposing the last SDL video output can unexpectedly shut down
SDL subsystems owned by the host process or future framework modules, such as
audio, gamepad, camera, or events.

Suggested improvement: pair the video acquire path with
`SDL.QuitSubSystem(SDL.InitFlags.Video)` under the same owner-thread/main-thread
policy. Leave process-wide `SDL.Quit()` to the application or an explicit
whole-SDL owner.

### Win32 NV12 staging upload sets `GL_UNPACK_ROW_LENGTH` in the wrong units

Repo paths:

- `MediaFramework/Media/S.Media.OpenGL/Nv12Win32SharedHandleGpuUploader.cs`

Reference:

- `Reference/Silk.NET-2.23.0`
- `Reference/Vortice.Windows-1.9.143`

`SetUnpackRowLength(rowPitchBytes, logicalRowBytes)` currently writes
`rowPitchBytes / logicalRowBytes`. OpenGL's unpack row length is a row length
in pixels/components, not a ratio of padded pitch to visible row size. For a
1920-wide luma plane with a 2048-byte D3D11 row pitch, the current code sets
`1`, not `2048`.

Why it matters: when the WGL interop path misses and the staging fallback is
used, padded D3D11 rows can upload corrupted NV12 planes after the first row.

Suggested improvement: compute row length per upload format. For `GL_RED` R8,
use `rowPitchBytes`. For `GL_RG` UV, use `rowPitchBytes / 2`. Add pure helper
tests for padded row pitches and an integration path that forces staging.

## Medium Priority

### Audio matrix dialog can leak closed windows through VM events

Repo paths:

- `UI/HaPlay/Views/Dialogs/AudioMatrixDialog.axaml.cs`

Reference:

- `Reference/Avalonia-12.0.4/src/Avalonia.Controls`

The dialog subscribes to `MediaPlayerViewModel.AudioMatrixLayoutChanged` when
the DataContext changes, and unsubscribes only on another DataContext change.
Closing the window does not necessarily clear DataContext.

Why it matters: repeated open/close cycles can keep dialog instances alive and
post UI work to closed windows.

Suggested improvement: override `OnClosed` or `OnDetachedFromVisualTree` and
unsubscribe from `_subscribedVm.AudioMatrixLayoutChanged`, clear `_subscribedVm`,
and optionally clear TreeDataGrid sources.

### Cue tree manually reimplements TreeDataGrid row drag/drop

Repo paths:

- `UI/HaPlay/Views/CuePlayerView.axaml.cs`
- `UI/HaPlay/Views/CuePlayerView.axaml`

Reference:

- `Reference/TreeDataGrid-12.0.0/site/articles/guides/drag-and-drop-rows.md`
- `Reference/TreeDataGrid-12.0.0/src/Avalonia.Controls.TreeDataGrid/TreeDataGrid.cs`
- `Reference/TreeDataGrid-12.0.0/src/Avalonia.Controls.TreeDataGrid/HierarchicalTreeDataGridSource.cs`

`CuePlayerView` tracks pointer press/move, starts `DragDrop.DoDragDropAsync`,
walks visual parents to find target rows, and calculates before/inside/after
placement manually. TreeDataGrid 12 has `AutoDragDropRows`, `RowDragStarted`,
`RowDragOver`, `RowDrop`, drop-position args, drag adorners, and source-level
row movement support.

Why it matters: the custom implementation is more brittle against TreeDataGrid
template and routed-event changes, and it misses built-in drag behavior such as
drop adorners.

Suggested improvement: move internal cue reordering to `AutoDragDropRows` plus
`RowDragOver` / `RowDrop` handlers for cue-specific validation and VM updates.
Keep external file drop handling separate.

### Cue tree plain-text columns can use simpler text cells once the AOT path is explicit

Repo paths:

- `UI/HaPlay/Views/CuePlayerView.axaml.cs`
- `UI/HaPlay/Views/AotBinding.cs`

Reference:

- `Reference/TreeDataGrid-12.0.0/src/Avalonia.Controls.TreeDataGrid/Models/TreeDataGrid/TextColumn.cs`

Most cue columns are read-only text, but they are built as `TemplateColumn` +
`TextBlock` + custom `AotBinding.OneWayText`. The custom helper is careful
about recycling. TreeDataGrid has typed text columns and expander columns for
this common path, but the referenced `TextColumn` constructor path uses
`Expression<Func<...>>` and `ColumnBase` calls `getter.Compile()`.

Why it matters: there is more code and subscription lifecycle to maintain than
needed for plain text cells, but blindly switching to stock expression-based
text columns can reintroduce NativeAOT/dynamic-code risk.

Suggested improvement: either keep the current helper and document it as the
intentional NativeAOT-safe path, or add a small local AOT text column built on
direct delegates plus `TypedBinding`. Only use stock
`TextColumn<CueNodeViewModel, string?>` after validating its expression compile
path under the app's NativeAOT strategy. Keep custom templates for status and
color strip where they add value.

### Script editor should own a TextDocument instead of mirroring Text

Repo paths:

- `UI/HaPlay/Views/ScriptEditorWindow.axaml.cs`
- `UI/HaPlay/Views/ScriptEditorWindow.axaml`

Reference:

- `Reference/AvaloniaEdit-master/src/AvaloniaEdit/TextEditor.cs`

`ScriptEditorWindow` mirrors `SelectedScriptText` into `ScriptEditor.Text`.
AvaloniaEdit's `TextEditor.Text` setter replaces the full document text,
resets the caret to offset 0, and clears the undo stack. The reference editor
exposes `DocumentProperty` and internally uses weak document-change managers.

Why it matters: VM refreshes or selection changes can wipe undo/caret/scroll
state and make editing scripts feel unstable.

Suggested improvement: have selected scripts map to `TextDocument` instances
and bind/set `TextEditor.Document`. Persist text from document changes instead
of replacing `TextEditor.Text` during normal editing.

### Mond instruction limit is lifetime-based, not per invocation

Repo paths:

- `MediaFramework/Control/S.Control/ControlScriptDiagnostics.cs`
- `MediaFramework/Control/S.Control/ControlScriptFileHost.cs`
- `MediaFramework/Control/S.Control/ControlScriptRuntime.cs`

Reference:

- `Reference/Mond-0.11.2/Mond/MondState.cs`
- `Reference/Mond-0.11.2/TryMond/AutoAbortDebugger.cs`

`InstructionLimitDebugger` is documented as enforcing a per-invocation budget,
but it stores `_count` on the debugger instance attached to the `MondState`.
Modules are loaded once and invoked many times, so valid repeated trigger calls
eventually consume the same counter and trip the limit.

Why it matters: a healthy long-running control session can start timing out
scripts simply because the module has processed enough events.

Suggested improvement: reset the debugger counter around each exported function
call, create a fresh budget/debugger per invocation, or expose an invocation
scope on the debugger.

### X32 meter blob parsing is not connected to the OSC cache/script surface

Repo paths:

- `MediaFramework/Control/S.Control/X32Session.cs`
- `MediaFramework/Control/S.Control/ControlScriptRuntime.cs`
- `MediaFramework/Control/S.Control/ControlDeviceProfiles.cs`
- `UI/HaPlay/ViewModels/ControlWorkspaceViewModel.cs`

Reference:

- `Reference/OSC_Endpoints/UNOFFICIAL_X32_OSC_REMOTE_PROTOCOL.pdf.txt`
- `Reference/OSC_Endpoints/x-air_osc [Behringer World Wiki].txt`

The code includes `X32Meters.ParseFloatBlob` / `ParseRtaDbBlob`, and device
profiles contain meter tasks, but `ControlScriptRuntime.SetCacheValue` ignores
`OSCArgumentType.Blob`. Incoming meter blobs can be monitored, but they do not
become usable cached values or script values.

Why it matters: enabling meter subscriptions can generate network traffic
without providing operator-visible or script-consumable meter data.

Suggested improvement: add an X32-specific cache decoder that maps known
`/meters` blob replies into stable numeric cache keys, or mark the built-in
meter tasks as monitor-only until that mapping exists.

### X32 periodic subscription sends should be protocol-aware

Repo paths:

- `MediaFramework/Control/S.Control/ControlDeviceProfiles.cs`
- `MediaFramework/Control/S.Control/ControlPeriodicOscSendManager.cs`
- `MediaFramework/Control/S.Control/X32Session.cs`

Reference:

- `Reference/OSC_Endpoints/UNOFFICIAL_X32_OSC_REMOTE_PROTOCOL.pdf.txt`

The profile-level X32 `/xremote`, `/subscribe`, and `/meters` tasks are modeled
as generic periodic OSC sends. `ControlPeriodicOscSendManager` records a send
attempt as the last-send time even when the send fails. Separately,
`X32Session` models renewals more explicitly, but it is not integrated with the
profile task path.

Why it matters: X32 remote/subscription lifetimes have specific renewal and
cleanup semantics. Generic periodic sends make retry, unsubscribe, and health
state less accurate.

Suggested improvement: either route X32 profiles through `X32Session`, or add a
protocol-aware periodic task type with renewal failure policy, argument
validation, and cleanup/unsubscribe behavior.

## Lower Priority / Cleanup

### PortMidi host errors lose useful detail

Repo paths:

- `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIInputDevice.cs`
- `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIOutputDevice.cs`
- `MediaFramework/Extras/MIDI/PMLib/PMUtil.cs`

Reference:

- `Reference/portmidi-2.0.8/pm_common/portmidi.h`

The wrapper imports `Pm_HasHostError` and `Pm_GetHostErrorText`, but the input
poll loop logs only the enum from `Pm_Read`, and common output write paths
return `PmError` without enriching host failures. The PortMidi header says
asynchronous host errors are reported by `Pm_Poll` / `Pm_HasHostError`, and
`Pm_GetHostErrorText` clears and describes them.

Suggested improvement: on `PmError.HostError`, and optionally during periodic
health checks, call `Pm_GetHostErrorText()` and surface the host-specific text
through monitor/device health.

### PortMidi ALSA sysdep naming is not exposed

Repo paths:

- `MediaFramework/Extras/MIDI/PMLib/Native.cs`
- `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIInputDevice.cs`
- `MediaFramework/Extras/MIDI/PMLib/Devices/MIDIOutputDevice.cs`

Reference:

- `Reference/portmidi-2.0.8/pm_common/portmidi.h`

PortMidi 2 supports `PmSysDepInfo` entries for ALSA client and port names, but
the wrappers always pass `nint.Zero` for `inputSysDepInfo` /
`outputSysDepInfo`.

Suggested improvement: expose optional ALSA client/port name settings and
marshal `PmSysDepInfo` when opening MIDI streams. This would make Linux MIDI
ports easier to identify in external routing tools.

### SkiaSharp native assets are Linux-only in the framework project

Repo paths:

- `MediaFramework/Media/S.Media.SkiaSharp/S.Media.SkiaSharp.csproj`
- `Directory.Packages.props`
- `UI/HaPlay/HaPlay.csproj`

Reference:

- SkiaSharp package usage in project references

`S.Media.SkiaSharp` references `SkiaSharp.NativeAssets.Linux` only. Other media
projects contain Windows-specific paths, and `TextLayerSource` /
`ImageFileSource` are generally useful outside Linux.

Suggested improvement: either include platform-specific Skia native assets for
the supported runtime identifiers, or document/enforce that host apps must bring
the native assets. A startup probe with a clear diagnostic would make failures
less mysterious.

### TextLayerSource should return pooled buffers on Skia failure paths

Repo paths:

- `MediaFramework/Media/S.Media.SkiaSharp/TextLayerSource.cs`

Reference:

- SkiaSharp package usage in project references

`TextLayerSource.Rasterise` rents from `ArrayPool<byte>` before creating the
`SKSurface`, typeface, font, and paint. If Skia throws, or if `SKSurface.Create`
returns null, the rented buffer is not returned.

Suggested improvement: wrap the rasterization block after `Rent()` in a
try/catch that returns the buffer on failure, check the created surface before
using `surface.Canvas`, and add a small fault-path test if practical.

### Borrowed D3D11 COM pointer ownership should be explicit

Repo paths:

- `MediaFramework/Media/S.Media.OpenGL/Nv12Win32SharedHandleGpuUploader.cs`
- `MediaFramework/Media/S.Media.OpenGL/D3D11InteropUtility.cs`
- `MediaFramework/Media/S.Media.FFmpeg/Video/Internal/D3D11VaNv12BackingFactory.cs`
- `MediaFramework/Media/S.Media.Core/Video/Win32SharedNv12Backing.cs`

Reference:

- `Reference/FFmpeg.AutoGen-main`
- `Reference/Vortice.Windows-1.9.143`

`Win32SharedNv12Backing` documents libav D3D11 pointers as non-owning, while
OpenGL utility code wraps raw COM pointers with Vortice objects and disposes
those wrappers. Some comments say this balances a reference acquired by wrapper
construction, but the ownership semantics are subtle and not enforced in one
place.

This is an ownership audit item rather than a confirmed live failure: the
current implementation appears to rely on Vortice/SharpGen wrapper construction
taking a COM reference before disposal releases it. If that assumption ever
differs by constructor or package version, borrowed FFmpeg pointers could be
released incorrectly.

Suggested improvement: add a small helper for borrowed COM pointers that either
takes `AddRef` before creating an owning wrapper or clearly creates a non-owning
wrapper if supported. Use it consistently and test repeated decode/import/
dispose cycles.
