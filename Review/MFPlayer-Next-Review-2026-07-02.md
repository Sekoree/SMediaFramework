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

## Post-review implementation pass (2026-07-02, evening — after the engine deletion)

Part-5 and residue items executed and verified (full suite **1,412/1,412** after every step; real-media
SessionSmoke exit 0; headless app launch clean):

1. **Structural split (Part-5 items 2–4) — DONE.** `FadeRamp` (one ramp loop; the clip fade-in, natural
   fade-out, stop fade, and voice fade-out are now step lambdas over it — voice fades also moved from the
   coarser 100 ms monitor step to the shared 25 ms rate, the one deliberate behavior change);
   `VoicePlayer` (voices + preview out of `ShowSession`, claim-CTS/monitor semantics preserved verbatim);
   `CueFireOrchestrator` (fire-lock + in-flight-fire cancellation; GO's select/advance stay internal
   dispatcher ops on the session, now explicitly `_showGeneration`-guarded); `CueShowSessionCoordinator`
   (the entire cue-workspace ShowSession surface out of `MainViewModel`: wiring, NXT-20 lease ordering in
   ONE home, coalescing reload, progress polls, health, shutdown). Sizes: `ShowSession` 2,459 → ~1,990;
   `MainViewModel` 2,405 → ~1,460.
2. **NXT-16 residue (Part-5 item 5) — DONE.** `GetCueDefinitionsAsync`/`GetPreparedCueIdsAsync` read the
   internally-locked graph/standby directly (volatile graph ref); `IsVoicePlayingAsync`/
   `GetVoiceProgressAsync` read a published voice view — no query round-trips the dispatcher anymore.
3. **`MediaRuntime` post-shutdown resurrection (NXT-05 note) — guarded** (loud error + debug assert; still
   rebuilds so a straggling teardown path can't crash the exit).
4. **Plan-doc drift (Part 2.2) — fixed** (Phase-8 checklist rewritten around the pivot; 02 project table
   annotated; README ABI-experimental caveat).
5. **HeadphonesCue/SharedHeadphonesBus — REMOVED (operator decision).** The persisted-but-never-consumed
   monitor-send surface (config props + tap enum, bus model/project list, three VMs, both view sections,
   strings) is deleted; a legacy-compat test asserts old projects carrying the fields still load (unknown
   JSON members drop silently).
6. **NXT-04 first measured gate — SHIPPED.** SessionSmoke now measures A/V sync quantitatively (exit codes
   17–21): every host-fanned composited frame's media PTS vs the lock-free playhead → steady-state
   bias/jitter, post-seek SHIFT (the audio-ahead-after-seek class), stale-frame detection, pause hold, and
   resume shift. Tolerances: |median| ≤ 250 ms, jitter ≤ 120 ms, seek/resume shift ≤ 150 ms, pause PTS
   advance ≤ 100 ms. Measured on this box (two files, three runs): bias −78…−79 ms (the mixer's
   nearest-not-ahead selection, ~¾ frame), jitter 1–3 ms, shift 0 ms everywhere — tight and reproducible.
   The full NXT-04 timeline/discontinuity CONTRACT (generation IDs threaded to every consumer) remains the
   architectural remainder; this closes the "no measured gates" half.
7. **NXT-15 — CLOSED (same evening).** (a) The ported app gained a real `HAPLAY_SMOKE` launch gate: first
   rendered frame (via `RequestAnimationFrame` after window open) → `TryShutdown(0)` through the app's
   NORMAL teardown — which is now wired to BOTH `ShutdownRequested` and `Exit` (idempotent), so forced
   shutdowns also release native holds — with a 45 s watchdog exiting 2 on a wedged launch. Verified JIT
   and AOT under xvfb (teardown log shows `Pa_Terminate` + `MediaRuntime shut down`, exit 0). (b) The
   **HaPlay AOT re-audit PASSED**: `PublishAot=true` is now the deliverable default — the linux-x64 publish
   emits exactly ONE warning (the known Mond `StackFrame` IL2026) and the native app launches/renders/
   tears down clean. (c) The `HaPlay_Test` leftover is gone — the exe is `HaPlay.Desktop` (the plain
   `HaPlay` name belongs to the UI library for `avares://`). (d) `next-build.yml`: the subtitle and GL
   smokes are GATING (deps pinned via apt; both verified locally with the exact CI args), `libportaudio2`
   added, and a gating **HaPlay AOT publish** (both OSes) + **Linux launch smoke** (exact CI command
   verified locally end-to-end) landed; the Windows launch run is best-effort until it has green history.
   Remaining niche: a Windows dynamic-plugin compile leg (MSVC plugin build — untested from this box).
   *(Amendment, same evening: the first CI run hit missing runner native deps (portaudio/portmidi etc.) —
   the operator deferred CI iteration, so the launch/subtitle/GL smokes were set back to best-effort with a
   promotion note; the AOT publishes stay gating (pure compile/link).)*
8. **NXT-04 contract, first structural slice — timeline discontinuity generations.** `TransportGroup` now
   carries a `TimelineGeneration` bumped on every discontinuity — seek (single + group barrier), loop wrap,
   pause/resume, clip replacement — exposed lock-free via `TransportSnapshot.TimelineGeneration`. Consumers
   stop inferring: the end monitor's stall-at-EOF window and the deck's end-confirmation debounce restart on
   a generation CHANGE (authoritative — covers discontinuities the deck didn't initiate, e.g. control-surface
   seeks, where the old seek-in-flight flag was blind). Tests: session generation-bump test (the fake audio
   source became `ISeekableSource` to allow transport seeks against doubles) + 2 deck window-restart tests;
   suite 1,415/1,415; the measured sync gates (item 6) still read shift ≈ 0 ms through the changed paths.
   Remainder of NXT-04: rate state + cue-local origin/trim + live correlation on one contract object, and
   threading it into compositions/subtitle feeds (they currently self-correct via the mastered playhead).
9. **NXT-11/14 first perf gate — steady-playback allocation budget (exit code 22).** SessionSmoke measures
   `GC.GetTotalAllocatedBytes` + gen0 collections over a QUIET 1.5 s window of full A/V playback (no skew
   sampling running, so the measurement doesn't pollute itself). Healthy baseline on the dev box (three
   runs, two files): **59–60 KiB / window (≈40 KiB/s), gen0 = 0** — the hot decode→route→composite→fan-out
   paths are allocation-free; the residue is the 100 ms monitors' Task machinery. Budget pinned at 512 KiB
   (~8×): a per-audio-chunk buffer regression (≈600 KiB/window) or any per-frame canvas allocation
   (MiB-scale) trips it; timer jitter never does. Deliberately NOT taken: the Part-5 §6 micro-allocs
   (per-poll dictionaries at ≤1 Hz — measured noise next to this baseline) and the sustained
   1080p60/multi-output benchmarks (hardware/nightly territory, still open).
10. **CI test-environment failures + hang (first real full-suite CI run after the NXT-15 trigger fix)** —
   from the operator's runner logs, four distinct problems, all addressed:
   (a) `AdaptiveRateAudioOutputTests` ×4 failed on BOTH OSes — the FFmpeg.AutoGen dynamic bindings can't use
   the runner's FFmpeg (absent on Windows; version-mismatched on Ubuntu → `NotSupportedException` from
   `av_channel_layout_default`). New `FFmpegNativeFactAttribute` (the `LibAssFact` pattern: probe a trivial
   resampler once, skip with the reason) applied to the resampler-backed tests.
   (b) `ResolveMidiDevices_*` ×2 failed — the tests inject a fake port catalog but the flow's
   `IsMidiAvailable` guard asked the REAL portmidi first. The availability probe is now a test seam beside
   `MidiCatalogProvider` (`MidiAvailabilityProbe`, prod default unchanged); the tests inject `true` and now
   pass on any runner — the resolution logic under test is pure.
   (c) `FireVoice_RunsOffDispatcher…` flaked on the loaded 2-core Windows runner — the 2 s/5 s `WhenAny`
   windows were too tight. All windows in `StopFadeAndVoiceOpenTests` widened (15 s probe / 20 s completion;
   test 1's fade raised to 30 s so the parked-loop regression still discriminates), and its probe fixed to a
   real marshaled op — `GetCueDefinitionsAsync` had become lock-free (item 2) and no longer proved anything.
   (d) **HaPlay.Tests HANGS on the runner** (both OS logs stop before its summary; not reproducible locally
   even without JACK — it needs the runner's truly-missing natives). Bounded + made diagnosable: the CI test
   step now runs `--blame-hang --blame-hang-timeout 10m`, so the next run kills the wedged testhost and
   NAMES the started-but-unfinished tests instead of stalling silently. `libportmidi0` added to the runner
   so the app-level MIDI probe behaves like a real install. Follow-up: read the blame output of the next CI
   run and fix the named test.
   **Outcome (next CI run, same evening): the hang is GONE on both OSes** — every assembly completes; it was
   downstream of the now-fixed MIDI/FFmpeg environment cascade. The run surfaced ONE last failure, identical
   on both OSes: `OutputPresetVideoSourceTests.TryReadNextFrame_ConvertsUyvyLiveInput_ToBgraPresetRaster` —
   the UYVY→BGRA test needs FFmpeg's swscale, dead on the runners for the same root cause as (a) (Windows
   has no FFmpeg; Ubuntu 24.04's FFmpeg 6.1 doesn't match the bindings' expected major, so every dynamic
   binding throws `NotSupportedException`). Fixed with a HaPlay.Tests `FFmpegNativeFactAttribute` probing
   `VideoCpuFrameConverter.CanConvert` (a real swscale context open). NOTE for later promotion work: the
   runner's apt `ffmpeg` is currently USELESS to the managed tests — to make the FFmpeg-native tests RUN on
   CI (not skip), pin an FFmpeg build matching FFmpeg.AutoGen's expected major version.

11. **CI round 3 — two Windows test races + the Linux outbound C-ABI smoke NRE.**
   (a) `ShowSessionTests.StopAllAsync_FadesCompositionLayerBeforeRelease` failed on the Windows runner
   ("stop returned before its fade, 7.9 ms"): the fake video clip is only 1 s, so with `FadeOut=180 ms` the
   NATURAL fade-out window opens at 820 ms — a slow runner reaches it before `StopAllAsync`, the natural fade
   claims first, and the stop's lost-claim path returns without ramping (documented behavior, wrong test
   setup). `SyntheticVideoSource`/`FakeVideoDecoderProvider.Registry` now take a frame count; the test uses a
   30 s clip so the stop always owns the fade.
   (b) `AudioRouterControlTests.NaturalEof_FlushesFlushableOutputs` asserted `FlushCount` the instant
   `IsRunning` flipped — but the run loop clears the flag BEFORE `FinishRunLoopThreadLifetime` flushes, so a
   preemption between the two failed it. The test now polls for the flush with a 2 s deadline.
   (c) Linux `mfp_session_load_show` failed with a bare "Object reference not set…" `last_error`. NOT
   reproducible locally: the exact CI recipe (fresh AOT publish + gcc + run) passes exit 0 on this box, and a
   new managed regression test (`LoadDocument_TheCAbiSmokesEmptyShowJson_OnABackendlessSession_Loads`) passes.
   Hardened anyway per NXT-12 — `ShowDocumentValidator` null-guards every top-level collection and
   `LoadDocumentCoreAsync` normalizes null collections to `[]` before validation (a minimal/older JSON omits
   later-added arrays; source-gen leaves missing positional params null) — and `NativeApi` error surfaces
   enriched from `ex.Message` to the full `ex.ToString()`, so if the runner still fails, `last_error` names
   the exception type and AOT frame instead of a bare message. Post-fix: full suite 1,416 passed / 0 failed;
   local C-ABI smoke exit 0.

12. **CI round 4 — one more release-lag test race + a REAL startup crash the Windows launch smoke caught.**
   (a) `EndAtDuration_StopsAHeldClip_ViaTheMonitor_WithoutSourceEof` failed on the Linux runner
   (`ClipDuration` still 400 ms after `IsRunning` flipped): the poll loop exits on the FIRST observable
   transition (stop) but the release that zeroes `ClipDuration` lands one dispatcher op later. The loop now
   polls for the terminal state (stopped AND released, 10 s deadline); same fix applied to the sibling
   `NaturalEnd_PlainStop_ReleasesClipAtOutPoint` (identical pattern, hadn't flaked yet). The FreezeLastFrame
   sibling is safe as-is (asserts attached, which holds from the instant the stop is visible).
   (b) **The Windows HaPlay launch smoke did its job**: on a machine without the portaudio native library the
   app hard-crashed in the MainViewModel ctor — `CuePlayerViewModel.RefreshPreviewAudioDevices()` calls
   `PortAudioDeviceCatalog.EnumerateOutputDevices()` directly, bypassing MediaRuntime's module guard
   (MediaRuntime itself degraded correctly to miniaudio; the unguarded picker then threw
   `DllNotFoundException` to the process boundary). Fixed app-wide with a cached `RuntimeModules`
   PortAudio probe (the MIDI/NDI pattern): the preview picker, both add-PortAudio-device dialogs
   (which surface the unavailable reason via `ValidationMessage`), all guarded;
   `PortAudioOutputRuntime` activation was already caught by `OutputManagementViewModel`. Verified: full
   suite 1,416 / 0; local xvfb HAPLAY_SMOKE launch exit 0. The smoke stays best-effort until it has a green
   history on both runners, but this is the first regression it caught — promotion candidate.

13. **CI round 5 — two more Windows timing races + an unnamed Linux HaPlay.Tests hang.** Round-4 fixes all
   held (Session tests green on both OSes; skips active).
   (a) `AudioRouterControlTests.SetRouteGain_RampsCleanly_FirstChunkInterpolatesFromOldToNew` failed on
   Windows ("no ramp chunk; countBefore=35 total=61"): the capture output sits behind a pump that DROPS on
   overflow for non-primary outputs, so the ONE ramp chunk a mutation produces can be lost on a loaded
   runner before it ever reaches the capture list. The test now retries the mutation (steady 1.0 → 0.5,
   up to 5 attempts, fresh ramp chunk each time) until a ramp chunk is actually captured — the
   interpolation assertion itself is unchanged.
   (b) `CuePlayerViewInteractionTests.Go_DispatchedStatusMessage_IsRaisedOnUiThread` failed on Windows at
   its 2 s `PumpUntil` window — the cue executor hops through `Task.Run` on a thread pool the parallel test
   collections keep saturated. Both Go tests widened to 20 s (early-exit on success).
   (c) Linux hung inside HaPlay.Tests (log stops after the UYVY skip) but the job was canceled at ~6 min —
   BEFORE the 10-minute `--blame-hang` report could name the hung test. Not reproducible locally (HaPlay.Tests
   495/495 in 3 s). `--blame-hang-timeout` cut to 4 m (~10x the slowest assembly's normal runtime) so the
   next hang produces a NAMED failure instead of a mystery cancel; if it hangs again, let the step run to
   the blame report. Post-fix: full suite 1,416 / 0 locally.

14. **NXT-04 authoritative transport timeline — SHIPPED.** Added one long-lived `TransportTimeline` per
   transport group. Its immutable snapshot carries monotonic master/output time, source time, cue-local time +
   origin, trim bounds, playback rate/running state, live/file correlation policy + anchor, and the existing
   discontinuity generation. Seek and loop now explicitly rebaseline `SessionClock` so source time can jump
   without making master time jump; pause/resume, freeze, replacement, and stop re-anchor/clear the same
   contract. `TransportSnapshot` publishes it while keeping the old fields; `ClipCompositionRuntime` uses its
   source coordinate for frame selection and subtitle event time, and its master coordinate for pump/output
   cadence. Live compositions now use this path too instead of the former free-running exception. Added 7
   tests (mapping/rate/live policy, master monotonicity, trim/cue coordinates, seek re-anchor, live composition,
   subtitle timing). Verification: solution build 0 errors; **1,423/1,423** tests green. The remaining NXT-04
   work is the hardware/scheduled tier: sustained physical multi-output presentation-skew gates and exposing
   this same contract to first-class plugin surfaces when NXT-10 lands.

15. **CI round 6 — one more GO-settle race (both OSes otherwise green; the HaPlay.Tests hang did NOT
   recur).** `CuePlayerViewModelTests.Go_InvokesMediaCueExecutor` waited a fixed 20 ms after `GoCommand`
   before asserting the executor ran — the trigger plan is async and the loaded Linux runner missed the
   window. Now polls via the file's existing `WaitUntilAsync` (20 s ceiling, early exit); the sibling
   `GoAdvancesSelectionToNextFireableCue` had the same fixed-sleep pattern (2×50 ms) and got the same
   treatment preemptively. (The group pre-wait test's fixed delays are ORDER-sensitive by design — 80 ms
   pre-wait windows — and were left alone; a polling rewrite would not strengthen them.)

16. **Remaining-board closure pass (2026-07-02, evening): D10 + hygiene pair.**
   (a) **D10 CLOSED (post-pivot resolution).** UI layout already lived in a per-machine sidecar; the real
   gap was that a saved show was never persisted as the framework document. New `ShowDocumentSidecar`
   (HaPlay/Models): every full project save also writes one mapped+validated `ShowDocument` per cue list
   (`<project>.show.<n>.json`, produced by the same `HaPlayShowMapper` the live session uses and gated by
   `ShowDocumentValidator`, so a headless/C-ABI host can't reject it at load); stale sidecars are cleaned
   up when the list count shrinks; sidecar errors surface in the save status but never fail the save. The
   FULL descent of `HaPlayProject` into Session was deliberately NOT done: decks/playlists, soundboards,
   control graphs and action endpoints are app-domain, not show-execution data (the original D10 wording
   predates the lift-and-shift pivot). Plan docs updated (08-Open-Decisions D10 resolution note;
   09-Phase-Checklists Phase-8 exit item checked). Tests: `ShowDocumentSidecarTests` ×3.
   (b) **Hygiene pair.** `ShowSession.TryGetActiveAudioPumpStats(deviceId)` — allocation-free
   single-device variant of the per-poll dictionary build; both UI health polls switched (deck + cue
   coordinator); parity asserted in the existing borrowed-carrier test. The NXT-20 "structural variant
   inside `LoadDocumentCoreAsync`" was resolved as a documented factory contract instead of code:
   reordering the transactional load would trade away NXT-12's never-destroy-the-running-show guarantee,
   so the reload-ordering obligation (hand over still-bound exclusive lines; detach dropped lines before
   release) is now spelled out on `_videoOutputFactory`. Post-pass: **1,426/1,426** green.

17. **Gate 5 framework slice — `S.Media.Source.YouTube` SHIPPED (UI slice next).** Per the review's
   Gate-5 design + operator UX requirements (separate audio/video streams, stream selection, caching):
   (a) `FFmpegStreamCopyRemuxer` (S.Media.FFmpeg.Common) — in-process libavformat stream-copy of a
   video-only + audio-only input (or single-input pass-through) into one local MKV, dts-ordered
   interleaving, cancellable, coarse progress, explicit container name for `.partial` temp paths; NO
   shelled ffmpeg. Tests: `StreamCopyRemuxerTests` (real x264+AAC lavfi inputs → remux → reopen both
   tracks through the registry; `RemuxFact` skips where natives/CLI are absent).
   (b) `S.Media.Source.YouTube` — new module referencing the LOCAL YoutubeExplode source
   (Reference/YoutubeExplode-6.6; net10 target, IsTrimmable+IsAotCompatible; declares 0.0.0-dev, i.e.
   pinned by checkout not by the folder name). Canonical URI `youtube://<id>?v=…&a=…&sub=…&novideo=1`;
   watch/share URLs normalize via `VideoId.TryParse`. Stream identity = operator-readable descriptors
   (video `label|codec|container`, audio `codec|container|language`) since this YoutubeExplode exposes
   no itags. `IYouTubeGateway` seam (manifest DTOs for the picker UI: video/audio streams + caption
   tracks; SRT caption download) with `YoutubeExplodeGateway` impl. `YouTubePreparer`: content-addressed
   cache (`<id>-<sha16>.mkv`), "best" resolution → CONCRETE descriptors (returned as `ResolvedSelection`
   for the UI to persist), `.partial` + atomic rename, per-key coalescing, subtitle sidecar `.srt`.
   `YouTubeDecoderProvider`: probe 1.0 canonical / 0.9 watch URLs; RELIABLE MODE — opens only the
   prepared local asset via a private FFmpeg registry; an unprepared open throws actionably (GO never
   starts a network download). Tests: 17 offline (URI round-trip, best resolution, cache hit,
   coalescing 8→1 run, stale-selection error, audio-only+subs, probe scoring, unprepared-open) + 1
   LIVE opt-in (`MFP_YOUTUBE_LIVE_TESTS=1`; ran GREEN locally against a real video: resolve → download
   separate streams → in-process remux → registry playback via the resolved URI). Arch rules updated
   (YoutubeExplode allowed for this module only). Post-slice: **1,446/1,446** green.
   ~~REMAINING for Gate 5: the HaPlay UI slice~~ — CLOSED same day, see item 18.

18. **Gate 5 UI slice — YouTube in HaPlay SHIPPED (Gate 5 complete).**
   `YouTubePlaylistItem` (persisted RESOLVED stream descriptors + cached display metadata + caption
   sidecar selections; polymorphic `kind:"youtube"`), `HaPlayPlaybackHelpers.BuildYouTubeUri`,
   cue+deck mapper wiring (canonical URI; `NotifyNaturalEnd` for auto-follow), MediaRuntime registers
   `YouTubeSourceModule` via the shared `Playback.YouTubeRuntime` (one preparer/cache for provider AND
   dialogs — coalescing works app-wide). `AddYouTubeDialog` (+VM, injectable gateway/preparer): paste
   URL → resolve manifest → pick SEPARATE video stream / audio stream / subtitle track (muxed streams
   are rarely offered) or audio-only → cache-status hint → "Download & add" with per-phase progress →
   item carries the resolved descriptors + prepared `.srt` sidecar. Deck: playlist add-menu entry +
   `youtube://` case in the ShowSession open path (subtitles pass through like file items). Cue player:
   add-menu entry; the cue row persists the caption sidecar, and — gap fix — `HaPlayShowMapper` now maps
   `MediaCueNode.Subtitles` onto the clip (`MapCueSubtitles`, mirroring the deck mapper; previously the
   cue picker's selections were persisted but never mapped to playback). Tests:
   `YouTubeIntegrationTests` ×5 offline (dialog resolve/prepare with caption sidecar; audio-only;
   bad-URL validation; canonical-URI + project-JSON round-trip; cue mapper → URI + sidecar subtitle +
   NotifyNaturalEnd). Post-slice: **1,449/1,449** green; xvfb HAPLAY_SMOKE launch exit 0 with the module
   registered.

19. **Gate 6 prototype — `S.Media.Source.MMD` SHIPPED (review stages 1–2 + camera-placement preview).**
   Pure managed module, zero native deps (arch rule: Core/Time only):
   (a) **Parsers.** `PmxDocument` — PMX 2.0/2.1: UTF-16/UTF-8 text, variable-width indices, vertices
   with BDEF1/2/4 + SDEF/QDEF skinning reads (SDEF/QDEF evaluated as their linear-blend equivalents),
   faces, materials, bones incl. IK data (parsed, not yet solved), vertex/group morphs; other morph
   kinds + trailing sections structurally skipped; every read bounds-checked → `PmxFormatException`
   (truncation fuzz test sweeps the fixture). `VmdDocument` — Shift-JIS names (CodePages), bone tracks
   with the packed per-channel Bezier blocks, morph tracks, camera track, 30 fps timeline.
   (b) **Evaluation.** `MmdAnimator` — MMD Bezier easing (bisection solve, tested), bone track sampling,
   FK with append/inherit rotation+translation (topologically ordered, cycle-safe), vertex morphs, CPU
   linear-blend skinning; camera sampling with VMD conventions. Deterministic pure-function-of-time
   (seek = playhead move; NO physics — review stage 5, deliberately omitted; IK solving is the known
   stage-6 artifact source on dance feet).
   (c) **Render + source.** `MmdSoftwareRenderer` — z-buffered flat-shaded rasterizer, MMD→RH
   conversion, material diffuse + one directional light, back-face culling honoring double-sided
   materials. `MmdVideoSource` (BGRA32, 30 fps, seekable, finite by motion duration) behind
   `mmd://?model=…&motion=…&camera=…` URIs with manual camera-override params; `MmdSourceModule`/
   provider registered in HaPlay's MediaRuntime.
   (d) **HaPlay camera placement.** `MmdPlaylistItem` (model/motion/camera paths + manual camera fields,
   persisted) + `AddMmdDialog`: file pickers and camera controls (distance/target/rotation/FOV sliders +
   preview-time scrubber) driving a LIVE debounced software-rendered preview — the rudimentary 3D
   camera-placement view the operator asked for; deck playlist + cue add-menu entries; mapper/deck open
   paths wired.
   (e) **Tests.** 8 module tests: byte-level PURPOSE-MADE tiny PMX/VMD fixtures (the bundled model/motion
   assets are non-redistributable → their tests are local-gated): parse, truncation fuzz, animation
   sampling incl. deterministic seek-back, Bezier endpoints/ease, video-source frames+seek+duration,
   camera-framing effect; plus the LOCAL-asset test (real YYB Miku + Rolling Girl: 50+ bones, minutes-long
   motion, most vertices moved 20 s in, model visibly renders at 320×180 — ran GREEN on this box) and a
   HaPlay mapper/JSON round-trip test. Post-slice: **1,460/1,460** green; xvfb launch smoke exit 0.
   NEXT (staged): GL renderer with real MMD materials/toon/outline via the first-class layer-surface work
   (NXT-10) — this module is the consumer that drives that ABI before v1 freezes; IK solving; physics.

## Remaining-board closure (2026-07-03) — items 20–25

20. **Sync-gate hardening.** Debug builds scale the SessionSmoke sync sample windows ×3 (`SyncWindowScale`
   — unoptimized CPU compositing runs ~7 fps vs the declared 24, so the count minimums false-failed on
   Debug runs, especially under load); every gate's FAIL message now names the exact tripped clause(s) via
   `FailedClauses` (they used to print thresholds the run had met, sending diagnosis the wrong way).
   Debug AND Release smoke green end-to-end.
21. **MMD IK — SHIPPED (stage 6, the dance-feet artifact source).** CCD solver in `MmdAnimator`: per-step
   unit-angle clamp (`IkLimitRadians`), single-axis hinge projection for knees (Y/Z limits pinned 0 →
   correction forced onto ±X — the classic knee treatment), combined-rotation Euler-XYZ clamping
   (`ClampEulerXyz`, row-vector Rx·Ry·Rz extraction), chain-only world refresh during the solve + ONE full
   settle pass at the end (an 800-bone × loop-40 model stays 30 fps-able), IK deltas reset per Evaluate
   (seek-back determinism preserved). 7 tests (`MmdIkTests`: reachable/unreachable/hinge-plane/hinge-bend/
   determinism/skin-follow/Euler-clamp). The real-asset test (fixed path, see below) runs the solver over
   the full Rolling Girl motion green.
22. **NXT-10 first-class layer surfaces — SHIPPED.** `IVideoCompositorSurfaceHost` capability interface
   (GlVideoCompositor implements it; the CPU compositor deliberately does not — surface-capable sources
   fall back to their frame path); the GL host now OWNS the `ConfigureGl` contract (per-surface
   ConditionalWeakTable, re-configures on canvas change — CompositeTargetsSmoke's manual call removed and
   green on real GL); `VideoCompositorSource` surface slots (add/remove/sort/`HasSurfaceSlots`, locked
   placement snapshots, surfaces composite ON TOP of frame layers — v1 contract); integrated multi-warp
   bypassed while surfaces are present; `ClipCompositionRuntime.AddSurfaceLayer` + `SurfaceLayerSlot`
   (PlacementResolver math with the canvas as source size; Dispose removes AND disposes the surface) +
   `IPlacedClipLayer` unifying frame/surface slots (ShowSession fades + live placement edits work on
   both); ShowSession commit: a single-placement clip whose source implements the new
   `ILayerSurfaceVideoSource` on a surface-hosting composition composites GPU-side with NO frame fan-out,
   rendering at the TransportTimeline's SOURCE time (transport for free). Tests: `SurfaceLayerTests`
   (Compositor ×5) + `SurfaceLayerSessionTests` (Session end-to-end GPU path + CPU fallback ×2).
23. **NXT-09 dynamic plugin host — PRODUCT FEATURE SHIPPED.** New `S.Abi.MediaPluginDirectory`: scans one
   directory, loads every library exporting `mfp_plugin_register` (fail-soft per file: non-plugins skip
   silently, broken ones record failures), `RegisterInto(media/control/compositor)` registries, reverse-
   order refcounted unload (a still-referenced library stays loaded to process exit — never
   unload-while-referenced). HaPlay `MediaRuntime` wires it: `HAPLAY_PLUGINS_DIR` (default
   `<app>/plugins`) loads before the registry build, capabilities register LAST (built-ins keep probe
   precedence), `MediaRuntime.CompositorSurfaces` exposes plugin layer-surface kinds, Shutdown disposes
   plugins AFTER the host. New `S.Abi.Tests` project (×5, gcc-gated real-plugin tests compile the
   canonical `test_plugin.c` at test time). END-TO-END VERIFIED: the app under HAPLAY_SMOKE loads
   `com.example.testplugin` from the plugins dir and its `testaudio` backend appears in the registry
   alongside PortAudio/miniaudio; clean teardown.
24. **NXT-04 hardware tier — cross-output skew gate.** `MultiOutputSmoke` now instruments both fanned
   outputs (per-Submit PTS→monotonic instant), reduces same-PTS pairs to a skew distribution and GATES it:
   p95 ≤ one frame period, one-sided frames ≤ 5% (exit 17/18). `--headless` runs it displayless (CI tier).
   Measured on this box: median 0.01 ms, p95 0.04–0.05 ms, 0 misses (headless AND windowed under xvfb) —
   the fan-out is phase-locked as designed. The operator hardware run (two windows on physical displays)
   is the remaining glass-level check.
25. **MMD GL renderer — SHIPPED (the NXT-10 consumer).** `MmdGlLayerSurface` (in S.Media.Source.MMD, which
   now references S.Media.Compositor — arch rule updated; Silk.NET bindings + StbImageSharp are pure
   managed, the GL context only ever comes from the hosting compositor): renders the skinned scene into
   its own color+depth FBO then quads into the canvas with the layer transform + opacity; real materials
   v1 = per-material diffuse textures (StbImageSharp), procedural two-tone toon ramp, MMD inverted-hull
   edge pass (parsed edge flag/color/size), double-sided flag, material-order alpha. `PmxMaterial` gained
   the toon/sphere/edge fields (parser now reads what it skipped); `MmdAnimator.Evaluate` gained skinned
   NORMALS; `MmdVideoSource` implements `ILayerSurfaceVideoSource` (surface mode switches its frame stream
   to a cached transparent buffer — priming/clocks stay alive, no double render). New `MmdGlSmoke` tool
   (real GL under xvfb, coverage gate + BMP dump for eyeballing): the real YYB Miku + Rolling Girl renders
   correctly (textures/toon/edges visible, framing matches the software reference; one 180° orientation
   quirk between the System.Numerics row-vector clip path and GL sampling is corrected in the blit and
   documented there). Known next slices: sphere maps, per-material/shared toon ramp textures, physics.
   ALSO fixed: `MmdRealAssetTests.AssetRoot` was a stale hardcoded `/home/seko/...MMDTest` path (wrong
   home AND wrong folder name vs `Reference/MMD_Test`) — now repo-root-resolved, so the real-asset test
   actually RUNS on this box (it had been silently skipping).

   Post-closure gates: solution build 0 errors; **1,479/1,479** (the sole skip is the network-gated
   YouTube live test); SessionSmoke Debug+Release green incl. sync/alloc gates; CompositeTargetsSmoke,
   MmdGlSmoke, MultiOutputSmoke (both modes) green on real GL/xvfb; HAPLAY_SMOKE launch (JIT + with a
   loaded plugin) exit 0 with clean teardown.

26. **Operator report "YouTube deck item is instantly done" (2026-07-03) — ROOT-CAUSED + FIXED.**
   Diagnosis path: the module layer was exonerated three ways (new live-gated audio-only test that
   CHECKS DURATION AND DECODES — the old live test only proved the sources open — passes for the exact
   reported video; the on-disk cache asset probes/decodes clean; a deck-shaped headless ShowSession fire
   against the real cache plays perfectly). The operator's session log then showed the truth: playing
   the item never touched the media layer AT ALL. Root cause: `MediaPlayerViewModel.CanLoadMedia()`
   predated the registry-URI item kinds — it admits live items and existing `FilePlaylistItem`s only, so
   a `YouTubePlaylistItem` (AND an `MmdPlaylistItem` — deck MMD playback was equally dead) made
   `OpenOrReloadAsync` silently return: no open, no error, nothing logged. Fix: both kinds are accepted
   unconditionally (the open path surfaces its own actionable errors — reliable-mode "not prepared",
   missing model). Bonus: the deck scrubber waveform now analyses a prepared YouTube item's cached asset
   (`HaPlayPlaybackHelpers.TryGetPreparedYouTubeAssetPath`) — it is a real local file. Tests:
   `MediaPlayerRegistryItemLoadTests` ×2 (playing an unprepared youtube / missing-model mmd item must
   reach the open path and surface ITS error, never sit silently idle) + the live audio-only decode test
   (`MFP_YOUTUBE_LIVE_VIDEO` overrides the video id for reproducing reports). ALSO: the module now
   references the YoutubeExplode NUGET package (operator switch; version pinned centrally) instead of the
   Reference/ local-source checkout — 17 offline + 2 live tests green on the package. HaPlay 506/506.

27. **Operator reports round 2 (2026-07-03) — loop/repeat "stuck at the beginning" + MMD see-through/
   wrong colors/camera — ALL ROOT-CAUSED + FIXED.**
   (a) **Deck loop/repeat/auto-advance never fired at end-of-media (framework bug).** The operator's
   session log showed it plainly: `AudioRouter RunLoop: all sources exhausted, completed naturally` … and
   then NOTHING for minutes. At natural EOF the router stops itself and FLUSHES the hardware output —
   which rewinds the output clock's epoch — while nobody stops the `MediaClock`: the transport then reads
   **IsRunning=true, position=0:00 forever** ("play button on, stuck at the beginning"). Every EOF
   consumer was blind: the deck poll's end-confirm (loop/repeat/auto-advance), the session's
   NotifyNaturalEnd stall detection, the voice monitor. Fix at the one choke point everything reads:
   `MediaPlayer.IsRunning` reports false once the audio router `CompletedNaturally` (cleared on restart —
   resume/loop relap works), and `MediaPlayer.Position` clamps to `Duration` there (the raw playhead
   reads ~0 post-flush). Regression: `AudioExhaustionShortOfMetadataDuration_RaisesClipNaturallyEnded`
   (Session — synthetic source exhausts at 40 ms with 10 s metadata; the event must fire). Two session
   tests that silently RELIED on the never-stopping clock (pause/resume, 30 s stop-fade) got long-lived
   fakes (`FakeAudioDecoderProvider.Registry(chunks:)` is now sizeable).
   (b) **MMD "see-through + wrong colors": the inverted-hull edge pass.** Differential renders
   (`MFP_MMD_GL_NOEDGE/NOBLEND` debug knobs, now permanent) isolated it in two frames: the scene's Z-flip
   inverts winding, so the edge pass's front-face culling kept the CAMERA-FACING expanded shell and
   painted it over the whole model (dark muddy tint + translucent look; teal hair read brown). Channel
   order, Stb decode, UVs, alpha and the readback chain were each verified correct along the way (the
   smoke's new `--diag` stage probes upload + full-chain channel order — note green stays green under an
   R/B swap, so the old green-only check proved nothing there). Fix: edge pass AFTER the main pass,
   opposite cull face, explicit `DepthMask(true)` (host 2D passes may leave depth writes off). Post-fix
   render matches the operator's MMD-editor reference (teal hair, correct grays, opaque, thin outlines).
   (c) **MMD default camera** now matches the MMD editor's default framing (distance 45, target (0,10,0),
   fov 30 — was 35/(0,12,0), "too close"); lateral offsets stay manual (content-specific, dialog sliders).
   Post-round gates: full sln **1,483/1,483** (parallel run, hang-blame armed, clean), Desktop rebuilt.

28. **Operator reports round 3 (2026-07-03) — grayscale MMD in the APP + camera-position XYZ.**
   (a) **In-app MMD rendered the grayscale SOFTWARE raster, not the GL renderer.** The session log showed
   the deck composition using `SDL3GLVideoCompositor` — the app's default GL backend — which did NOT
   implement `IVideoCompositorSurfaceHost` (only the inner `GlVideoCompositor` did), so
   `SupportsSurfaceLayers` was false and the MMD source silently took its CPU frame fallback. Fix: the
   SDL3 wrapper now implements the capability by delegation (EnsureInitialized + context-current + inner
   call, exactly like `Composite`). LESSON: capability interfaces on an inner type are invisible through a
   wrapper — audit every compositor DECORATOR when adding one.
   (b) **Texture resolution hardened for Windows-authored models on Linux:** exact path first, then a
   case-insensitive per-segment walk (`MmdGlLayerSurface.ResolveTexturePath`, tested); missing/undecodable
   textures now log a WARNING naming the file instead of silently rendering white ("black and white"
   diagnosis is one log read away now).
   (c) **Camera-position XYZ (operator request):** the Add-MMD dialog gained direct camera-EYE XYZ fields,
   two-way synced with the orbit form (position = target + back(rotation)·|distance|; inverse pitch =
   atan2(dy,dz), yaw = atan2(dx,√(dy²+dz²))) — the persisted item/URI stay in MMD's orbit form. Dialog
   defaults now match the source defaults (45/(0,10,0)).
   (d) **Physics: confirmed NOT implemented** (deliberately staged — the review's stage 5; rigid-body/
   spring dynamics for hair/skirt remain the known gap after IK).
   Gates: MMD tests 16/16, HaPlay 506/506, MmdGlSmoke green; full sln green in parallel (one solo-pass
   load-flake: `AudioRouterControlTests.Pause_WaitsForInFlightSubmitBeforeFlush`, pre-existing class).

29. **Operator reports round 4 (2026-07-03) — MMD sphere maps + toon textures, MSAA, textured preview.**
   (a) **Sphere maps (.sph multiply / .spa add) + per-material toon ramp textures SHIPPED** in the GL
   renderer — the YYB eyes are almost entirely their ADDITIVE sphere maps ("the eyes have no textures"
   report), and the toon ramps carry the shade tint. View matrix now uploads separately (view-space
   normals → matcap UV `n.xy·0.5+0.5`); toon samples `v = 0.5 − 0.5·N·L` with the procedural two-tone as
   fallback; neutral fallbacks (white/black 1×1) keep un-sphered materials unchanged. The `spa/` and
   `toon/` model folders are now consumed as authored.
   (b) **MSAA (4×) with a toggle**: multisampled scene renderbuffers resolved into the sampled color
   texture; graceful fallback when unsupported; `mmd://…&aa=0` disables; persisted on `MmdPlaylistItem`
   (`Antialias`), dialog checkbox added.
   (c) **The dialog preview (and CPU-compositor fallback) is textured now**: `MmdSoftwareRenderer` gained
   per-material nearest-neighbor diffuse sampling (`SetTextures`/`MmdCpuTexture`, barycentric UVs,
   alpha-cutout texels skipped); `MmdVideoSource` loads the textures lazily via the same case-insensitive
   path resolution as GL. The 320×180-era grayscale preview now shows the real model.
   Gates: MMD 16/16, HaPlay 506/506, MmdGlSmoke (GL + software) renders verified visually — the GL frame
   is near-parity with the MMD-editor reference. Remaining staged MMD work: physics (stage 5), shared
   toon01–10 ramps, per-vertex (smooth) normals in the software raster.

30. **MMD PHYSICS — SHIPPED (stage 5, operator go-ahead).**
   (a) **Parser**: PMX rigid bodies + joints now read (display frames skipped properly to reach them;
   files ending after the morphs — the tiny fixtures — legitimately carry no physics via `Reader.HasMore`).
   `PmxRigidBody` (shape/group/mask/placement/mass/damping/mode) + `PmxJoint` (spring 6-DOF limits).
   YYB probe: 136 bodies (27 kinematic colliders, 106 dynamic links, 3 pivoted), 125 joints.
   (b) **Solver** (`MmdPhysics`): compact position-based (XPBD-style) — kinematic bodies snap to their
   animated bones; dynamic bodies predict under gravity (−98 model-units/s²) with Bullet-style per-second
   damping; joints solve as CHAIN PROJECTIONS (keep tangential motion = the swing, correct only arm-length
   toward the pivot — a plain point-anchor correction freezes the pendulum outright, found by test), plus
   swing orientation from the arm (bones BEND, not shear) and per-joint Euler angular limits (reuses
   `ClampEulerXyz`); collisions = capsule/sphere closest-segment push-outs honoring group masks (boxes
   approximated as capsules along their longest axis — documented); 1/120 s substeps, 4 iterations,
   NaN/finite guards on write-back. STATEFUL by design: backward seeks / >0.5 s jumps reset onto the
   animated pose (MMD's own seek behavior); the deterministic-animator property is explicitly scoped to
   the FK/IK/morph layers.
   (c) **Integration**: `MmdAnimator.Evaluate(..., MmdPhysics?, physicsDeltaSeconds)` steps physics
   between IK and skinning and rebuilds skin matrices from the final worlds; both the GL surface and the
   software source own an instance and track frame deltas; toggle plumbed end-to-end (`mmd://…&phys=0`,
   `MmdPlaylistItem.Physics`, dialog "Physics (hair/skirt)" checkbox, default ON).
   (d) **Tests ×3 + real-asset extension**: horizontal-pendulum swings down and stays bounded (this test
   caught the frozen-pendulum bug), backward-jump re-bases, no-dynamic-bodies → no simulation; the
   real-asset test now simulates ~2 s of Rolling Girl over the full 136-body chain asserting every vertex
   finite/bounded AND visibly diverged from rigid FK. MmdGlSmoke warms 1.5 s at 30 fps before each capture
   so eyeball frames carry momentum; the verification render shows the twin-tails hanging/draping
   naturally instead of the rigid bind angle. MMD tests 19/19; full sln green (same solo-pass flake).
   Known simplifications for later: no twist simulation, no spring constants (limits carry the shape),
   box colliders as capsules, no restitution/friction response.

31. **MMD PHYSICS TUNING — FIXED (operator report 2026-07-03: "hair/clothing way too loose, body parts
   stiff / knees not bending").** Diagnosis first, then five solver defects fixed in `MmdPhysics` +
   `MmdAnimator`:
   *Diagnosis*: knees were exonerated by probe — IK bends to 111° and physics leaves leg bones
   bit-identical (no dynamic bodies attach to core bones on YYB); the "stiff body" was the SKIRT
   misbehaving around the legs, and "loose hair" was real. Collision-mask semantics were also probed and
   confirmed CORRECT (the PMX ushort is the Bullet collides-with mask; head/hair values read sensibly).
   (a) **Authored stiffness was ignored**: YYB tails lock their inner joints to ±0.1°, but the old chain
   projection preserved all tangential motion — limits only clamped rotation while POSITION dangled like
   rope. Joints now solve as swing→spring→HARD limit clamp→position DERIVED from the clamped frame (MMD
   joints lock the linear DOF). New regression test: `NearLockedJoint_TracksTheParentRigidly`.
   (b) **Lattice links vs chains**: skirt bodies carry a 2nd joint (horizontal ring, wrap 15→0); full
   frame-snap per joint let the ring override its waist anchors and collapse the skirt. First-in-file
   joint per body = structural driver (full solve); extra joints = soft anchor-coincidence links,
   relaxation 0.5, CAPPED like contact recovery — uncapped, a flipped ring is collectively stable (each
   plate holds its neighbor up, overpowering restoration; found by tracing t=57–65 with contacts disabled).
   (c) **Contact realism**: thin boxes (skirt plates 0.47×0.665×0.1) now use their SMALLEST half-extent
   as capsule radius (the old averaged radius kept plates permanently penetrating the hip capsule) and
   penetration recovery is rate-capped at 8 units/s (Bullet split-impulse analogue) so a leg sweeping
   through the ring can't blast plates into orbit in one substep.
   (d) **Swing rate cap** (12 rad/s): short-armed plates read garbage arm directions for a few substeps
   when their kinematic anchor teleports through a fast move; the cap blocks 180° single-substep flips.
   The limit CLAMP is deliberately uncapped — near-locked tails must track a whipping head rigidly.
   (e) **Shape restoration**: authored joint springs are now honored (bangs: 5/s toward bind) and every
   free joint gets a baseline restore of 3/s × angularDamping⁴ — the stand-in for the shape-holding that
   authored angular damping produces in Bullet (this solver carries no angular velocity). Skirt
   (damping 0.99999) gets the full rate and holds its authored A-line (≈8° sag at rest); a lightly damped
   0.5 test pendulum keeps true gravity dynamics (≈0.2/s). Damping clamp also raised 0.999→1.0 (hair tips
   are authored 1.0 = velocity dies every step).
   (f) **Re-chain after write-back** (`MmdAnimator`): non-physics descendants of physics-driven bones
   (30 on YYB: tip/hem bones carrying skin weights) now re-chain under their moved parents via the
   extracted `LocalMatrix` helper — they used to stay at the rigid FK pose and tear the mesh.
   *Verification*: skirt vertices now track the authored silhouette (max dev 0.2–0.4 units at t=10/20/40
   vs 4.6 before = flipped-over-the-waist); the violent 56–65 s section sways 8–35° with full recovery
   (was wedged at 150–166° permanently); GL renders at t=10/20/60 match the MMD-editor reference
   (skirt pleats + hanging tails present at all three). MMD tests 21/21 (new rigid-follow test); full
   sln 1,487/0. Remaining known simplifications: no twist, boxes-as-capsules for FAT boxes, no
   restitution/friction, ring links positional-only.

32. **MMD ANIMATOR + PHYSICS — REFERENCE-ALIGNED REWRITE (operator report 2026-07-03: "physics very
   stiff, knees aren't moving"; references added: `Reference/MMDTest/babylon-mmd-main`,
   `SystemAnimatorOnline-master`, ground-truth `mmdTest.mp4`).** Item 31's diagnosis ("knees exonerated")
   was WRONG about the visible mesh: IK solved correctly but the YYB rig skins the legs to D-bones
   (左ひざD append-inherits from 左ひざ, ratio 1, deform layer 1) and the animator folded append BEFORE
   the IK pass from sampled FK only — the knee's rotation exists ONLY as an IK result (1 VMD key), so
   the visible leg never bent (左足首D measured 4.1 units from the solved ankle; 0 vertices weighted to
   IK-driven leg bones, 580 to 左ひざD).
   (a) **Animator restructured to MMD's transform order** (babylon-mmd/Saba semantics): bones process
   ONE AT A TIME stable-sorted by (deform layer, index), split before/after-physics; append reads the
   donor's CURRENT state INCLUDING its IK rotation (`ikRot * animRot`, recursion via in-order folding,
   own-first composition `anim * append` per `appendTransformSolver.ts`); IK solves in place when the
   IK bone's turn comes; already-processed bones re-chain after each solve (the toe-IK chain).
   Parser now reads deform layer, transformAfterPhysics (0x1000), fixed axis (0x0400 — projected at
   runtime like babylon's axis-limit path, twist bones 腕捩/手捩).
   (b) **IK solver ported from babylon-mmd `ikSolver.ts`** (System.Numerics ops map 1:1 — verified
   empirically): limit REFLECTION during the first half of iterations (the straight-knee bootstrap),
   per-link step scale `unitAngle·(chainIndex+1)`, limit-adaptive Euler order (YXZ/ZYX/XZY + 88° clamp),
   fully-locked links skipped, hinge axis snapped by parent-frame sign.
   (c) **Physics rewritten as a sequential-impulse rigid-body solver** (replaces the heuristic PBD whose
   hard per-substep limit clamps were the "very stiff" root cause): real inertia tensors, linear+angular
   velocities, uniform 6-DOF joints in Bullet's Euler-XYZ convention with per-axis linear/angular limits
   AND authored springs, contacts with friction/restitution and warm starting, jointed pairs excluded
   from collision (Bullet's disableCollisionsBetweenLinkedBodies), babylon's 5° angular-limit clamp
   (tiny ranges → locked equality rows), katwat PhysicsWithBone→Physics adjustment, type-2 bodies
   re-seeded from the bone each frame (three.js `_setPositionFromBone`). Solver architecture is
   split-impulse: BIAS-FREE warm-started velocity rows + post-integration NGS position pass — angular
   corrections rotate each body ABOUT ITS OWN JOINT ANCHOR (all-locked joints correct the FULL relative
   rotation, no Euler decomposition), linear/contact corrections are translate-only; both choices are
   load-bearing (centre-rotations + rotational linear fixes ping-pong divergently through I⁻¹r² ≫ m⁻¹
   on light chains — found via staged instrumentation, NGS was AMPLIFYING error 0.005→0.30 rad/substep).
   Item 31's invented tuning (swing caps, damping⁴ shape restore, driver-vs-lattice split) is deleted;
   stiffness now comes from the authored joints themselves.
   *Verification*: knee probe ankleD→ankle 4.1→0.0000 at 8 timestamps; IK convergence ≤0.009 units;
   static hang settles (tail 2.4 units of authored ±10° root sag, stationary from t=2s); 40 s dance run
   no NaN/explosion, tail tip tracks head with up to 15-unit flowing lag, 130 fps for full
   evaluate+physics+skin; locked-pendulum test lands EXACTLY on the rigid solution (0.707,10.707);
   software renders at t=20/47 match `mmdTest.mp4` posing (bent-knee wide stance) with flowing tails;
   `MmdGlSmoke` green under xvfb. New tests: D-chain append/IK inheritance, LimitAngle reflection,
   ProjectToAxis. MMD tests 22/22; full sln 1,489/0 (2 skips: network-gated YouTube live + MmdRealAsset
   skip on this runner's asset-root resolution).
   Remaining known simplifications: boxes still collide as capsules (inertia is exact now), group
   morphs/bone morphs unparsed, local-append flag unhandled, IK-toggle VMD frames unread.

## Verification appendix

```bash
dotnet build next/MFPlayer.Next.sln            # 0 errors
MFP_PORTAUDIO_HOST_API=JACK pw-jack dotnet test next/MFPlayer.Next.sln --no-build
# review baseline: 1,428 passed / 0 failed across 14 test projects
# post-remediation (NXT-18…26): 1,436 passed / 0 failed
# post-engine-deletion (NXT-13): 1,409 passed / 0 failed (−38 engine tests)
# post-follow-ups (VU meters / NotifyNaturalEnd / progressive waveform): 1,412 passed / 0 failed
# post-implementation-pass (items 1–11): 1,416 passed / 0 failed
# post-authoritative-timeline contract (item 14): 1,423 passed / 0 failed
# post-closure pass (items 15–16, D10 sidecars): 1,426 passed / 0 failed
# post-Gate-5 framework slice (item 17, YouTube module): 1,446 passed / 0 failed
# post-Gate-5 UI slice (item 18, YouTube in HaPlay): 1,449 passed / 0 failed
# post-Gate-6 prototype (item 19, MMD module + camera preview): 1,460 passed / 0 failed
# post-remaining-board closure (items 20–25 below, 2026-07-03): 1,479 passed / 0 failed (only skip = the
#   network-gated YouTube live test; the MMD real-asset test now RUNS — its AssetRoot was a stale
#   /home/seko + MMDTest-vs-MMD_Test path and is repo-root-resolved now)
# post-physics-tuning (item 31, 2026-07-03): 1,487 passed / 0 failed / 2 skipped (network-gated)
# post-reference-aligned rewrite (item 32, 2026-07-03): 1,489 passed / 0 failed / 2 skipped

MFP_PORTAUDIO_HOST_API=JACK pw-jack dotnet run \
  --project next/MediaFramework/Tools/SessionSmoke -- /run/media/sekoree/512/mambo.mp4
# post-remediation: exit 0 (fire + seek + composite/subtitles + trim + loop + fade + fan-out)

git log --oneline 79b70ed..HEAD               # 16 remediation commits
git diff --stat 79b70ed..HEAD                 # 85 files, +8,411 −552
```

Key evidence read end-to-end for this review: `ShowSession.cs` (2,074 lines), `SessionDispatcher.cs`, `CueGraph.cs`, `ShowDocumentValidator.cs`, `ShowDocument.cs`, `ClipStandbyEngine.cs` (arm/refresh paths), `ClipCompositionRuntime.cs` (pump/output lifecycle), `MediaPlayer.cs`, `MediaOpenResult.cs`, `MediaHost.cs`, `MediaRegistry` surface, `NativeApi.cs`, `MediaRuntime.cs`, `ShowSessionGate.cs`, `MediaPlayerViewModel.ShowSession.cs`, `MainViewModel.cs` (ShowSession wiring + reload), `HaPlayShowMapper.cs`, `MediaPlayerShowMapper.cs` (surface), `HaPlay.Desktop.csproj`, `.github/workflows/next-build.yml`; old-tree inventory via project/file enumeration (`S.Media.FFmpeg.Encode`, `S.Media.SkiaSharp`, `S.Media.Effects`, `JackLib` reference scans).

Limitations: static review + build/test execution on this dev box; no hardware A/V-sync or soak measurements this pass (the NXT-04/11 measured gates remain the standing ask); smoke tools not re-run (unchanged since the prior review's execution baseline).
