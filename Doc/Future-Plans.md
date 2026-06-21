# Future Framework development ideas

Ordered roughly from simplest to most complex. Status notes added as items are picked up.

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
  - **(1) Black when added mid-playback:** `OnSharedOutputsCollectionChanged` only rebuilds the checkbox list
    (`SyncOutputsCollection`); it does NOT hot-wire a newly-added clone into the *running* session. Since the
    clone has no checkbox, nothing calls `session.TryAddOutput(clone)`, so it never receives frames.
    **Fix:** on collection-add of a clone whose parent is currently routed in the live session, ensure the
    clone's preview runtime is started, then `TryAddOutput(clone)` through the playback arc (mirror
    `HotApplyRoutingToggleAsync`'s add branch).
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
    graph/convenience open (file/uri/stream), video+audio routers + routing, PortAudio/SDL output factories,
    PortAudio device discovery, C function-pointer events, transport/state. Builds + AOT-publishes clean.
  - **Remaining (building blocks only — high-level cue/soundboard/control engines are intentionally left to
    host apps / possible future addon libraries):** live inputs (PortAudio capture, NDI receiver) for
    `open_live`; NDI sender + file/encoder (recording) output factories; NDI source discovery; compositions
    (`ClipCompositionRuntime` layers + per-output mapping/warp).

- **Properly modularize the framework** — make parts non-strictly-required. **Assessment + plan** (the
  framework half is largely already done; the HaPlay half needs the running app to verify, so it is planned
  here rather than shipped blind):
  - *Framework level — already modular.* The framework is split into independent projects
    (`S.Media.Core`, `S.Media.FFmpeg`, `S.Media.PortAudio`, `S.Media.NDI`, `S.Media.SDL3`, `S.Media.Effects`,
    `S.Control` + `PMLib`/`OSCLib`, plus the new `S.Media.Interop`). A consumer references only what it needs:
    core+ffmpeg+portaudio gives audio playback with **no** NDI/SDL/MIDI references; adding `S.Media.NDI`
    enables NDI output; etc. Native runtimes are loaded lazily (`NDILibraryResolver`, `PMLib.Native`,
    `FFmpegRuntime`), so a managed reference that's never exercised won't fault on a machine missing that
    native lib. So "parts aren't strictly required" already holds for downstream consumers.
  - *HaPlay level — reflect missing native runtimes (the remaining work).* HaPlay references everything, so
    it must detect at runtime whether each native runtime is actually present and hide the corresponding UI:
    - NDI: probe with a guarded `NDILib.NDIRuntime.IsSupportedCpu()` / `Version` (throws/false when the NDI
      runtime isn't installed). Gate the Add-NDI-output / Add-NDI-input commands + the NDI output/input
      dialogs + any NDI device discovery.
    - MIDI: probe with a guarded `PMLib.PMUtil.Initialize()` / `CountDevices()` (throws when portmidi is
      missing). Gate the Add-MIDI-endpoint command and the MIDI device pickers in the Control workspace.
    - Suggested implementation: a small `RuntimeModules` helper in HaPlay caching `IsNdiAvailable` /
      `IsMidiAvailable` (each = `try { <native probe>; true } catch { false }`), bound to `CanExecute` /
      `IsVisible` on the relevant commands/menus. Deferred from this pass because correct gating spans
      several UI surfaces and needs the app run with/without each native runtime to verify nothing useful is
      hidden (a false-negative probe would wrongly disable a working feature).

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
  - *New `S.Media.MiniAudio` project.* P/Invoke against miniaudio (its single-file `miniaudio.c` built as a
    native lib, or a prebuilt shared lib — mirroring how `PALib` *binds* PortAudio, not how `PortAudioOutput`
    is shaped). `MiniAudioOutput` implements the framework's existing `IAudioOutput`/`IClockedOutput`/
    `IFlushableOutput`/`IPlaybackClock`/… directly from miniaudio's own callback + ring model — it does not
    reuse or imitate PortAudio's internals.
  - *Selection.* `MediaFrameworkRuntime` registers available backends; a host (or the C-ABI / HaPlay) picks
    one by name. HaPlay then drops "Add PortAudio" → "Add Audio Output" with a backend picker (persisted on
    the output definition); the C-ABI gains a `backend` parameter / a `mfp_audio_backend_*` enumeration
    rather than a PortAudio-only factory.
  - *Why deferred:* a from-scratch real-time backend (callback timing, ring sizing, underrun/flush,
    device-clock `ElapsedSinceStart`) only reveals glitches/drift on real audio hardware, so it must be
    validated live (`PlaybackSmoke` + the HaPlay deck), not landed unverified in a headless pass. The
    interface step (now done) was the safe part; what remains is `MiniAudioBackend`/`MiniAudioOutput` itself
    plus routing HaPlay's add-output picker and the C-ABI through `AudioBackends` instead of PortAudio-only.
