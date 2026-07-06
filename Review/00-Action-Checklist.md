# Action checklist — consolidated

Every finding from `01`–`11` as one checkable list. Each item is phrased as the action to take, tagged
with severity, component, and the source doc. Verification-only confirmations are not repeated here —
this is the work list.

**Legend:** 🔴 High · 🟠 Medium · 🟡 Low/polish. Component in *(italics)*; `NN` = source doc number.
Counts: 7 high, 34 medium, 22 low, plus acceptance/CI gates and guardrails.

Recommended order (from `README.md`): high concurrency/resource fixes → outbound ABI + remote-API
transport → CI native validation → document/settings/YouTube hardening → appearance/keyboard/a11y →
docs + coverage.

---

## 🔴 High — address before treating artifacts as production-ready

- [ ] **ROUTE-01** — Collect the video pump's pressure payload under `_gate`, invoke subscribers after releasing it; in the router snapshot the pump ref under the router lock and read counters outside it. Add a stress test whose subscriber queries metrics/removes an output. *(Routing; 02)*
- [ ] **CTRL-01** — Replace the unbounded control-event `Channel` with a bounded one; coalesce continuous controls (fader/encoder/meter), preserve lifecycle/button edges, expose dropped/coalesced counters; never block native MIDI/UDP receive callbacks. Add flood tests with a slow runtime. *(Control; 06)*
- [ ] **MMD-01** — Store only in-flight bakes; evict in a `finally` only if the entry still refers to the completing task; treat cancelled work as retryable; separate shared bake lifetime from per-caller waiting (`sharedTask.WaitAsync(callerToken)`); clean stale `.partial` on failure. Add success-eviction, cancel-then-retry, concurrent-join, result-collectability tests. *(MMD; 05)*
- [ ] **ABI-01** — Reconcile `s_media_player.h` with `NativeApi`: implement real error mapping (`MFP_ERR_NOT_FOUND`) and real state reporting (idle/playing/paused/ended/error), and either an audio-capable `mfp_session_create` or a corrected header. Add a C conformance test for every documented return/state. *(Interop; 07)*
- [ ] **ABI-02** — Add per-session reference-counted call leases + a closing state (or require+enforce caller serialization); make `mfp_shutdown` reject/defer until active calls drain. Add concurrent go/query/destroy/shutdown stress from C. *(Interop; 07)*
- [ ] **API-01 / UX-09** — Stop shipping the token in URLs/clipboard: default docs to header-auth `POST`, mask the token with explicit reveal/copy/regenerate, require HTTPS (embedded cert/reverse proxy) or short-lived scoped tokens for LAN, and store the secret in OS credential storage when available (warn on file fallback). *(HaPlay remote + UX; 08/09)*
- [ ] **REL-01** — Define required/optional native manifests per RID; fail artifact creation when required libs are absent; load-probe them from the publish dir and run the published app + backend enumeration against that exact directory. *(CI/release; 10)*

---

## 🟠 Medium

### Core / routing / audio
- [ ] **CORE-01** — Make registry disposal an interlocked transition; reject capability ops after disposal; document lease semantics on `IMediaRegistry`. Add a Dispose-vs-`TryOpen*` stress test. *(Core; 01)*
- [ ] **ROUTE-02** — Add an output registration lease/generation: snapshot targets under lock, submit outside it, retire removed outputs after leases drain; at minimum require+enforce non-blocking pump outputs. *(Routing; 02)*
- [ ] **AUDIO-01** — Define a backend conformance suite; run resolver/format/lifecycle/error tests against both PortAudio and MiniAudio without hardware; keep device-dependent cases tagged. *(Audio; 02)*

### Video / FFmpeg / NDI / presentation
- [ ] **FFMPEG-01** — Build a generated-fixture matrix (A-only/V-only/A+V, VFR, missing PTS, multi-track, truncated, repeated seek, cancel during open/read, EOF, CPU vs HW) asserting buffer ownership + native allocation stability across loops. *(FFmpeg; 03)*
- [ ] **FFMPEG-02** — Extract packet/codec ownership, timestamp normalization, and seek/drain coordination into internal components with invariant tests; keep the public provider API unchanged. *(FFmpeg; 03)*
- [ ] **NDI-01** — Factor native capture/send behind a small adapter; unit-test timestamp correlation, buffer limits, format change, reconnect, cancellation, exactly-once release; retain an opt-in real loopback soak. *(NDI; 03)*
- [ ] **PRESENT-01** — Make one Linux software-GL presentation path gating and establish a proven Windows launch lane; keep HW acceleration optional but visible. *(Presentation; 03)*

### Session / show document
- [ ] **DOC-01** — Validate every serialized scalar/path at load and return all errors: empty `MediaPath`; negative offsets/fades; non-finite placement/crop/rotation/opacity; non-positive dest sizes; out-of-range opacity/crop; negative layers; stream indices `≥ -1`; positive per-route rate; non-empty audio-group IDs. Add boundary/NaN/Infinity tests. *(Session; 04)*
- [ ] **DOC-02** — Before v1 freeze, either implement+document `Outputs`/`Devices` or remove/deprecate with a migration; disambiguate `Outputs` vs `Routes` (same type today). *(Session; 04)*
- [ ] **SESSION-01** — Continue extracting `ShowSession`/`ClipCompositionRuntime` around stable invariants (doc staging/commit, transport-group runtime, clip factory, audio-output topology, monitoring/end), keeping all mutations on the dispatcher. *(Session; 04)*
- [ ] **SESSION-02** — Move the `text:` source into a first-party framework module, or explicitly define text URIs as a HaPlay-only schema extension; don't present the same `ShowDocument` as portable if hosts interpret it differently. *(Session; 04)*

### Sources / subtitles
- [ ] **MMD-02** — Distinguish "bad cache → delete + rebake" from "storage unavailable → report failure"; use unique temp filenames, flush/close, atomic replace, best-effort cleanup in `finally`. *(MMD; 05)*
- [ ] **MMD-03** — Add an ABI/version export + a create/step/read/destroy + invalid-handle/args contract test; validate the staged shim matches the managed binding before load. *(MMD native; 05)*
- [ ] **MMD-04** — Hoist the six per-joint `stackalloc float[3]` buffers (`MMDPhysics.cs:144-149`) out of the loop, next to `frameA`/`frameB`; clears the six `CA2014` warnings. *(MMD; 05 — low/med)*
- [ ] **YT-01** — Rethrow on caller-token cancellation (use exception filters for genuine best-effort); delete partial caption files on cancel/failure; add per-stage cancellation tests. *(YouTube; 05)*
- [ ] **YT-02** — Give the coalesced prepare an internal lifetime; each caller awaits via `WaitAsync(ct)`; make progress a shared observable or documented-unavailable. *(YouTube; 05)*
- [ ] **SUB-01** — Gate one hermetic Linux subtitle fixture (bundled font/config); malformed-input + repeated-lifecycle tests; add subtitle stream-index validation to `ShowDocumentValidator`. *(Subtitles; 05)*

### Control / MIDI / OSC
- [ ] **CTRL-02** — Make `DisposeAsync` the primary path with a documented shutdown bound; log/abandon non-cooperative plugin work; bounded wait in the sync adapter. *(Control; 06)*
- [ ] **CTRL-03** — Replace the monitor buffer's `List.RemoveAt(0)` with a fixed-size circular buffer; benchmark at max capacity + expected meter/update rate. *(Control; 06)*
- [ ] **OSC-01** — After CTRL-01, document which OSC messages may coalesce/drop and whether bundles stay atomic; test malformed packets, max datagrams, bundle ordering/timetags, sender churn, flood shutdown. *(OSC; 06)*

### Interop / plugin ABI
- [ ] **ABI-03** — Build a table-driven pure-C ABI suite on Linux + Windows treating the header as spec (errors, states, invalid UTF-8/JSON, repeated init/shutdown, double destroy, concurrent access, last-error locality); run against the published shared lib. *(Interop; 07)*
- [ ] **PLUG-01** — Add a normative ownership/threading section to `mfp_plugin.h` (callback concurrency, re-entrancy, buffer ownership, pointer lifetime, destroy-vs-work races); add negative test plugins; version structs with size/version fields. *(Interop; 07)*
- [ ] **PLUG-02** — Define host serialization or per-adapter operation leases; test dispose during blocked submit/read/render with a deliberately slow plugin. *(Interop; 07)*

### HaPlay application
- [ ] **API-02** — Status/read endpoints GET, mutations POST only; return `405` + `Allow`; consider idempotency/request IDs. *(HaPlay; 08)*
- [ ] **API-03** — Add a bounded concurrency gate, per-request timeout/shutdown token, header/query size limits, and in-flight tracking; `Stop` prevents new dispatch and awaits handlers with a deadline. *(HaPlay; 08)*
- [ ] **UI-01** — Remove/disable theme + density until the Classic theme has real dark + density resources (density is a hard no-op; dark yields white-on-white in variant-aware controls). Add a headless test asserting a measurable resource/control property. *(HaPlay; 08)*
- [ ] **SET-01** — Save settings via the existing atomic temp-and-move (`ProjectIO`), keep one backup, log/recover corrupt files, coalesce frequent window-placement writes. *(HaPlay; 08)*
- [ ] **LOG-01** — Use `Channel.CreateBounded<string>` (SingleReader, DropOldest), track dropped lines, batch writes/flush on a short interval, force-flush only on warn/error/shutdown, bound message length, lower the default capacity. *(HaPlay; 08)*
- [ ] **APP-02** — Extract owned services with explicit lifetimes (remote-API host, endpoint-health monitor, project session, workspace registry); keep timers/native lifecycle/persistence out of the view model. *(HaPlay; 08)*

### HaPlay UX / accessibility
- [ ] **A11Y-01** — Set `AutomationProperties.Name`/`HelpText` on every icon-only control; expose play/pause/hold/selected-workspace/health/active-cue state; verify tab order, focus, screen reader, 200% scale; add accessibility-tree smoke asserts. *(UX; 09)*
- [ ] **A11Y-02** — Reimplement soundboard tiles as `Button`/`ListBoxItem` with a command, keyboard activation, selected/playing state, and a keyboard-accessible overflow menu; preserve drag/drop/edit via behaviors. *(UX; 09)*
- [ ] **UX-01** — One compact deck header (name, transport state, current item, elapsed/remaining, route health); empty deck → primary "Add media" + DnD hint; drop the permanent "players are tabs" line after onboarding. *(UX; 09)*
- [ ] **UX-02** — Single transport strip with consistent 36-44 px hit targets and stable order; put playback mode near next/prev; put Hold adjacent to Play (strong latched state) or in a labeled safety zone; keep remove/destructive actions secondary. *(UX; 09)*
- [ ] **UX-03** — Add a searchable shortcut/help overlay; show accelerators in menus/tooltips; make live shortcuts configurable; reconsider bare-Escape Panic (modifier or preference). *(UX; 09)*
- [ ] **UX-04** — Hide appearance options until they change the actual theme; when implemented, validate contrast across all workspaces/dialogs/meters/preview surfaces. *(UX; 09)*
- [ ] **UX-08** — Add screenshot/layout checks at 720×480, 1024×768, 1440×900, and 200%; at narrow widths collapse setup inspectors before live controls; let secondary toolbars wrap/overflow. *(UX; 09)*
- [ ] **UX-10** — Unify empty states behind one pattern (icon + one-line explanation + primary action): I/O has a good one, but Players, Soundboard, and Cues present blank areas. *(UX; 09/11 — low/med)*

### Engineering / CI / docs
- [ ] **REL-02** — Make one hermetic Linux subtitle/GL/HaPlay-launch path gating; keep a separate non-gating HW matrix; convert deferred `continue-on-error` comments into tracked issues with owners/exit criteria. *(CI; 10)*
- [ ] **TEST-01** — Prioritize contract/fake-adapter tests (NDI, MiniAudio, FFmpeg, outbound ABI), then gate one real-runtime path per platform; track coverage by behavioral contract. *(Tests; 10)*
- [ ] **TEST-02** — Enumerate every non-test/tool first-party project; assert exactly one architecture rule each; remove stale `S.Media.Encode.FFmpeg`/`S.Media.Images.Skia` entries and `Next/` references; include the wrapper trees (`PALib`/`MALib`/`PMLib`/`NDILib`/`OSCLib`/`LibAssLib`). *(Tests; 10)*
- [ ] **TEST-03** — Add a targeted regression test with each fix (MMD eviction/retry, control-queue overload, video pressure re-entrancy, ABI conformance/concurrent destroy, appearance no-op); require stress/bounded-memory asserts for concurrency bugs. *(Tests; 10)*
- [ ] **DOCS-01** — Replace the README with: supported OS/RID + native-dependency matrix; quick-start host composition + playback; ownership/threading/disposal/real-time-callback rules; module map + plugin guidance; HaPlay build/run/config/security; test/smoke commands. Remove the `Next/` comment in `Directory.Build.props`. *(Docs; 10)*
- [ ] **BUILD-02** — Pin immutable native-input URLs/commits, verify SHA-256, record licenses/versions in the artifact, mirror/cache approved inputs, and generate an SBOM per artifact. *(CI supply chain; 10)*

---

## 🟡 Low / polish

- [ ] **CORE-02** — Roll back accumulated lifetimes in reverse order on builder/build failure; test with a successful fake module followed by a throwing one. *(Core; 01)*
- [ ] **TIME-01** — Benchmark/soak at the intended max simultaneous clip count (thread count, context switches, wakeups, missed deadlines); consolidate scheduling only on evidence. *(Time; 01)*
- [ ] **PLAYER-01** — Extract an internal decode-loop state object + explicit transition helpers; test transition legality rather than growing branch tests. *(Players; 01)*
- [ ] **AUDIO-02** — Expose backend availability/diagnostic records ("not installed" / "incompatible" / "open failed") from module registration; surface them in HaPlay; avoid fatal optional-backend probing. *(Audio; 02)*
- [ ] **GPU-01** — Publish one ownership/threading table for CPU frames, GL textures, DMABUF/D3D handles, compositor surfaces, and presenters. *(GPU; 03)*
- [ ] **NDI-02** — Add wakeup rate, buffered duration, dropped frames, reconnect count, and shutdown latency to diagnostics + soak asserts. *(NDI; 03)*
- [ ] **SESSION-03** — Replace the migration-phase comments in `ShowDocument.cs` with current behavioral/threading/ownership guarantees. *(Session; 04)*
- [ ] **YT-03** — Add configurable max cache size/age, LRU cleanup after successful prepare, and protection for in-flight assets. *(YouTube; 05)*
- [ ] **CTRL-04** — Document projects/scripts as trusted code; show script sources/capabilities before first activation for downloaded projects; add a "disable control scripts on open" preference. *(Control; 06)*
- [ ] **MIDI-01** — Add opt-in Windows/Linux device-loop tests using virtual MIDI; surface PortMidi version/capability in diagnostics. *(MIDI; 06)*
- [ ] **AOT-01** — Pin and document the accepted Mond `IL2026`; add a runtime test of the affected error/stack path in the published AOT smoke; revisit on Mond upgrades. *(AOT; 06)*
- [ ] **APP-01** — Disable the endpoint-health timer when zero endpoints; restart it on collection changes; suppress success logs for empty/unchanged sweeps at normal trace. *(HaPlay; 08)*
- [ ] **APP-03** — Profile initialization (module probing, PortAudio acquire/release churn); defer non-essential work off the first-frame path (startup self-reports >1 s). *(HaPlay; 08)*
- [ ] **UX-05** — Define semantic color tokens (primary action, selected, live, held, warning, error/panic, healthy, disabled, secondary text); use spacing/section headers before more borders; measure contrast; reserve saturated colors for operational state. *(UX; 09)*
- [ ] **UX-06** — Add a HaPlay icon family at platform sizes + a consistent wordmark/name; fix stale `HaPlayer` naming. *(UX; 09)*
- [ ] **UX-07** — Route all user-facing strings through one resource system (errors, tooltips, empty states, dialog titles, enum names, shortcut descriptions); add a lint rule for raw `Text`/`Content`/`Header`/`Title` literals. *(UX; 09)*
- [ ] **DOCS-02** — Decide the distribution model (source/project-ref, NuGet, or single host SDK); add only the appropriate packaging metadata + API-compat checks; avoid dozens of accidental packages. *(Docs; 10)*
- [ ] **BUILD-01** — Treat first-party compiler/analyzer warnings as errors (narrow documented suppressions); capture expected AOT warning IDs/counts so new ones fail CI. *(Build; 10)*
- [ ] **BUILD-03** — Baseline expected warning IDs/counts from a **clean** build (not incremental) and fail CI on drift (incremental builds hid the MMD `CA2014`s). *(Build; 10)*
- [ ] **REPO-01** — Delete the orphaned `UI/HaPlay.{App,Controls,Core}` directories (only `bin/obj`, not in the solution). *(Repo; 10)*

---

## ✅ Acceptance gates — verify once the fixes land

### UX acceptance (09)
- [ ] Every live operation is possible with keyboard only and has a visible focus state.
- [ ] A screen reader identifies every icon-only button, soundboard tile, state, and selected workspace.
- [ ] GO, Panic/Stop All, Hold, and remove/delete cannot be confused by proximity or identical styling.
- [ ] No critical state is communicated only by color.
- [ ] Main workflows remain operable at 720×480 and 200% scale.
- [ ] Appearance settings cause observable, tested changes — or are not shown.

### CI / release quality gates (10)
- [ ] Required-native manifest + load probe for each uploaded RID.
- [ ] Zero unexpected build/AOT warnings (from a clean build).
- [ ] Full unit + architecture suite on Linux and Windows.
- [ ] Gating Linux software-GL HaPlay launch, subtitle render, and C ABI conformance.
- [ ] Windows published-app launch + PortAudio/libass load probes.
- [ ] Stress suite for routing/control/session cancellation and disposal races.
- [ ] Best-effort CI steps reviewed quarterly (must not become undocumented permanent state).

---

## 🛡️ Guardrails — do not regress while fixing the above

- Registry composed at the root (not process-global); immutable capability registry; deterministic highest-confidence decoder selection; explicit `IDisposable` frame/source ownership.
- Bounded video queues + explicit dropped-frame counters; conversion staged **outside** the router lock; native bindings isolated in `PALib`/`MALib`/`NDILib`/etc.
- **The audio router raises pump pressure lock-free** (`AudioRouter.OutputPump`) — do **not** add a lock "for symmetry" with the ROUTE-01 fix.
- SIMD channel-map mixers keep their `IsHardwareAccelerated`/`Avx.IsSupported` guards, scalar fallbacks, and scalar remainder loops.
- No `async void` anywhere in the framework — keep it that way.
- Session: validate + stage the replacement document before touching the live show; dedicated dispatcher + active-fire cancellation; bounded/cycle-validated cue traversal; source-generated JSON.
- MMD: disk bake keeps physics off the GO path; the native shim frees all constraints/bodies/motion/shapes and bounds-checks every handle.
- ABI: opaque monotonic tokens (not raw `GCHandle`), thread-local last-error, plugin unload leasing + delayed `NativeLibrary.Free`, real compiled C smoke clients.
- HaPlay: atomic project save; optional module probing (starts without NDI/every backend); broad headless VM tests; single `ShowSession` runtime for cues + soundboard; remote API disabled + loopback-only + constant-time token compare **by default**.
