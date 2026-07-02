# MFPlayer `next` rewrite — consolidated review (old ↔ new ↔ plan ↔ prior review)

**Review date:** 2026-07-02
**Reviewed revision:** `cb6fa9e` on branch `next-2` (16 commits / +8,411 −552 lines since the previous review's `79b70ed`)
**Scope:** (1) the old tree (`MediaFramework/`, `UI/`) vs. the rewrite (`next/`), (2) the rewrite plan (`Next/01–09`) vs. what was actually built, (3) verification of the prior review's remediation state (`Review/MFPlayer-Next-Critical-Review.md`, NXT-01…NXT-17), and (4) a fresh code review of the current implementation — concentrated on the ~8.4k lines added since `79b70ed`, which the prior review has not seen.
**Relationship to the prior review:** this document *consolidates* — it re-verifies the prior findings against today's code, corrects its status table where the code has moved past it, and continues its numbering with new findings **NXT-18 … NXT-26**. The prior file remains as the detailed history; treat *this* file as the current ledger.

## Verified baseline

| Check | Result |
|---|---|
| `dotnet build next/MFPlayer.Next.sln` | 0 errors |
| Full managed suite (`MFP_PORTAUDIO_HOST_API=JACK pw-jack dotnet test`) | **1,428 passed / 0 failed** (14 test projects; was 1,301 at `79b70ed`, 1,400 at the review doc's last note) |
| Session tests | 86 (was 48 at review time) |
| Legacy engines (`CuePlaybackEngine`, `HaPlayPlaybackSession`, `SoundboardEngine`) | still present — the `HAPLAY_USE_SHOWSESSION=0` fallback |
| Deck `_session` usages (engine-deletion Stage 3 marker) | **102** across `MediaPlayerViewModel*` (was 93 — new deck features touched the engine partials) |
| Runtime default | ShowSession (flipped 2026-07-01, `ShowSessionGate`) |

## Executive conclusion

The rewrite is in **substantially better shape than at the 2026-06-30 review**. Every blocker (NXT-01/02/03/04-core/05/08) is genuinely fixed in code — I re-read the fixes rather than trusting the remediation log, and they hold: host-declared output-lease ownership, one atomic cancellable demux-shared open, off-dispatcher cue fires with a generation guard, clock-mastered session compositions, an owning `MediaHost` disposed by both HaPlay and the C ABI, and a real handle table behind every C export. The remediation log is *accurate and honest* — with the pleasant exception that it now **understates** progress: several items it lists as deferred are already done (see “review-ledger corrections” below).

What remains is exactly what the prior review predicted would remain: the **duplicate orchestration layer** (engines kept as fallback → NXT-06/13 deletion), the **plugin host as a product feature** (NXT-09/10), the **full timeline contract + measured sync gates** (NXT-04 remainder), and **performance/CI gates** (NXT-11/14/15 remainders).

The fresh pass found **no new blocker**, but it did find a coherent family of real defects in the new code — all variations of one theme the NXT-03 fix established but did not finish: **the session dispatcher is still parked by long-running work that isn't a cue fire** (stop fades, soundboard/preview opens), and the UI **synchronously blocks on that same dispatcher** (sync `LoadDocument` on the UI thread), so a parked loop becomes a frozen app. There is also a lease-ordering bug on the cue-workspace reload path that mirrors the exact hazard the deck stop path documents and guards against. These are NXT-18…NXT-21 below and should be scheduled before the engines are deleted (the engines currently mask nothing — these paths are already the default — but deletion removes the escape hatch).

---

# Part 1 — Old tree vs. new tree

## 1.1 Project mapping

Every old capability either moved, merged, or was deliberately dropped. The mapping, verified against both trees:

| Old (`MediaFramework/`) | New (`next/MediaFramework/`) | Status |
|---|---|---|
| `S.Media.Core` | `S.Media.Core` | rewritten slim + registry/dispatcher/`MediaHost` added |
| `S.Media.Time`, `S.Media.Routing`, `S.Media.Gpu`, `S.Media.Session`, `S.Media.Players` (old stubs) | same names | the stubbed restructure the plan formalized — now real |
| `S.Media.FFmpeg` | `S.Media.FFmpeg.Common` + `S.Media.Decode.FFmpeg` | split; decode complete incl. hw-decode, subtitles (text + bitmap), capture provider |
| `S.Media.FFmpeg.Encode` (**15 files, ~1,019 lines**: audio/video/mux file outputs) | **— none —** | **dropped.** See §1.2 |
| `S.Media.Effects` (31 files, ~5k lines) | merged into `S.Media.Compositor` (36 files) | done, FFmpeg-decoupled |
| `S.Media.OpenGL` | `S.Media.Gpu` | done |
| `S.Media.SDL3` / `S.Media.Present.SDL3` | `Present.SDL3` + `Present.SDL3.Compositor` | done |
| `S.Media.Avalonia` | `S.Media.Present.Avalonia` | done (`VideoOpenGlControl`) |
| `S.Media.NDI` + `Present.NDI` (empty) | `S.Media.NDI` | done; shared-receiver A/V correlation |
| `S.Media.PortAudio`/`MiniAudio` + `Audio.*` split | `S.Media.Audio.PortAudio` / `.MiniAudio` | done; PortAudio gains `padev://` capture provider |
| `S.Media.Playback` | `S.Media.Session` (`ShowSession`/`CueGraph`/`ClipCompositionRuntime`/standby/soundboard) | rebuilt fresh (not ported) |
| `S.Media.SkiaSharp` (~510 lines, images/text) | **— none —** in framework | text rendering moved to HaPlay (`TextFrameRenderer`, SkiaSharp in UI); images open via FFmpeg. Framework-level image/text capability intentionally dropped |
| `Control/S.Control` + `Extras/MIDI`/`OSC` | `Control/S.Control` + `S.Control.Abstractions`, `MIDI/PMLib`, `OSC/OSCLib` | done, fully data-driven (zero device-specific C# logic; profiles + Mond helpers) |
| `Extras/JackLib` (~778 lines) | **— none —** | dead code even in the old tree (no `.csproj` references it) — correct drop |
| `Interop/S.Media.Interop` | `S.Media.Interop` (outbound C ABI) + **new `S.Abi`** (inbound plugin host) | outbound retargeted to `ShowSession`; inbound is new capability |
| — | **new** `S.Media.Subtitles` + `Subtitles/LibAssLib` | net-new (libass renders everything; FFmpeg decodes to ASS/bitmap) |

**UI:** old `UI/` (`HaPlay`, `HaPlay.App`, `HaPlay.Controls`, `HaPlay.Core`, `HaPlay.Desktop`, `HaPlay.Tests`) → `next/UI/` (`HaPlay`, `HaPlay.Desktop`, `HaPlay.Tests`; 199 vs. 186 `.cs` files). This is the Phase-8 **pivot** (see Part 2): the old app was *ported* onto the new framework and its engines re-backed onto `ShowSession`, not rebuilt. The old app and solution remain untouched and buildable.

## 1.2 Real parity gaps old → new

1. **FFmpeg encode/mux is gone.** The old `S.Media.FFmpeg.Encode` was a working ~1k-line record-to-file capability (audio/video codecs, mux output, `EncoderSmoke`, its own test project). No UI feature referenced it, so nothing user-visible broke — but the prior review's **Gate 5 (YouTube cached playback) requires exactly this** (remux two streams into one local asset), and the empty `next/` shell was deleted by the NXT-13 cleanup. *Action:* when Gate 5 starts, salvage from `MediaFramework/Media/S.Media.FFmpeg.Encode/` rather than rewriting; until then this is a recorded, deliberate gap.
2. **Framework-level image/text sources.** Old `S.Media.SkiaSharp` let the *framework* render stills/text; in `next/` that is HaPlay-private (`TextFrameRenderer` + the `text:` decoder provider registered by `MediaRuntime`). Consequence: a *headless* host (C ABI, future control surfaces) cannot play text cues — `text:` URIs only resolve inside HaPlay. Acceptable now; revisit if the C ABI is meant to run full HaPlay shows (a show document with a text cue loaded through `s_media_player` will fail to open that clip).
3. **Old diagnostic tools without replacements** — `EncoderSmoke`, `TransportSyncProbe` (already noted in NXT-14). The new tool set (23 smokes) otherwise supersedes the old one.
4. Everything else the old framework did — file/NDI/capture playback, HW decode, GPU composition + warp/multi-output, routing/matrix, subtitles (new!), control, soundboard, cues — has a working new-tree counterpart, most of it regression-tested.

---

# Part 2 — Plan (`Next/`) vs. implementation (`next/`)

## 2.1 Phase status (verified against checklists + code)

| Phase | Plan gate | Actual state |
|---|---|---|
| 0 Scaffold | ✅ | done as documented |
| 1 Core+Time+Routing | ✅ | done; registry/dispatcher/sync primitives in place |
| 2 First playback | ✅ | done (audio-first per scope decision) |
| 3 GPU+Compositor+Players | ✅ | done incl. mesh warp, `CompositeMulti`, zero-copy targets; dmabuf modifier negotiation still the recorded platform caveat |
| 4 Session | ✅ | done; substantially hardened post-review (validator, off-dispatcher fires, lock-free snapshots, multi-placement fan-out, barriers) |
| 5 Live+multi-output+backends | ✅ | done incl. 1-hour multi-output soak; Windows CI leg still deferred |
| 6 Subtitles+Control+plugin host | ✅ gates met | subtitles complete; control fully data-driven; **but** the plugin host is adapters+smokes, not a host feature (NXT-09 stands — the plan's own “review-hard-before-v1” stance is correct: do not freeze ABI v1) |
| 7 Outbound C ABI | ✅ | done + hardened (handle table, shutdown, negative-handle C gate); Windows leg deferred |
| 8 UI | **pivoted** | see below |

## 2.2 The Phase-8 pivot — plan docs are stale here

`Next/09-Phase-Checklists.md` Phase 8 still describes the original **rebuild** approach (fresh `HaPlay.Core`/`App`/`Desktop` thin-MVVM slices, cue-authoring workspace, preview surface experiments) — work that was **abandoned on 2026-06-29** in favor of porting the old HaPlay wholesale onto the new framework and strangling its engines onto `ShowSession`. The pivot is recorded only in memory/review notes, not in the plan. The actual Phase-8 state:

- Old HaPlay ported into `next/UI/` — compile-first complete, app launches; 132 UI files byte-identical to the old tree at review time (an architectural port, as the prior review characterized it).
- Cue workspace, media-player deck, and soundboard **re-backed onto `ShowSession` and default-on** (2026-07-01 flip, `ShowSessionGate`; startup log names the active path).
- Engines retained as the `HAPLAY_USE_SHOWSESSION=0` no-rebuild fallback; deletion staged (footprint: `SoundboardEngine` 2 files → `CuePlaybackEngine` 9 → `HaPlayPlaybackSession` 19) and gated on the deck Stage-3 migration (102 `_session` usages) + real-world miles.
- Phase 8's plan-level exit (“old HaPlay retired; feature parity”) is therefore **still open**, as is “UI persists only view-state” (D10).

**Plan-doc drift to fix (low effort, high confusion-avoidance):**
1. Rewrite the Phase-8 section of `09-Phase-Checklists.md` around the pivot (port → re-back → flip → delete), marking the abandoned rebuild slices as historical.
2. `02-Project-Structure.md` still lists `Images.Skia` / `Encode.FFmpeg` as solution projects; both were removed from the sln (NXT-13). Annotate.
3. The checklist's Phase-8 `HAPLAY_SMOKE` claims belong to the abandoned rebuild app; the *ported* app has no such hook (NXT-15 remainder).
4. `README.md`'s “What this rewrite adds → Real modularization/plugins” should carry the “ABI experimental until YouTube/MMD exercise it” caveat the review established, so nobody freezes v1 off the README.

---

# Part 3 — Prior-review ledger (NXT-01…17), re-verified in code

I re-read every claimed fix at `cb6fa9e` rather than trusting the log. Verdicts:

| ID | Ledger says | Code says (this review) |
|---|---|---|
| NXT-01 | fixed | **Confirmed fixed.** Factory returns `ClipCompositionOutputLease`; HaPlay cue + deck paths lease borrowed lines `DisposeOutputOnRuntimeDispose:false`; session-owned `DiscardingVideoOutput` stays session-disposed. *New related ordering bug on the reload path → NXT-20.* |
| NXT-02 | core fixed, network prepare/cache deferred | **Confirmed.** `MediaOpenRequest/Result` atomic open; result owns the asset (player adopts as owned companion); provider's real error surfaces; AVIO-interrupt cancellation threaded. Deferred remainder unchanged (YouTube gate). |
| NXT-03 | done for fires | **Confirmed for fires** (fire-lock + setup→open-off→commit + `_showGeneration` + `CancelActiveFire`). **Not applied to voices/previews (→ NXT-19) and stop-fades re-park the loop (→ NXT-18).** |
| NXT-04 | partial by design | Confirmed: compositions clock-mastered per group (`SessionClockMaster`), seek/pause barriers, fire-time start barrier. Full per-group timeline/discontinuity contract + measured skew gates remain the architectural remainder. |
| NXT-05 | MediaHost done | **Confirmed.** `MediaHost` owns registry + module lifetimes + (inert) plugin leases with leak reporting; `App.axaml.cs` wires `MediaRuntime.Shutdown()` on `ShutdownRequested`; C-ABI `SessionBox` holds+disposes a host. One note: `MediaRuntime.Registry` lazily *rebuilds* a fresh host if touched after `Shutdown()` — a late poll during teardown can resurrect PortAudio; acceptable but worth an assert. |
| NXT-06 | flipped, engines kept | Confirmed. Deck Stage-2 items the ledger still lists as open are **done** (see corrections below); Stage 3 = 102 `_session` usages. |
| NXT-07 | fixed | **Confirmed.** GO filters armed+enabled, cursor advances only on ran/faulted; cycle guard at both validator and `CueGraph` (defence in depth); fault-policy enum honestly documented (only `StopShow`/`Continue` real); fades honor configured gains. |
| NXT-08 | fixed | **Confirmed.** Monotonic handle table, never dereferences caller tokens; every export no-throw; shutdown destroys live sessions; destroy idempotent; C smoke has the negative-handle gate. |
| NXT-09 | deferred | Unchanged — adapters + smokes, no host feature. Correctly deferred until YouTube/MMD exercise the ABI. |
| NXT-10 | deferred | Unchanged — layer surfaces still outside `IVideoCompositor`/multi-output. |
| NXT-11 | seam done; “host GPU wiring remains” | **Ledger is stale — the host GPU wiring is DONE.** Both HaPlay paths inject `CueCompositionRuntime.CreateShowSessionCompositor` (GL with CPU fallback) into `ShowSession` (cue: `MainViewModel.cs:286`; deck: `MediaPlayerViewModel.ShowSession.cs:88` — with an observed real-world motivation recorded in the comment: CPU compositing made NDI egress stutter). Remaining: `CompositeWithSurfaces` CPU readback + benchmark/allocation gates. |
| NXT-12 | fixed | **Confirmed.** Validate → stage → atomic swap; staged-composition rollback on factory failure; version gate (a versionless doc → `Version=0` → rejected with a clear message). Minor completeness gap → NXT-25. |
| NXT-13 | **done (2026-07-02)** | Empty projects gone. **Engine deletion executed:** `CuePlaybackEngine`(+partials/types), `SoundboardEngine`, `HaPlayPlaybackSession`(+partials), `ShowSessionGate`/`HAPLAY_USE_SHOWSESSION`, `EngineAudioGenlock`, `CuePreviewSession`, `CuePreRollCache`, pre-connect caches, `PlaylistDecoderCache`, input connectors, `SubtitleOverlayVideoSource`, `CueAudioSourceAdapters`, `PlaybackThroughputDiagnostics` + 4 engine test files — 25 files, net **−9.8k lines**. Shared DTOs survive in `Playback/CuePlaybackTypes.cs`; cue auto-follow now rides the new `ShowSession.ClipNaturallyEnded` event. ShowSession is the *only* runtime — no fallback. Gates: full sln build 0 errors, 1,409 tests green (−38 engine tests; 1 solo-pass load-flake in `VideoPtsClockTests`), real-media SessionSmoke OK, AotSmoke publish+run OK. The two deliberate gaps (deck VU meters, bare-file-EOF auto-follow) were closed + hardware-verified the same day — see *Post-deletion follow-ups*. |
| NXT-14 | partial | Session tests 48→86, PortAudio tests added, flip-confidence + end-behavior + fan-out coverage added; 1,428 total. Perf/alloc/sync-tolerance gates still absent. |
| NXT-15 | partial | Branch triggers fixed (`next`, `next-*`). Still open: `HaPlay.Desktop` `PublishAot=false` **and `AssemblyName=HaPlay_Test`** (rename is pure leftover — fix independently of the AOT audit); GL/subtitle smokes `continue-on-error`; no HaPlay launch/self-exit smoke; no Windows legs. |
| NXT-16 | fixed | **Confirmed** — `Snapshot()` lock-free over volatile `_groupViews`; composition stats + audio-pump stats got the same treatment. Residue: `GetCueDefinitionsAsync`/`GetVoiceProgressAsync`/`IsVoicePlayingAsync`/`GetPreparedCueIdsAsync` still marshal — harmless *until* the dispatcher parks (NXT-18/19), then the soundboard progress + cue-list reads stall with it. |
| NXT-17 | fixed | Confirmed (documented lifetime; freed on shutdown for the calling thread — other threads' final buffer still leaks once per thread; bounded, acceptable). |

**Ledger corrections (progress the review doc understates — no action needed, just recording):**
- Deck **subtitle attach** is done (`MediaPlayerShowMapper.MapSubtitles`), listed as “remaining” in the cutover checklist.
- Deck **canvas sizing** is done (`ResolveDeckCanvasSize` — largest driven output resolution, 1080p fallback), listed as “currently hardcoded 1920×1080”.
- Deck **NDI-output audio** is done (`BuildNdiAudioLease` + borrowed-carrier audio leases + per-fire session-owned resampler wrapper), listed as deferred “(b)”.
- Deck **hot output add/remove** landed (`RebuildActiveClipAudioOutputsAsync`, `AddCompositionOutputAsync`/`RemoveCompositionOutputAsync`) with correct release-after-unroute ordering on the remove path.
- Seek robustness landed on the deck (`ConfirmShowSessionEnded` two-tick end confirmation; `SeekAsync` preserves play state — the “seek stops playback” class is fixed and unit-tested).

---

# Part 4 — New findings (fresh review of the post-`79b70ed` code)

Severity scale as before. None of these is a default-flip regression blocker on its own; NXT-18/19/21 together are the “operator hits a wall for N seconds” family and should precede engine deletion.

> **Status update (2026-07-02, same day):** all nine findings below (NXT-18…NXT-26) were implemented and
> verified — see the **remediation log** at the end of this part. Full suite after the fixes: **1,436/1,436**
> (+8 new regression tests), real-media `SessionSmoke` exit 0.

### NXT-18 — High — ✅ FIXED (2026-07-02): stop fades park the session dispatcher for their full duration

`StopAsync`/`StopAllAsync`/`StopCueAsync` run `FadeGroupsAsync` **inside** their dispatcher work item (`ShowSession.cs:1519-1558`), and `FadeGroupsAsync` awaits `Task.Delay` steps until the *longest* fade completes (`:1586-1602`). `SessionDispatcher.RunLoopAsync` awaits each work item to completion (`SessionDispatcher.cs:126-145`), so the serial loop is parked for the entire fade — 750 ms by default, or the cue's configured `FadeOut` (operator-set, can be many seconds).

While parked, everything queued behind it stalls: pause/resume, seeks, `LoadDocument`, a concurrent fire's `CommitClipAsync` (GO-after-stop waits out the fade before its clip commits), `DisposeAsync`, and every still-marshaled query (cue list, soundboard voice progress → tiles freeze during a stop fade). This is precisely the D5/NXT-03 hazard class, reintroduced on the stop path — and it is *inconsistent within the same file*: the natural-end fade was explicitly moved off-dispatcher for this exact reason (`StartNaturalFadeOut`, “without occupying the session dispatcher between steps”, `:1255-1311`).

**Fix:** reuse the existing pattern. On the dispatcher: mark the group(s) fading (`TryBeginFadeOut` already provides the claim) and return; run the ramp off-dispatcher (per-group task or one shared stopwatch task, exactly like `StartNaturalFadeOut`); re-enter the dispatcher for the final `ReplaceActiveAsync` with a `ReferenceEquals(group.Active, clip)` guard (already the established idiom). `StopAsync`'s *returned task* should still complete after the release so callers keep their “stopped means stopped” contract. Add a test: STOP with a 5 s `FadeOut`, assert a concurrent `SetPausedAsync`/`GetVoiceProgressAsync` completes in <100 ms.

### NXT-19 — High — ✅ FIXED (2026-07-02): soundboard voice fires and cue previews open media on the dispatcher

`FireVoiceAsync` (`ShowSession.cs:840-881`) and `PreviewCueAsync` (`:779-813`) `await _standby.ArmAsync(...)` **inside** `InvokeAsync`. The open itself runs on a worker (`ClipStandbyEngine.PrepareAsync` wraps it in `Task.Run`), but that is irrelevant to the loop: the dispatcher awaits the whole work item, so it is parked for the full open duration. The NXT-03 fix (setup on-dispatcher → open off → commit on-dispatcher, with a generation/cancel re-check) was applied to cue fires only.

Consequences: a soundboard tile pointing at a slow/NAS/cold file freezes *all* transport (GO, STOP commit, pause) and the marshaled queries for the duration of the open; same for auditioning a cue. Soundboard is a rapid-fire surface — this will be *felt* in real operation, and unlike a cue fire there is no `_activeFireCts`, so STOP does not even preempt it.

**Fix:** apply the fire pattern: validate + release-previous on the dispatcher, `ArmAsync` off it (with a per-voice CTS so stop/replace cancels an in-flight open), commit (attach outputs, start, register handle) back on the dispatcher with a staleness re-check. The `_showGeneration` guard is not needed (voices/previews are document-independent) — a simple “was this voice id re-fired/stopped meanwhile” token suffices. Add the corresponding blocked-open tests (the NXT-03 test recipe transfers 1:1).

### NXT-20 — Medium — ✅ FIXED (2026-07-02): cue-workspace reload releases borrowed video lines while the old compositions still hold them

`ReloadCueShowSession` (`MainViewModel.cs:949-1011`) does, in order: **release** every previously-held line (`:962-964`) → **re-acquire** lines for the new bindings (`:974-987`) → `LoadDocument` (`:991`), which only *then* disposes the old compositions (detaching their outputs). Between the release and the load-commit, an old composition whose pump has started (any cue played on it since load) keeps submitting canvas-format frames into a sink the release just handed back to the idle slate / reconfigured — the exact failure the deck stop path documents and guards against with detach-before-release (`MediaPlayerViewModel.ShowSession.cs:480-493`: “the pump keeps submitting canvas-format frames to it → a format-mismatch flood (Submit throws every tick)”).

The deck's **source-switch** path has the same shape (`TryOpenViaShowSessionAsync:100-160`): it stops the clip (`StopAsync`) but does not detach composition outputs before the UI-thread release/re-acquire block; the old composition survives until `LoadDocument` replaces it.

Window is short (release → synchronous load) and it self-heals, so this is a transient error-flood / visible flash, not a crash — the lease ownership itself (NXT-01) is respected. But it is the same defect class, on the *default* path, and it also causes a needless idle-slate flicker on every structural cue edit while idle.

**Fix (either):** (a) detach first — loop `RemoveCompositionOutputAsync` over the held lines before releasing them (both call sites), mirroring `ShowSessionStopAsync`; or better (b) make it structural: have `LoadDocumentCoreAsync` retire the *old* compositions **before** invoking the video-output factory for the new ones (the staged-swap already tolerates reordering: validate → retire-old-outputs → stage new → swap), so hosts never need to sequence this themselves. (b) also removes the release/re-acquire churn for lines that stay bound across a reload — no slate flash.

### NXT-21 — Medium — ✅ FIXED (2026-07-02): the UI thread blocks synchronously on the session dispatcher (`LoadDocument`)

`ReloadCueShowSession` runs on the UI thread (debounce timer / collection events / `EnsureCueShowSessionCurrentAsync`'s flush-before-fire) and calls the **blocking** `ShowSession.LoadDocument` (`MainViewModel.cs:991`); the deck open path does the same (`MediaPlayerViewModel.ShowSession.cs:162`, continuation on the UI context via `ConfigureAwait(true)`). `LoadDocument` is sync-over-async onto the session dispatcher — so whenever the dispatcher is parked (a stop fade NXT-18, a voice/preview open NXT-19, or any future long command), **the UI thread freezes for the same duration**. The dispatcher-stall findings above escalate from “transport feels sluggish” to “the whole app beachballs” through this one coupling. This is the `ui_thread_observable_property_sets` / C1 (“UI-thread Play”) bug class from the old framework, re-entering through the back door.

**Fix:** all three call sites are inside `async` flows or can be — await `LoadDocumentAsync` (keep the output acquisition on the UI thread *before* the await, which the code already stages correctly; only the load itself must not block). Grep-gate: no `\.LoadDocument\(` / `.GetAwaiter().GetResult()` on session APIs from `next/UI/`. Consider `[Obsolete]`-ing the sync `LoadDocument` for UI assemblies once the C ABI keeps its (legitimately synchronous) use.

### NXT-22 — Low — ✅ FIXED (2026-07-02): fire-and-forget session tasks flow the dispatcher's `AsyncLocal` identity

`SessionDispatcher.IsOnDispatcherThread` is `AsyncLocal`-based; the monitors/fades deliberately wrap their `Task.Run` in `ExecutionContext.SuppressFlow()` with a comment explaining that a leaked identity would make `InvokeAsync` run *inline off the real loop* and race transport commands (`ShowSession.cs:1179-1181`). But `LoadDocumentCoreAsync`'s trailing `_ = WarmUpcomingAsync()` (`:327`) runs on the dispatcher **without** suppression: its continuations (after the first await inside `RefreshStandbyAsync`) execute on thread-pool threads that still carry `Current.Value == dispatcher`. Today nothing after that first await calls `InvokeAsync` or touches session state, so it is latent, not live — but it is one refactor away from a very confusing race, and the codebase already knows the rule.

**Fix:** wrap the warm launch in `SuppressFlow` (or route it through a small `PostBackground` helper that does), and/or add a debug assert in `InvokeAsync`'s inline path that the *physical* thread is the pump thread when the logical identity says so.

### NXT-23 — Low — ✅ FIXED (2026-07-02): `CueGraph` execution log grows without bound

Every fire appends to `_log` (`CueGraph.cs:284-297`) and `Clear` only runs on document load. A multi-day install with looping/auto-continue shows accumulates indefinitely (and `ExecutionLog` snapshots copy the whole list per query). Cap it (ring buffer, e.g. last 1,000 entries) — the UI only ever reads the tail.

### NXT-24 — Low — ✅ FIXED (2026-07-02): audio device enumeration on every clip-spec build

`BuildClipSpec` → `ResolveBackendSampleRate` → `IAudioBackend.EnumerateOutputDevices()` runs on every fire, every warm (×2 per GO), and every soundboard voice fire (`ShowSession.cs:529-576`, `:845`); the constructor enumerates too. PortAudio enumeration is not free and is exactly the call that misbehaves on flaky ALSA setups (this box's known failure mode). Cache the device list / default-rate lookup in the session (invalidate on the backend's device-change notification or a short TTL).

### NXT-25 — Low — ✅ FIXED (2026-07-02): validator does not check route/audio-output references

`ShowDocumentValidator` covers cues/clips/compositions/placements/follow-ons/stop-targets, but not `Routes` (`OutputPatchRoute.SourceId` → cue id, `OutputId` → `_master`/output ids) or `AudioOutputs` group ids — a dangling route silently never matches at play time (`ResolveOutputChannelMap` just returns null). Cheap to add, keeps the “caught at load, not silently dropped at play” promise uniform.

### NXT-26 — Low — ✅ FIXED (2026-07-02): live-source warm-read in the open path is not cancellable

`MediaPlayer.TryOpenLive` blocks for a live source's first frame to learn its native formats (`MediaPlayer.cs:753-758`) — outside the cancellation plumbing that now covers the registry open itself. An `ndi://` source that registers but never delivers a frame (sender wedged between discovery and connect) makes the open hang un-preemptably; with NXT-19 unfixed, a soundboard/preview open of such a source parks the dispatcher indefinitely. Thread the token (bounded wait + retry-or-fail) through the warm-read.

## Remediation log — NXT-18…NXT-26 (2026-07-02)

All nine findings implemented the same day, on top of `cb6fa9e`. Verified: full solution build 0 errors; **1,436/1,436** managed tests (session suite 86 → 94 — the 8 new regressions below); real-media `SessionSmoke` (`/run/media/sekoree/512/mambo.mp4`) exit 0 — audio cue + seek + video composite with a subtitle layer + trim-in + loop + fade-in + host fan-out, all through the changed paths.

| ID | What changed | Key files | Regression tests |
|---|---|---|---|
| NXT-18 | Stop fades ramp **off** the dispatcher: `StopAsync`/`StopAllAsync`/`StopCueAsync` → shared `StopGroupsCoreAsync` (claims fades on the loop via the existing `TryBeginFadeOut`) + `RunStopFadeAsync` (off-loop stopwatch ramp with short marshaled `ApplyFadeLevel` steps; early-exit when every faded clip is replaced) + an identity-guarded final release — a cue fired *during* the fade survives ("stop only releases what it saw", the same outcome the old atomic version got by queuing the fire's commit behind the whole fade). Dispose racing a stop is ODE-guarded. The on-loop `FadeGroupsAsync` is deleted. | `ShowSession.cs` | `Stop_WithLongConfiguredFade_DoesNotParkTheDispatcher`, `Stop_WithConfiguredFade_StillReleasesTheClip_AtFadeEnd`, `Fire_DuringAStopFade_SurvivesTheStopsRelease` |
| NXT-19 | `FireVoiceAsync`/`PreviewCueAsync` restructured to the NXT-03 pattern: **setup (dispatcher; claim CTS published in `_pendingVoiceOpens`/`_previewCts`) → `ArmAsync` off-loop → identity-checked commit (dispatcher)**. Stop / re-fire / stop-all / dispose cancel a *pending* open; a preempted fire completes without error (the voice/preview simply never started); dispose racing the commit releases the orphaned clip directly. **Bonus fix found under test:** `WarmUpcomingAsync` ran its standby refresh — media opens — as one awaited dispatcher work item, so GO's background pre-roll (and the load-path warm) parked the loop for its opens too; it now reads cue state on the loop and refreshes off it. | `ShowSession.cs` | `FireVoice_RunsOffDispatcher_AndStopVoicePreemptsThePendingOpen`, `StopAllVoices_PreemptsPendingOpens_Too`, `RefiringAVoice_ReplacesItsPendingOpen`, `Preview_OpenRunsOffDispatcher_AndStopPreviewPreemptsIt` |
| NXT-20 | Detach-before-release + **hold-across-reload**. Cue reload: lines still bound by the new model keep their hold and output (no release→re-acquire churn ⇒ no idle-slate flash); dropped lines are detached from the live compositions (`RemoveCompositionOutputAsync`) *before* release. Deck source-switch: the same detach loop the stop path already had now runs before its release/re-acquire block. | `MainViewModel.cs`, `MediaPlayerViewModel.ShowSession.cs` | HaPlay suite (527) + the hardware-check list below |
| NXT-21 | No UI-thread sync-block on the session loop: deck open and cue reload `await LoadDocumentAsync` (UI context kept). The cue reload became a **single-runner coalescing loop** (`ReloadCueShowSessionAsync`: an overlapping trigger marks the graph dirty and shares the in-flight task; the runner re-loops until clean — single-holder line acquisition can never double-run) and `EnsureCueShowSessionCurrentAsync` awaits it, keeping its dirty-after ⇒ throw contract. | `MainViewModel.cs`, `MediaPlayerViewModel.ShowSession.cs` | HaPlay suite (527) |
| NXT-22 | The load-path warm launch suppresses `ExecutionContext` flow (`SuppressFlow` + `Task.Run`), so the dispatcher's `AsyncLocal` identity can no longer leak into warm continuations (pairs with the `WarmUpcomingAsync` restructure above). | `ShowSession.cs` | structural (suite-covered) |
| NXT-23 | `CueGraph` execution log bounded to the newest 512 entries (batch-trimmed, O(1) amortized). | `CueGraph.cs` | documented on `ExecutionLog` |
| NXT-24 | `EnumerateOutputDevicesCached` (5 s TTL, lock-guarded — the spec builder runs off-loop) backs `ResolveBackendSampleRate`, so fires / warms / voice fires stop re-enumerating PortAudio devices every time. | `ShowSession.cs` | existing routing tests |
| NXT-25 | Validator: unique audio-output ids; an enabled route must reference an existing cue and a declared audio output or the implicit `_master`. | `ShowDocumentValidator.cs` | `Validator_RejectsDanglingRoutes_AndDuplicateAudioOutputIds` |
| NXT-26 | Live first-frame warm-read is bounded (30 s backstop) and cancellable: `TryWaitLiveFirstFrame` (worker read + wait-with-token; an abandoned read's late frame is disposed by its continuation, and the read unblocks when the failure path disposes the source); the token threads `TryOpen → TryOpenLive` and the live builder's `Cancellation` now applies to it; a cancelled graph wiring disposes the opened asset and propagates as cancellation; the partial-open cleanup is unified for both catch paths. | `MediaPlayer.cs`, `MediaPlayerOpenBuilder.cs` | Players/Session suites + the NDI hardware check below |
| hygiene | `MediaRuntime` doc-comment typo (`OpenFile(Registry, Registry, …)`). | `MediaRuntime.cs` | — |

**Hardware checks for the next real-hardware session:** (a) stop-fade sounds identical (same ramp math, new scheduling); (b) an `ndi://` deck/cue open against a registered-but-silent sender fails bounded (~30 s) instead of hanging, and STOP preempts it; (c) cue-list edits while idle no longer flash the idle slate on still-bound output lines; (d) soundboard rapid-fire on slow media keeps GO/STOP responsive. → **Soak completed by the operator (2026-07-02, “all seems fine”) before the engine deletion was approved.**

## Deck-bug fixes from operator testing (2026-07-02, same day)

Three flipped-default deck bugs reported from real use, root-caused and fixed (suite after: **1,439/1,439**, +3 tests; real-media `SessionSmoke` still exit 0):

| Bug | Root cause | Fix |
|---|---|---|
| Routing an audio device mid-playback → no output | Two layers: `RebuildActiveClipAudioOutputsAsync` removed every output *then* faulted whole on the first bad device — and on a fixed-rate JACK graph `CreateOutput` at the clip's mix rate throws, so one route add silenced everything. | **Per-route error isolation** (`ShowSession.TryAttachRouteOutput`, used by the rebuild AND both fire-path route loops — legacy parity: a bad output logs + the clip plays its remaining routes); the deck audio factory (`BuildDeckAudioLease`) now creates PortAudio outputs itself and, when a device rejects the mix rate, opens it at the line's configured rate behind an egress `ResamplingAudioOutput` (device released via the lease hook). Tests: `AudioRouteRebuildTests` (zero→one rebuild pumps; rebuild + fire survive a bad device). |
| Audio line never shows active in the I/O menu while playing | `TryGetShowSessionLineHealthMetrics` was gated on the deck's *video* lines (`_playerAcquiredLines`) — an audio-only PA line always fell through to the idle engine probe. | Combined video+audio health like the cue path: reverse-map the line to its PA backend device id / NDI carrier-audio key and fold `GetActiveAudioPumpStatsByDevice` into the score. |
| Hold image doesn't work (+ picker flyout needs sideways scrolling) | Under the flipped default every HOLD path gated on the null legacy `_session` (playback no-op); the idle slate still *created a second SDL window* (pre-port behavior — previews now persist and playback acquires them, `StopPreviewsForPlayback` is a no-op); the flyout had `MinWidth` only, so a long path blew it past the screen. | HOLD during ShowSession playback = the hold image rendered at the composition canvas and held in the top-most full-canvas layer (`ApplyShowSessionHoldImageAsync` via `SetCompositionTestPatternAsync`; re-applied per track); idle slate now **acquires the line's persistent sink** (single-holder, wrapper never disposes the borrowed inner, released on dispose → idle preview restored) and covers all local engines, not just SDL; ShowSession **audio-only** playback keeps the slate allowed (deck holds no video lines) for the classic "audio + logo" use; flyout got `MaxWidth` so Browse stays reachable. |

## Post-deletion follow-ups (2026-07-02, same day — all HARDWARE-VERIFIED by the operator)

The two deliberate gaps recorded with the NXT-13 engine deletion, plus one operator UX request, closed the
same day. Suite after: **1,412/1,412** across all 14 projects (+3 tests), real-media `SessionSmoke` exit 0.
**All three confirmed working on real hardware by the operator (2026-07-02).**

| Item | Implementation |
|---|---|
| **Deck VU meters** (dark since the engine deletion — the legacy engine owned the peak meters) | The deck's ShowSession audio-output factory (`BuildDeckAudioLease`, `MediaPlayerViewModel.ShowSession.cs`) wraps every resolved lease output in `MeteringAudioOutput.Wrap` (taps register on fire, unregister via the lease `Release` hook). `MeteringAudioOutput` is now disposal-transparent (`Dispose` forwards to the inner output), so the session's `DisposeOutputOnRuntimeDispose` ownership semantics are unchanged. `PollAudioMeters` (max peak across active taps → `PeakLevelDb`) runs on the deck's 250 ms ShowSession poll tick and parks at −∞ when the poll stops. Meters everything the deck routes, including NDI carrier audio. |
| **Bare-file-EOF cue auto-follow** (a plain-Stop file cue with no trim/fade/loop started no end monitor → idled at EOF, `ClipNaturallyEnded` never fired) | New opt-in `ShowClipBinding.NotifyNaturalEnd` — set by `HaPlayShowMapper` for `FilePlaylistItem` cues ONLY (images/text hold deliberately, live inputs never end; the deck mapper never sets it, so deck playlist auto-advance UX is unchanged). Flagged clips join the end monitor: released + `ClipNaturallyEnded` at the duration out-point, **plus stall-at-EOF detection** — an audio-clocked clip (`player.SampleRate > 0`; video-only/held clips excluded, their clock legitimately idles) whose clock stays stopped ≥ 5 monitor ticks (500 ms) without a host pause is treated as source EOF (covers VBR/imprecise-duration files whose position never reaches the metadata out-point). New `TransportGroup.PausedByHost` (set by `Set(All)PausedAsync`, cleared on clip replace) keeps an operator pause from reading as EOF. Tests: `NotifyNaturalEnd_BarePlainStopClip_ReleasesAndRaisesClipNaturallyEnded` (Session), `NotifyNaturalEnd_SetForFileCues_NotForHeldOrLiveSources` (HaPlay mapper). |
| **Progressive waveform** (operator request: the scrubber waveform popped in only after the whole file was analysed) | `WaveformExtractor.ExtractAsync` gained an `onPartial` callback: throttled (~150 ms) snapshots of the buckets analysed so far, normalized against the running peak so the shape reads correctly mid-analysis (the final result is normalized globally, exactly as before). Both consumers — the deck scrubber (`MediaPlayerViewModel`) and the cue-editor waveform (`CuePlayerViewModel`) — post partials to the UI, so the waveform fills in left-to-right during analysis. |

**Hardware checks for these:** hold image during video playback (covers all outputs, clears on toggle-off), hold + audio-only track (slate on video lines), idle hold image on local windows (no extra window appears; window returns to idle preview on un-hold/play), mid-play routing of a JACK/48k device onto a 44.1k file (audio arrives, resampled), I/O LEDs for audio-only deck lines, and that a *known-bad* device mid-play no longer silences the good ones.

## Engine-deletion readiness — refined audit (2026-07-02, evening)

Operator confirmed the deck fixes on hardware. Progress + the now-precise blocker list for deleting
`SoundboardEngine`/`CuePlaybackEngine`/`HaPlayPlaybackSession` (delete all three together with `ShowSessionGate`
once this list is empty — a piecemeal delete would leave the `HAPLAY_USE_SHOWSESSION=0` escape hatch
half-working):

**Closed this pass:** `PortAudioInputPlaylistItem` now plays through the deck ShowSession path
(`padev://<escaped-name>`, audio-only — the deck's last unrouted source kind; image/text/subtitle items are
cue-editor-only). Suite 1,439 green.

**Remaining blockers (all bounded, enumerated by code audit — the deck's live `_session` feature surface is
small; most of the 102 textual usages are engine-path plumbing that deletes *with* the engine):**
1. **NDI per-item options are dropped by the ShowSession deck path** — `LowBandwidth`, `VideoOnly`,
   `AudioMinBufferedDurationMs` (the probed jitter-buffer override!) vanish because the deck re-opens plain
   `ndi://<name>` and disposes its pre-connected configured receiver. Needs option carriage (URI query params
   parsed by `NDIDecoderProvider`, or a configured-receiver handoff into the registry open).
2. **padev per-item config dropped** the same way (`Channels`/`SampleRate`/`HostApi*`/`SuggestedLatency` — the
   provider resolves name → device defaults, ≤2 ch). Same option-carriage design as (1).
3. **Waiting-for-source retry** for an offline live item only engages via the engine's failure path today
   (ShowSession open fails → engine also fails → `EnterWaitingForSource`). The ShowSession path must call it
   directly for live items.
4. **Per-cell audio gain matrix live-apply** on the deck (framework `ApplyActiveAudioMatrixAsync` exists;
   deck sends an int channel map + compound gain — the near-1:1 wire-up the ledger already flagged,
   hardware-gated).
5. Final `_session` sweep — the active member surface is only: hold
   (`ApplyFallbackImage`/`PumpHoldFrames`/`ResubmitLastCachedFramesAt`/`SetHoldFallback` — ShowSession
   equivalents exist), hot routing (`TryAddOutput`/`TryRemoveOutput` — equivalents exist), live-state probes
   (`IsLive`/`LiveHasVideo`/`LiveHasAudio`/`IsLiveSourceDisconnected`/`HasWiredLine`), `Player`
   position/clock reads, and `TrySetOutputMatrix`/`AttachMediaPlayerSubtitles` (equivalents exist). Everything
   else is guards/assignments.

**Non-blocker discovered:** `HeadphonesCueEnabled`/targets/tap-points are persisted + shown in the UI but have
NO playback consumer on either path (the wiring never made the port) — a vestigial feature to either re-wire
onto ShowSession later or remove from the UI; it does not gate deletion.

---

# Part 5 — Simplifications and optimizations

Ordered by leverage. Items 1–3 restate the standing plan with updated numbers; 4+ are new.

1. **Delete the engines (the standing NXT-06/13 endgame) — unchanged, re-affirmed.** All transport parity is on `ShowSession`; the fallback has soaked since 2026-07-01. Order: `SoundboardEngine` (2 files) → `CuePlaybackEngine` (9) → `HaPlayPlaybackSession` (19, gated on deck Stage 3: **102** `_session` usages across `MediaPlayerViewModel` + `.Transport`/`.Playlist`/`.Configuration`). Then remove `ShowSessionGate` + both branches of every gated callback, and the dead engine event subscriptions in `MainViewModel`. **Do NXT-18/19/21 first** — once the fallback is gone, a dispatcher stall has no escape hatch.
2. **God-object inventory (updated).** Production files >800 lines in `next/`: the four UI clusters (`MediaPlayerViewModel` 3,626 — *grew* during the deck re-back; `ControlWorkspaceViewModel` 2,926; `CuePlaybackEngine` 2,512†; `MainViewModel` 2,425; `CuePlayerViewModel` 2,072; `HaPlayPlaybackSession` 1,924†; † = deleted by item 1) and the framework five (`MediaContainerSharedDemux` 2,453; `ShowSession` **2,074** — grew ~1,000 lines in remediation; `AudioRouter` 1,789; `GlVideoCompositor` 1,669; `ClipCompositionRuntime` 1,527). `ShowSession` now visibly combines five separable responsibilities (document load/validate/swap; fire/GO orchestration; per-clip commit + fades/monitors; preview/voices; lock-free view publication) — after NXT-18/19 land, split along those seams (they are exactly the seams those fixes touch: a `CueFireOrchestrator` and a `VoicePlayer` fall out naturally).
3. **`MainViewModel`'s ShowSession wiring wants to be a class.** `TryWireShowSessionCueTransport` is ~300 lines of callback assignment inside an already-2,425-line VM, plus reload/debounce/poll/health helpers. Extract a `CueShowSessionCoordinator` (owns `_cueShowSession`, `_cueVideoOutputs`, the acquired-line list, the polls, and reload) — it also gives NXT-20's detach-before-release one home instead of two.
4. **One stop-fade implementation.** After NXT-18 there will be three fade ramps (`StartFadeIn`, `StartNaturalFadeOut`, the stop fade) sharing shape but not code — fold into one `FadeRamp` helper (stopwatch → scale → `ApplyFadeLevel` → commit), parameterized by direction and completion action.
5. **Snapshot-backed remaining queries (NXT-16 residue).** `GetCueDefinitionsAsync` (per fire-failure lookup + UI), `GetVoiceProgressAsync` (200 ms soundboard poll!), `IsVoicePlayingAsync`, `GetPreparedCueIdsAsync` still round-trip the dispatcher. Cue definitions change only at load — publish them with the group views; voice progress can read a volatile voice-view like the audio pumps do. Cheap, and it decouples the polls from any future long command.
6. **Micro-allocations in hot-ish paths** (post-NXT-11 leftovers): `Snapshot()` allocates a fresh array per 250 ms poll ×(deck+cue) — fine; but `GetActiveAudioPumpStatsByDevice` allocates a dictionary per 1 Hz health poll per line — pass a reusable buffer or return the view; `PublishGroupViews` rebuilds both views on every clip replace (fine) *and* every `GetOrAddGroup` (fires for every snapshot of a not-yet-seen group id — harmless today since groups are load-driven, worth a comment).
7. **`MediaRuntime` doc-comment typo** (`OpenFile(MediaRuntime.Registry, MediaRuntime.Registry, …)`) and the `HaPlay_Test` assembly name (NXT-15) — trivial, but both are the kind of leftover that erodes trust in comments; sweep them with the next touch of those files.

---

# Part 6 — Consolidated status and recommended order

**Where the rewrite actually stands (2026-07-02, post-remediation):** Phases 0–7 done and gated; Phase 8 default-flipped with engines as fallback; all prior blockers fixed and verified; **NXT-18…26 fixed same-day (see the Part-4 remediation log); 1,436 tests green**; the open architectural gates are NXT-04 (timeline contract + measured sync), NXT-09/10 (plugin host + first-class surfaces, deliberately deferred until YouTube/MMD), NXT-11/14/15 remainders (benchmarks, adversarial/perf tests, CI gates incl. HaPlay AOT), and the engine deletion.

**Recommended sequence from here:**

1. ~~**Dispatcher-stall family:** NXT-18/19/21/26~~ — **DONE (2026-07-02)**, with the blocked-open/stop-latency regression tests.
2. ~~**Lease-ordering:** NXT-20~~ — **DONE (2026-07-02)** at both HaPlay call sites (detach-before-release + hold-across-reload); the optional structural variant inside `LoadDocumentCoreAsync` remains a nice-to-have for future non-HaPlay hosts.
3. ~~**Hardware soak of the fixes** (checklist at the end of the Part-4 remediation log), then **engine deletion** (Stage 3 → delete → drop `ShowSessionGate`)~~ — **DONE 2026-07-02**: soak completed by the operator, then the deletion executed in one pass (see NXT-13 row). ShowSession is the only runtime; there is no `HAPLAY_USE_SHOWSESSION` escape hatch anymore.
4. **Small-batch hygiene:** ~~NXT-22…25~~ (done), ledger/plan-doc drift (Part 2.2), `HaPlay_Test` rename (left alone deliberately — renaming the output binary may break launch scripts; do it with the AOT re-audit).
5. Then the standing gates in the prior review's order: Gate 3 (timeline contract + measured sync + perf budgets), Gate 4 exit (view-state-only persistence), Gate 5 (YouTube — salvage old `S.Media.FFmpeg.Encode` for the remux), Gate 6 (MMD prototype drives the surface ABI; only then freeze ABI v1).

**Bottom line:** the remediation since `79b70ed` is real, verified, and in places ahead of its own ledger. The new findings are all in the newest code and share one root cause — the serial dispatcher's contract (“never await long work on the loop”) is enforced for cue fires but not yet everywhere — plus one ordering bug on a borrow/release boundary. Fixing that family closes the last *correctness*-grade risks on the default path; everything after is the planned architectural completion work, not firefighting.

## Verification appendix

```bash
dotnet build next/MFPlayer.Next.sln            # 0 errors
MFP_PORTAUDIO_HOST_API=JACK pw-jack dotnet test next/MFPlayer.Next.sln --no-build
# review baseline: 1,428 passed / 0 failed across 14 test projects
# post-remediation (NXT-18…26): 1,436 passed / 0 failed
# post-engine-deletion (NXT-13): 1,409 passed / 0 failed (−38 engine tests)
# post-follow-ups (VU meters / NotifyNaturalEnd / progressive waveform): 1,412 passed / 0 failed

MFP_PORTAUDIO_HOST_API=JACK pw-jack dotnet run \
  --project next/MediaFramework/Tools/SessionSmoke -- /run/media/sekoree/512/mambo.mp4
# post-remediation: exit 0 (fire + seek + composite/subtitles + trim + loop + fade + fan-out)

git log --oneline 79b70ed..HEAD               # 16 remediation commits
git diff --stat 79b70ed..HEAD                 # 85 files, +8,411 −552
```

Key evidence read end-to-end for this review: `ShowSession.cs` (2,074 lines), `SessionDispatcher.cs`, `CueGraph.cs`, `ShowDocumentValidator.cs`, `ShowDocument.cs`, `ClipStandbyEngine.cs` (arm/refresh paths), `ClipCompositionRuntime.cs` (pump/output lifecycle), `MediaPlayer.cs`, `MediaOpenResult.cs`, `MediaHost.cs`, `MediaRegistry` surface, `NativeApi.cs`, `MediaRuntime.cs`, `ShowSessionGate.cs`, `MediaPlayerViewModel.ShowSession.cs`, `MainViewModel.cs` (ShowSession wiring + reload), `HaPlayShowMapper.cs`, `MediaPlayerShowMapper.cs` (surface), `HaPlay.Desktop.csproj`, `.github/workflows/next-build.yml`; old-tree inventory via project/file enumeration (`S.Media.FFmpeg.Encode`, `S.Media.SkiaSharp`, `S.Media.Effects`, `JackLib` reference scans).

Limitations: static review + build/test execution on this dev box; no hardware A/V-sync or soak measurements this pass (the NXT-04/11 measured gates remain the standing ask); smoke tools not re-run (unchanged since the prior review's execution baseline).
