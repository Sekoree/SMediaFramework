# MMD, YouTube, and subtitles review

Scope: `S.Media.Source.MMD`, first-party `Native/mmd_bullet`, `S.Media.Source.YouTube`, `S.Media.Subtitles`, `LibAssLib`.

## Assessment

These modules are cleanly optional and expose source/provider abstractions rather than contaminating core media interfaces. MMD has useful parser/physics tests and YouTube has offline selection/cache tests. MMD's bake coalescing contains a confirmed process-lifetime retention and cancellation defect; YouTube cancellation semantics need correction. Subtitle native coverage remains largely environment-dependent.

## Findings

### MMD-01 — Bake task cache never evicts success or cancellation (high)

`MMDPhysicsBakeCache.Pending` is a static dictionary of `Task<MMDBakedPhysics?>` (`MMDBakedPhysics.cs:265-268`). `LoadOrStart` and `BakeAsync` reuse any task for which `IsFaulted` is false (`:295-303, 339-347`), but completed entries are never removed.

Consequences:

- Every successful task permanently roots the full baked result arrays for the process lifetime, even though the result was written to disk.
- A cancelled task has `IsFaulted == false`, so every future call returns the same cancelled task and can never retry that model/motion pair.
- A caller cancellation token and progress callback belong only to the first task creator; later joiners inherit that lifetime but cannot observe progress.

Recommendation: store only in-flight work. In a `finally`, remove the entry only if it still refers to the completing task. Treat cancelled work as retryable. Separate shared bake lifetime from caller waiting (`sharedTask.WaitAsync(callerToken)`), and decide how multiple progress observers subscribe. Clean stale `.partial` files on failure. Add success-eviction, cancel-then-retry, concurrent-join, and result-collectability tests.

### MMD-02 — Cache integrity is only partially handled (medium)

Cache read catches `IOException` only (`MMDBakedPhysics.cs:281-292, 322-336`). Corrupt content may be reported as a miss by `TryLoad`, but authorization, invalid-data, or other filesystem failures can escape inconsistently. Writes create `file.partial` and move it, but failure cleanup is absent (`:350-365`).

Recommendation: distinguish “bad cache, delete and rebake” from “storage unavailable, report failure.” Use unique temporary filenames, flush/close, atomic replace, and best-effort cleanup in `finally`.

### MMD-03 — Native shim needs a contract test independent of Bullet internals (medium)

The C++ shim is the first-party ownership boundary over a large external physics engine. Parser/physics tests exercise it when the native library exists, but artifact correctness depends on building and staging the shim.

Recommendation: add a small ABI/version export and a test for create/step/read/destroy, invalid handles/arguments, repeated construction, and deterministic cleanup. Validate that the staged library matches the managed binding before loading.

### MMD-04 — `stackalloc` inside the joint loop (low/medium, added 2026-07-06)

`MMDPhysics` allocates six `stackalloc float[3]` buffers **per iteration** of
`foreach (var joint in model.Joints)` (`MMDPhysics.cs:144-149`), for the linear/angular limits and
spring stiffness passed to `WorldAddSpringConstraint`. `stackalloc` is not released at end-of-iteration
— it survives until the method returns — so the stack cost scales as `6 × 3 × 4 B × jointCount` and is
bounded only by the asset. The compiler flags all six as `CA2014: Potential stack overflow. Move the
stackalloc out of the loop.` The two frame buffers at `:120-121` are already correctly hoisted above
the loop; these six were missed.

Recommendation: hoist the six buffers next to `frameA`/`frameB` and overwrite `[0]/[1]/[2]` each
iteration. Typical models stay under the thread stack today, but the allocation is unbounded by input
and the fix also clears six standing build warnings (see `BUILD-03`).

### YT-01 — Caption fallbacks swallow cancellation (medium)

`YouTubeGateway.GetManifestAsync` catches every exception around caption metadata (`YouTubeGateway.cs:83-94`). `TryDownloadCaptionsAssAsync` does the same for manifest, JSON3, and flat-caption paths (`:123-130, 146-160, 166-180`). This includes `OperationCanceledException`, so caller cancellation can turn into “captions unavailable,” continue fallback/network/file work, and potentially complete preparation rather than cancel it.

Recommendation: rethrow when the supplied token is cancelled; use exception filters for genuinely best-effort failures. Delete partial caption files on cancellation/failure. Add cancellation tests at each gateway stage.

### YT-02 — Coalesced preparation is owned by the first caller (medium)

`YouTubePreparer.PrepareAsync` creates the shared lazy task with the first caller's progress reporter and cancellation token (`YouTubePreparer.cs:83-107`). A first caller cancellation cancels all joiners; later tokens do not cancel their own wait; later callers receive no progress.

Recommendation: give the shared operation an internal lifetime and let each caller independently await it with `WaitAsync(cancellationToken)`. If underlying work should cancel when no waiters remain, use reference-counted waiters. Progress should be a shared observable or explicitly documented as unavailable to joiners.

### YT-03 — Cache retention is manual only (low)

Prepared and intermediate streams remain until the user clears the media cache. HaPlay exposes cache size/open/clear, which is useful, but long-running installations need quota/age policy.

Recommendation: add configurable maximum size/age, LRU cleanup after successful preparation, and protection for in-flight assets.

### SUB-01 — Subtitle validation and real-runtime coverage are limited (medium)

LibAss wrapper tests exist, but native-dependent tests/smokes depend on installed libass/fonts and CI currently treats the decode smoke as best-effort. Font discovery, malformed ASS, rapid seek, resolution changes, simultaneous overlays, and repeated dispose remain platform-sensitive.

Recommendation: gate one hermetic Linux fixture with bundled test font/config, run malformed-input and repeated-lifecycle tests, and retain platform-specific smoke lanes for actual font systems. Make subtitle stream-index validation part of `ShowDocumentValidator`.

## What should remain

- MMD's disk bake avoids doing physics work on a live GO path.
- YouTube separates manifest/selection/preparation from local-file playback.
- Rich manual-caption JSON3 conversion with a flat ASR fallback is a reasonable product choice.
- Subtitle rendering sits behind a factory/overlay contract and does not force libass into the core.

