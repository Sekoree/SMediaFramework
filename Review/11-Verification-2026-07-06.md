# Verification pass and addenda — 2026-07-06

This document is a second-pass review layered on top of the 2026-07-05 report (`README.md` and
`01`–`10`). Its purpose is threefold:

1. **Re-check** whether the prior findings still describe the code as it exists today.
2. **Corroborate** the paper findings against a live HaPlay run and fresh screenshots of every
   workspace, rather than static AXAML review alone.
3. **Add** defects and refinements the first pass did not record.

## Baseline: the code is unchanged since the last review

The only commit after the 2026-07-05 review (`d0c38015 "Review docs"`) adds the review documents
themselves. No first-party source under `MediaFramework/` or `UI/` changed. Every prior finding was
therefore re-validated against the identical tree it was written against — none has been silently
fixed, and none has been invalidated by later edits. The confirmations below quote current line
numbers so the report can be trusted as still-actionable.

## Confirmations (prior findings re-verified against current code)

| ID | Verified how | Result |
|---|---|---|
| ROUTE-01 | `VideoOutputPump.Submit` holds `lock (_gate)` (`VideoOutputPump.cs:221`) and calls `RaisePumpPressure` inside it (`:235`); `RaisePumpPressure` invokes the external subscriber directly (`:308-309`) | **Confirmed** |
| CTRL-01 | `ControlEventQueue` still constructs `Channel.CreateUnbounded` (`ControlEventQueue.cs:19`) | **Confirmed** |
| CTRL-02 | Synchronous `Dispose` blocks on `_worker.GetAwaiter().GetResult()` with no deadline (`ControlEventQueue.cs:231`) | **Confirmed** |
| MMD-01 | `MMDPhysicsBakeCache.Pending` static dictionary never evicts; `LoadOrStart`/`BakeAsync` reuse any task where `!IsFaulted` (`MMDBakedPhysics.cs:267, 297, 341`); `StartBake` has no `finally` removal (`:350-365`) | **Confirmed** |
| MMD-02 | `StartBake` writes `file + ".partial"` then `File.Move`, with no cleanup if `Save` throws (`MMDBakedPhysics.cs:358-361`) | **Confirmed** |
| DOC-01/TEST-02 | `ArchitectureTests` allowed-map still lists removed `S.Media.Encode.FFmpeg` (`:39,58`) and `S.Media.Images.Skia` (`:48,59`); scan roots are only `["Media","Control","Interop"]` (`:65`), so `PALib`/`MALib`/`PMLib`/`NDILib`/`OSCLib`/`LibAssLib` are unchecked | **Confirmed** |
| API-01 | Token persisted in plaintext (`app-settings.json` → `restApiAccessToken`) **and** shown in full as selectable text on the Project workspace (see `Assets/HaPlay-Project.png`); the workspace itself states "copied URLs include `?key=` automatically" | **Confirmed (live)** |
| API-02 | `HandleRequestAsync` answers `OPTIONS` with `Allow: GET, POST, OPTIONS` and never rejects `GET` for mutations (`RestApiServer.cs:161`) | **Confirmed** |
| API-03 | Accept loop fires `_ = HandleRequestAsync(...)` untracked, with no concurrency cap, per-request timeout, or drain on stop (`RestApiServer.cs:138`) | **Confirmed** |
| UI-01 | See "Refinement" below — confirmed and sharpened | **Confirmed (live)** |
| APP-01 | The running app logs `RefreshAllEndpointHealthAsync … outcome=probed=0` every 5 s with zero endpoints configured | **Confirmed (live log)** |
| DOCS-01 | `Directory.Build.props:4` still says "Design docs live in `./Next/`" in the same sentence that says the `Next` tree "was removed" | **Confirmed** |

One security sub-point is worth recording as *already handled well* so it is not "re-found" later:
the remote token is compared with a constant-time `FixedTimeEquals` (`RestApiServer.cs:240`), so
API-01/UX-09 are transport/exposure problems, **not** a timing-oracle problem.

## New findings

### MMD-04 — `stackalloc` inside the joint loop can grow the stack unbounded (low/medium)

`MMDPhysics` builds each joint's 6-DOF spring constraint inside `foreach (var joint in model.Joints)`
and, per iteration, allocates six `stackalloc float[3]` buffers (`MMDPhysics.cs:144-149`) for the
linear/angular limits and spring stiffness. C# `stackalloc` is **not** released at the end of a loop
iteration — it lives until the method returns — so the stack cost is `6 × 3 × 4 B × jointCount`. A
model with hundreds to a few thousand joints consumes tens of KB of extra stack that is never
reclaimed while the (long-running) constraint build proceeds. The compiler flags this directly:

```
MMDPhysics.cs(144,32): warning CA2014: Potential stack overflow. Move the stackalloc out of the loop.
```

(six occurrences, lines 144–149). The two frame buffers at `:120-121` are already correctly hoisted
above the loop; these six were not.

Impact: for typical models this stays well under the 1 MB thread stack, so it is unlikely to crash
today — but it is unbounded by asset input, wasteful, and the analyzer treats it as a real
stack-overflow risk. Recommendation: hoist all six into `stackalloc float[3]` buffers declared next
to `frameA`/`frameB` and overwrite `[0]/[1]/[2]` each iteration. This also removes the six standing
`CA2014` warnings from the build.

### BUILD-03 — The "single build warning" baseline under-counts (low)

The 2026-07-05 baseline recorded exactly one build warning (`SessionSmoke/Program.cs:222`,
unreachable code). A clean rebuild of `S.Media.Source.MMD` on 2026-07-06 additionally surfaced the
six `CA2014` warnings above (MMD-04). The discrepancy is an incremental-build artifact: warnings from
projects that were already up-to-date are not re-emitted, so a whole-solution `--no-restore`
incremental build silently hides them. This is exactly the failure mode `BUILD-01` warns about — new
analyzer warnings blending into (or disappearing from) existing output. Recommendation: capture an
expected-warning-ID/count set from a **clean** build and fail CI on drift, rather than eyeballing an
incremental build's tail.

### REPO-01 — Orphaned project directories remain on disk (low, hygiene)

`UI/HaPlay.App`, `UI/HaPlay.Controls`, and `UI/HaPlay.Core` still exist on disk but contain only
`bin/` and `obj/` build artifacts (e.g. `HaPlay.App.csproj.nuget.g.props`). None of the three is
referenced by `MFPlayer.sln`, and there is no `.csproj` for any of them — they are leftovers from
removed projects. They cost nothing at build time but they mislead a reader scanning `UI/` (the
directory listing implies six UI projects; only three are real: `HaPlay`, `HaPlay.Desktop`,
`HaPlay.Tests`). Recommendation: delete the three directories.

### UI-01 (refinement) — "Dark" is not a no-op; it is a partial, broken theme, and is not honored as a real theme at startup

The prior report said density is a no-op and dark "selects the dark variant." The live behavior is
more specific and worth stating precisely:

- **Density is a guaranteed no-op.** `AppearanceController.ApplyDensity` scans `app.Styles` for a
  `FluentTheme` (`AppearanceController.cs:32-39`), but `App.axaml` *replaced* `FluentTheme` with the
  in-repo Classic theme (its own comment says so), so the loop never matches and the setting does
  nothing — regardless of Normal/Compact.
- **Theme is applied at startup but the Classic chrome has no dark variant.**
  `MainViewModel` calls `AppearanceController.ApplyTheme(_theme)` during construction
  (`MainViewModel.cs:108`), which does set `RequestedThemeVariant = Dark`. Empirically, forcing
  `"theme":"dark"` and relaunching still renders the light Classic chrome
  (`Assets/HaPlay-DarkTheme-Players.png`): the Classic theme only defines light/default resources, so
  the chrome cannot go dark. What *does* flip are the variant-aware controls the `App.axaml` comment
  calls out (AvaloniaEdit script editor, validation adorners) — those resolve dark values against a
  light surface and become unreadable. So "Dark" yields a mostly-light window with a few
  white-on-white islands, not a dark theme.

Net: both appearance settings are broken in different ways, and the Project workspace copy
("Theme and density apply to this machine and persist…") over-promises. This strengthens, rather than
changes, the recommendation in `UI-01`/`UX-04`: hide the options until the Classic theme has real
dark + density resources, or remove them.

### APP-03 — Startup exceeds the app's own "slow" threshold (low, perf)

On a cold `--no-build` Release run the app logs its own warning:
`App.OnFrameworkInitializationCompleted: slow completion in 1354.83ms (threshold=1000.00ms)`. This is
self-reported by HaPlay's diagnostics, so it is a known-good signal to act on rather than a synthetic
measurement. It is minor, but combined with `MainViewModel.ctor` taking ~218 ms it suggests the
composition root is doing more synchronous work on the UI thread than the 1 s budget it sets for
itself. Recommendation: profile the initialization path (module probing, PortAudio acquire/release
churn is visible in the log) and defer non-essential work off the first-frame path.

## What the second pass did *not* find (recorded so it is not mis-"fixed")

- **The audio router does not share ROUTE-01.** The audio side raises pump pressure from
  `AudioRouter.OutputPump.RecordDrop` (`AudioRouter.OutputPump.cs:117-122`), reached from `Commit`,
  which operates on lock-free `ConcurrentQueue`/`BlockingCollection` buffers — **no lock is held**
  when the subscriber callback runs. ROUTE-01 is genuinely video-pump-specific; do not "symmetrically"
  wrap the audio path in a lock while fixing it.
- **The SIMD channel-map mixers are correct.** `ChannelMap.SimdAccumulate.cs` guards every
  vectorized path with `Avx.IsSupported`/`Vector.IsHardwareAccelerated`, provides scalar fallbacks
  (`:106,182,202`), and closes each vector loop with a scalar remainder loop (`:95,170,243`). No tail
  or portability defect was found.
- **No `async void` exists anywhere in the framework** (`MediaFramework/Media|Audio|Control|NDI|Interop|MIDI|OSC|Subtitles`).
- **The MMD native shim is clean.** `mmd_bullet.cpp` frees constraints, bodies, motion states and
  shapes in `mmd_world_destroy`, bounds-checks every body index, and returns `-1` for unknown shape
  types before allocating. Its real gaps (no ABI-version export, no invalid-handle contract test,
  unstated single-thread stepping requirement) are already `MMD-03`.

## Live UX walkthrough (fresh screenshots)

The application was launched per workspace at 1280×860 (Classic theme) and captured. These replace
static AXAML inference with the actual rendered state and confirm the `09-HaPlay-UX` findings.

| Workspace | Screenshot | What it corroborates |
|---|---|---|
| Players | `Assets/HaPlay-Players.png` | UX-01 (duplicated `Stopped` / `Player 1` / `Idle` chips), UX-02 (oversized Play vs. tiny playlist `+`/`Remove tab`; `HOLD` + `Playback` isolated far right), UX-06 (Avalonia logo in the title bar) |
| Cues | `Assets/HaPlay-Cues.png` | UX-02/workspace note: `GO` `Back` `Pause` `Stop` `Panic` in one row with **Panic directly adjacent to Stop**; an unlabeled lock glyph beside `Stop all` (A11Y-01) |
| Soundboard | `Assets/HaPlay-Soundboard.png` | Empty board with no call-to-action; `Edit`/`Stop all` only — reinforces the empty-state recommendation and A11Y-02 (tiles are non-semantic) |
| Control | `Assets/HaPlay-Control.png` | The most developed workspace: `Surfaces`/`Scripts`/`Monitor`/`Tools` tabs, arm state, built-in device profiles. Device config and the live `Monitor` are already on separate tabs (better than the report implies) |
| I/O | `Assets/HaPlay-IO.png` | A *good* empty state ("No outputs yet. Add a local video, audio, or NDI output…"), for contrast with the weaker Players/Soundboard empty states |
| Project | `Assets/HaPlay-Project.png` | API-01/UX-09 (full token shown as plain selectable text; "copied URLs include `?key=` automatically"), API-02 ("GET or POST"), UI-01/UX-04 (`Theme: Follow system`, `Density: Compact` — the two broken controls) |
| Dark theme | `Assets/HaPlay-DarkTheme-Players.png` | UI-01 refinement: `"theme":"dark"` still renders light Classic chrome |

Empty-state consistency is itself a small UX finding: I/O explains what to do, while Players,
Soundboard, and Cues present blank areas. Aligning them behind one empty-state pattern (icon +
one-line explanation + primary action) is a cheap, high-signal improvement that fits under UX-01.

## Bottom line

The 2026-07-05 review is accurate and current; every high-severity finding reproduces on today's
tree, and several were additionally confirmed against a live run. This pass adds one real robustness
defect (**MMD-04**), two hygiene items (**BUILD-03**, **REPO-01**), one perf note (**APP-03**), and a
sharper statement of the appearance-settings breakage (**UI-01 refinement**). The recommended order
in the root `README.md` is unchanged; fold MMD-04 into the MMD work already scheduled first, and the
hygiene/appearance items into the existing quality and UX passes.
