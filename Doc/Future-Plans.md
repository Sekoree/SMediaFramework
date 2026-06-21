# Future Framework development ideas

Ordered roughly from simplest to most complex. Status notes added as items are picked up.

Validation 2026-06-21: rechecked the completed UI cleanup, remove-in-use output flow, `Doc/Explained`
chapter 17 wiring, NativeAOT C-ABI project, and audio-backend abstraction against current code. Focused
validation passed: `dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore -v:m`,
`dotnet test MediaFramework/Test/S.Media.PortAudio.Tests/S.Media.PortAudio.Tests.csproj --no-restore -v:m`,
`dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m`, `dotnet build
MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj --no-restore -v:m`, and `dotnet build
MFPlayer.sln -m:1 --no-restore -v:m`.

Validation update 2026-06-21: fixed the follow-up crash when removing a clone/output after accepting the
"Stop playback & remove" warning. Focused validation passed:
`dotnet test MediaFramework/Test/S.Media.Playback.Tests/S.Media.Playback.Tests.csproj --no-restore -v:m`,
`dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m`, and
`dotnet build MFPlayer.sln -m:1 --no-restore -v:m`.

Validation update 2026-06-21: implemented the miniaudio backend foundation and expanded the NativeAOT C ABI
for backend-neutral live audio capture. Focused validation passed:
`dotnet test MediaFramework/Test/S.Media.MiniAudio.Tests/S.Media.MiniAudio.Tests.csproj --no-restore -v:m`,
`dotnet test MediaFramework/Test/S.Media.Core.Tests/S.Media.Core.Tests.csproj --no-restore -v:m`,
`dotnet build MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj --no-restore -m:1 -v:m`,
`dotnet build MFPlayer.sln -m:1 --no-restore -v:m`, and
`dotnet publish MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj -c Release -r linux-x64 -p:PublishAot=true -v:m`.
The publish output includes `libsmedia_miniaudio.so`, and `nm` shows the new
`mfp_audio_input_*`, `mfp_audio_source_destroy`, and `mfp_player_open_live_audio` exports.

Validation update 2026-06-21: expanded the NativeAOT C ABI with NDI discovery, live receiver open, and sender
output factories. Focused validation passed:
`dotnet build MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj --no-restore -m:1 -v:m`,
`dotnet test MediaFramework/Test/S.Media.NDI.Tests/S.Media.NDI.Tests.csproj --no-restore -v:m`,
`dotnet build MFPlayer.sln -m:1 --no-restore -v:m`, and
`dotnet publish MediaFramework/Interop/S.Media.Interop/S.Media.Interop.csproj -c Release -r linux-x64 -p:PublishAot=true -v:m`.
`nm` shows the new `mfp_ndi_*` and `mfp_player_open_live_ndi` exports.

Validation update 2026-06-21: HaPlay audio outputs are now backend-selectable instead of PortAudio-only.
The Add/Edit output dialog is labelled as an audio-output dialog with a backend picker; existing PortAudio
project entries still deserialize through the old `portAudio` discriminator, while new entries persist
`AudioBackendName` / `AudioBackendDeviceId` and the persistent runtime opens non-PortAudio outputs through
`IAudioBackend`. Focused validation passed:
`dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m` and
`dotnet build MFPlayer.sln -m:1 --no-restore -v:m`.

Validation update 2026-06-21: implemented HaPlay runtime-module gating for optional NDI and MIDI native
runtimes. `RuntimeModules` caches guarded probes, Add-NDI output/input commands and menu entries are hidden
when NDI is unavailable, and MIDI endpoint/catalog tabs and commands are hidden/disabled when PortMidi cannot
initialize, including the Control workspace live MIDI resolver/profile-builder surfaces. Focused validation passed:
`dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m` and
`dotnet build MFPlayer.sln -m:1 --no-restore -v:m`.

## Quick wins (UI text / cleanup)

- **[DONE]** In HaPlay the quick buttons to fullscreen/window a window had the "preview" suffix — removed
  to avoid confusion (`FullscreenPreviewMenuHeader` / `WindowedPreviewMenuHeader` → "Fullscreen" / "Windowed").
- **[DONE]** Local video output naming clarified: the Avalonia output's confusing "In-app preview" is now
  "UI Framework (Avalonia) Window"; the SDL output is "Standalone (SDL3) Window". (The per-engine subtitle
  text about Avalonia/SDL is kept as-is.)
- **[DONE]** Removed the unused NDI "recording" function. It was a HaPlay UI-only stub (`ToggleNdiRecording`
  / `IsNdiRecording` / a "REC" badge + Record button) that just flipped a bool and showed a badge — the
  backend recording was never wired ("a separate framework follow-up" that never happened), so the control
  did nothing and was misleading. Removed the command, property, button, badge, and the now-orphaned
  `RecordButton` / `StopRecButton` / `RecBadgeLabel` strings. (There is no recording code in the framework
  `S.Media.NDI` / `NDILib` itself.)

## Medium (bug fixes / UX)

- **[DONE]** When **removing an output while a video is playing** it could crash. The UI remove command now
  routes through `OutputManagementViewModel.RemoveLineAsync`: if a player is actively playing through the
  line (`PlaybackUsageProbe`), it shows a "Remove output while in use?" dialog offering "Stop playback &
  remove" or cancel; on confirm it stops the player(s) using the line before removing, so removal can't race
  a live submit. Programmatic removals (project load / reconfigure) are unaffected. Covers audio + video.
- **Clones of video outputs are currently extremely error prone.** Investigated; documented here because a
  correct fix needs live GL hardware to reproduce/verify the crashes (not possible in a headless dev loop —
  shipping blind changes to the crash-prone GL path would risk making it worse). Architecture + root causes:
  - A "clone" is a separate `LocalVideoOutputDefinition` with `CloneOfId` set; it's its own SDL/Avalonia
    window. It is **not** an `SDL3GLVideoOutput` texture mirror — it's routed alongside its parent via the
    "PlayerRoutingMirror" (`MediaPlayerViewModel.SelectedOutputLines` / `HotApplyRoutingToggleAsync` expand a
    ticked parent into parent + `GetClonesOf(parent)`). Clones have no own checkbox; their routing derives
    from the parent's tick.
  - **[DONE] (1) Black when added mid-playback:** `OnSharedOutputsCollectionChanged` now detects newly added
    clones whose parent is selected in a running session, waits briefly for the clone's preview runtime to
    come up, then calls `TryAddOutput(clone)` through the playback arc. This covers the missing hot-wire
    path; the GL resize/teardown crash items below still need hardware logs before changing blindly.
  - **(2) Clone not sized independently (crops):** with `HAPLAY_MEDIAPLAYER_COMPOSITIONS` on (default) the deck
    renders one fixed-size canvas and fans it to every output; each window should letterbox via the SDL
    output's `ViewportFit=Contain`. A smaller clone cropping suggests the clone branch isn't getting the
    Contain fit (or, in the legacy direct-router path, frames are sized for the parent). **Fix:** ensure the
    clone's `SDL3GLVideoOutput.ViewportFit` is `Contain` and that the composition lease for a clone is an
    independent output (its own viewport), not a shared parent surface.
  - **(3) Crash on resize / using playback controls:** GL-thread sensitive; needs a stack trace from a real
    GPU. Most likely a use-after-dispose / cross-context GL call when the clone window resizes or is torn down
    while the parent's render thread is mid-frame (clones share the deck's video route but have separate GL
    contexts). **Fix approach:** capture the crash log on hardware (the framework logs faults), then guard the
    clone's resize/teardown against the parent render thread (serialize on the anchor context, null-check the
    window/GL handles). Consider whether clones should instead use the real `SDL3GLVideoOutput` texture-mirror
    mechanism (anchor `ownsThread:false`, `CreateTextureMirror`+`RegisterTextureMirror`) which already
    handles shared-context rendering, rather than the route-duplication approach.
  - **[DONE] (4) Crash when removing while playback is loaded:** accepting the remove-in-use dialog's stop
    action stops transport but keeps the loaded media-player composition alive. Composition-opened outputs now
    record their fan-out lease id in `HaPlayPlaybackSession`, and `OutputLineRemoving` removes that lease from
    `ClipCompositionRuntime` before `OutputManagementViewModel` disposes the SDL/NDI runtime. The composition
    runtime retires a single output behind a submit gate, so an in-flight pump snapshot cannot submit to the
    disposed `SDL3GLVideoOutput`.
- **Sync the "Explain" docs** (`Doc/Explained/`) with the current code/behavior.
  - **[DONE]** Added the missing component: new chapter `17-NativeAOT-C-ABI.md` (the `S.Media.Interop` C ABI,
    which didn't exist when the series was written), wired into the `01-Overview` component table and the
    `README` index.
  - **[ONGOING]** A full line-by-line re-audit of chapters 02–16 against current code is a larger effort
    (and several behaviours changed recently — e.g. the composition pump now oversamples the source rate to
    avoid the 1:1 beat (ch. 05/10), hot-wiring a clocked audio output into a running router is now supported
    (ch. 04), and seek-while-playing no longer fights the slider (ch. 06/13)). These chapters aren't *wrong*
    on fundamentals but should get those specifics folded in on the next docs pass.

## Large (architecture / new dependencies)

- **NativeAOT C-ABI** to consume the framework from other languages.
  - **[FOUNDATION DONE]** `MediaFramework/Interop/S.Media.Interop` — NativeAOT shared library
    (`s_media_player.so`/`.dll`/`.dylib`) with `mfp_*` `UnmanagedCallersOnly` exports. Covers: lifecycle,
    graph/convenience open (file/uri/stream), audio-only live open, video+audio routers + routing,
    backend-neutral audio output/input factories + device discovery, PortAudio/SDL compatibility factories,
    C function-pointer events, transport/state. Builds + AOT-publishes clean.
  - **[DONE]** Backend-neutral live audio capture is exposed to native hosts: `mfp_audio_input_device_*`,
    `mfp_audio_input_create`, `mfp_audio_source_destroy`, and `mfp_player_open_live_audio`. Source ownership
    transfers into the player on a successful live open; otherwise the host remains responsible for destroying
    the source handle.
  - **[DONE]** NDI building blocks are exposed to native hosts: `mfp_ndi_source_count/get/open/destroy`,
    `mfp_player_open_live_ndi`, `mfp_ndi_output_create/video/audio/connection_count/destroy`, and optional
    runtime probes. NDI remains lazy: `mfp_initialize` does not require the NDI runtime to be installed, and
    NDI-specific calls report errors through `mfp_last_error` when the native runtime cannot be loaded.
  - **Remaining (building blocks only — high-level cue/soundboard/control engines are intentionally left to
    host apps / possible future addon libraries):** a generic non-NDI live video input handle path and compositions
    (`ClipCompositionRuntime` layers + per-output mapping/warp).
    File/encoder recording output factories were dropped from the remaining scope: HaPlay no longer needs the
    old NDI recording path, and recording factories are not part of the current interop goal.

- **Properly modularize the framework** — make parts non-strictly-required. **Assessment + implementation:**
  - *Framework level — already modular.* The framework is split into independent projects
    (`S.Media.Core`, `S.Media.FFmpeg`, `S.Media.PortAudio`, `S.Media.NDI`, `S.Media.SDL3`, `S.Media.Effects`,
    `S.Control` + `PMLib`/`OSCLib`, plus the new `S.Media.Interop`). A consumer references only what it needs:
    core+ffmpeg+portaudio gives audio playback with **no** NDI/SDL/MIDI references; adding `S.Media.NDI`
    enables NDI output; etc. Native runtimes are loaded lazily (`NDILibraryResolver`, `PMLib.Native`,
    `FFmpegRuntime`), so a managed reference that's never exercised won't fault on a machine missing that
    native lib. So "parts aren't strictly required" already holds for downstream consumers.
  - **[DONE]** *HaPlay level — reflect missing native runtimes.* `RuntimeModules` caches guarded
    `IsNdiAvailable` / `IsMidiAvailable` probes. Add-NDI output/input commands and menu entries are
    hidden/disabled when the NDI runtime cannot initialize; MIDI endpoint/catalog tabs, commands, and the
    Control workspace live MIDI resolver/profile-builder controls are hidden/disabled when PortMidi cannot
    initialize. Existing project content still loads; the gate only removes creation/discovery surfaces that
    would otherwise fail immediately. Hardware validation with NDI/PortMidi intentionally remains a
    machine-local check.

- **Adopt the miniaudio library** as an audio backend (source + headers in `Reference/miniaudio-0.11.25`).
  **The right shape is a backend-agnostic interface, NOT a miniaudio-emulates-PortAudio shim.** miniaudio
  should be a peer of PortAudio behind one common abstraction, never a translation of PortAudio's API.
  - **[DONE]** *Introduced the common audio-backend interface in `S.Media.Core`* — `IAudioBackend`
    (`EnumerateOutputDevices`/`EnumerateInputDevices`/`CreateOutput`/`CreateInput`) + `AudioDeviceInfo` +
    `AudioBackendOptions` + an `AudioBackends` registry. `PortAudioBackend` (in `S.Media.PortAudio`) wraps the
    existing catalog + ctors with no behaviour change and registers itself from `UsePortAudio()`. Tested
    (Core registry + PortAudio backend). The shape that was planned:
    ```
    interface IAudioBackend {
        string Name { get; }                                   // "PortAudio" / "miniaudio"
        IReadOnlyList<AudioDeviceInfo> EnumerateOutputDevices();
        IReadOnlyList<AudioDeviceInfo> EnumerateInputDevices();
        IAudioOutput CreateOutput(AudioDeviceSelector device, AudioFormat format, AudioBackendOptions opts);
        IAudioSource CreateInput (AudioDeviceSelector device, AudioFormat format, AudioBackendOptions opts);
    }
    record AudioDeviceInfo(string Id, string Name, int MaxChannels, double DefaultSampleRate, bool IsDefault);
    ```
    Note the *frame-level* contract is already backend-neutral: `AudioRouter` only ever sees
    `IAudioOutput` / `IClockedOutput` / `IAudioSource`, never PortAudio types. So this new interface only
    abstracts the **device/enumeration/open** layer that sits above those. `PortAudioBackend : IAudioBackend`
    wraps today's catalog + ctors (no behaviour change); `MiniAudioBackend : IAudioBackend` is the new one.
  - **[DONE]** *New `S.Media.MiniAudio` project.* The project builds a small native shim over vendored
    `miniaudio.c` on Linux/macOS and binds it through AOT-friendly `LibraryImport` calls. `MiniAudioBackend`
    implements `IAudioBackend`; `MiniAudioOutput` implements the framework's existing `IAudioOutput` /
    `IClockedOutput` / `IFlushableOutput` / `IPlaybackClock` / playback-stats contracts from miniaudio's
    callback + managed ring model; `MiniAudioInput` implements `IAudioSource` for capture. `UseMiniAudio()`
    registers the backend without opening hardware. Current caveat: Windows expects a matching
    `smedia_miniaudio.dll` sidecar unless/until a Windows native build step is added.
  - *Selection.* `MediaFrameworkRuntime` registers available backends; a host (or the C-ABI / HaPlay) picks
    one by name. **[C-ABI DONE]** The C ABI now has backend-neutral `mfp_audio_backend_*`,
    `mfp_audio_device_*`, `mfp_audio_input_device_*`, `mfp_audio_output_create(backend, deviceId, ...)`,
    `mfp_audio_input_create(backend, deviceId, ...)`, and `mfp_player_open_live_audio(...)` entry points,
    declared in the public header while keeping the legacy PortAudio factory for compatibility. NativeAOT
    publish copies the miniaudio sidecar next to `s_media_player.so`.
  - **[DONE]** HaPlay output selection is backend-aware: the Add/Edit dialog now says "audio output", shows a
    backend picker, persists `AudioBackendName` / `AudioBackendDeviceId`, and the persistent audio runtime
    opens non-PortAudio outputs through `IAudioBackend`. Existing PortAudio project files remain compatible.
  - **Remaining:** real hardware validation for callback timing, ring sizing, underrun/flush, and device-clock
    `ElapsedSinceStart` (`PlaybackSmoke` + HaPlay deck). PortAudio-only queue tuning remains guarded to
    PortAudio until a backend-neutral queue/latency control surface is introduced.
