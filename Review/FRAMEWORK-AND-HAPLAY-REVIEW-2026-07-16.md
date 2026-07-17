# MFPlayer framework and HaPlay review

**Review date:** 2026-07-16

**Code baseline:** branch `test-enhancements`, commit `2e645d8b`. The review document was introduced by commit `32f6a516`; its follow-up verification was performed against that commit's worktree on 2026-07-16.

**Scope:** the current framework, native bindings, extras, HaPlay application, tests, build/package/release automation, and the versions of externally supplied native libraries.

## Executive assessment

The repository is substantially better engineered than the average media application. Its module boundaries are mostly meaningful, native ownership is usually explicit, queues are generally bounded, diagnostics and health reporting are built into the runtime, NativeAOT is treated as a real constraint, and the validation surface is unusually broad. The clean build, 1,918 passing tests in a correctly serialized run, package build, vulnerability scan, and successful HaPlay launch smoke all support that assessment.

It is not release-ready without addressing three issues:

1. **Multichannel audio rings can split an interleaved frame when they overflow.** This can rotate/corrupt channels for three-, five-, six-, or seven-channel formats in the core bus and both physical audio backends.
2. **The miniaudio boundary can load an arbitrary system `libminiaudio` into hand-mirrored 0.11.25 structures.** Upstream explicitly does not guarantee ABI compatibility. A mismatch is a process-memory-corruption risk, not a recoverable device error.
3. **The native artifact workflow says libass 0.17.5 but obtains Ubuntu's 0.17.1 package on Linux.** Upstream 0.17.5 fixes two out-of-bounds writes with assigned CVEs. The artifact must be built from or verified as the fixed version before release.

The next tier is release/product-definition work: projectM is a confirmed part of the full feature set but is neither reproducibly buildable from a fresh checkout nor included in the full artifact; the CI serialization setting is ineffective; and the 33 generated NuGet packages lack clear entry points, documentation, and a native-prerequisite contract.

A follow-up audit verified that the findings and core evidence make sense. It also found two concrete omissions: HaPlay's documented `--media-live-uyvy-passthrough` switch is a no-op that nevertheless logs success, and tests intended to compile a now-deleted control guide silently return as passing. The visual review found that all three base themes load, including Simple/Fluent dark mode, but the shared semantic palette and active-navigation treatment are still Classic/light-oriented. Those additions do not displace the three release blockers above, but they should be included in the reliability and UI work.

### Priority legend

- **P0:** release/security blocker.
- **P1:** correctness or memory-safety defect that should be fixed before a public release.
- **P2:** important reliability, performance, maintainability, or packaging work.
- **P3:** worthwhile hardening or simplification.

## Validation performed

| Check | Result |
|---|---|
| Original `dotnet build MFPlayer.sln -c Release --no-restore --nologo` | Passed, no warnings |
| Default solution-wide Release test run | 1,917 passed, 10 skipped, 1 failed |
| Exact failed test in isolation | Passed |
| Full run with `-- RunConfiguration.MaxCpuCount=1` | 1,918 passed, 10 skipped, but test projects still ran concurrently |
| Full run with MSBuild `-m:1` | 1,918 passed, 10 skipped, 0 failed; test projects actually serialized |
| `dotnet pack MFPlayer.sln -c Release` | Passed; 33 packages emitted, all with missing-README warnings |
| Original `dotnet list MFPlayer.sln package --vulnerable --include-transitive` | No known vulnerable NuGet packages reported |
| Original `dotnet list MFPlayer.sln package --outdated` | Small Microsoft package updates plus a major SkiaSharp update; details below |
| Isolated HaPlay JIT/Xvfb smoke | Passed: runtime ready and first frame rendered before clean exit |
| `scripts/build-projectm.sh` from this checkout | Failed immediately because `Reference/projectm-4.1.6` is absent and ignored |
| Follow-up Release build (`--no-restore`) | Passed in 2.12 s, no warnings or errors |
| Follow-up serialized full test run (`-m:1`) | 1,918 passed, 9 skipped, 1 failed; the OSC test failed on a racy free-port handoff and passed immediately in isolation |
| Follow-up Release pack (`--no-build --no-restore -m:1`) | Passed; reconfirmed 33 packages and a missing-README warning for every package |
| Follow-up NuGet vulnerability scan | No known vulnerable direct or transitive packages reported by the configured NuGet source |
| Follow-up NuGet outdated scan | Confirmed Logging 10.0.10, Test SDK 18.8.1, and SkiaSharp 4.150.1 as the only update families described below |
| Follow-up real-UI theme captures | Classic/Light, Simple/Dark, and Fluent/Dark all launched and rendered representative workspaces at a 1280×800 window size under Xvfb |

The default test failure was:

`HaPlay.Tests.CueVideoOutputFanoutTests.ImplicitLayoutTile_IsActiveBeforeFirstSave_AndLiveMoveKeepsOutputRaster`

The test waits only for the fake output's frame count to become non-zero and then immediately asserts a content pixel. The composition is allowed to submit an initial black canvas before the synthetic source is visible, so that condition does not mean the expected source frame has arrived. It passed alone and in the actually serialized full run. This is a **load-sensitive test synchronization defect**, not currently evidence of a production fan-out defect. Replace the count-only wait with a wait for the expected pixel/frame predicate.

The follow-up serialized run failed in `OSCLib.Tests.OSCClientReceiveTests.FixedLocalPort_BindsTheClientSourcePort` with `SocketException: Address already in use`. Its helper binds a loopback UDP socket to port zero, reads the selected port, disposes the socket, and then asks `OSCClient` to bind that number (`OSCClientReceiveTests.cs:45-64`). The release/rebind gap is a time-of-check/time-of-use race, and the probe's loopback bind is not identical to the production wildcard bind. The exact test passed immediately in isolation. This is a test-fixture defect, not evidence that an already available, explicitly configured OSC port cannot bind.

The smoke used isolated settings/cache roots, software GL, Xvfb, and `HAPLAY_SMOKE=1`. It reached `MediaRuntime ready` and `HAPLAY_SMOKE: first frame rendered - shutting down (exit 0)`. The follow-up captures launched the real desktop application with isolated settings and visited Players, Cues, Soundboard, Control, I/O, and Project across the three representative theme combinations. No real PortAudio/miniaudio device, MIDI device, NDI sender/receiver, projectM library, Windows host, or macOS host was available, so those paths received static/API review and their existing automated coverage but not physical-device validation.

`Reference/` is excluded by `.git/info/exclude` and is not tracked repository content. Its large local third-party/test corpus was therefore not treated as shipping source or included in the line-by-line review. The vendored `External/Classic.Avalonia` fork was reviewed at its integration boundary, not re-audited as an independent upstream framework.

## Findings

### P0-1: the Linux artifact can ship a libass version missing the claimed security fixes

**Evidence**

- `Directory.Version.props:17-23` declares the libass native binding version as 0.17.5, and `.github/workflows/build.yml:245-251` repeats that version in the artifact contract.
- The Linux artifact job runs on mutable `ubuntu-latest`, installs `libass9` with apt, and copies the resulting `libass.so.9` (`.github/workflows/build.yml:260,294-307`).
- Ubuntu Noble currently publishes `libass9` **0.17.1-2build1**, not 0.17.5: [Ubuntu package metadata](https://packages.ubuntu.com/noble/libass9).
- Upstream 0.17.5 fixes two out-of-bounds write defects, CVE-2026-61626 and CVE-2026-61627: [libass 0.17.5 release](https://github.com/libass/libass/releases/tag/0.17.5).
- The Windows job uses whatever libass is present in the runner's mutable vcpkg checkout and is `continue-on-error`; it does not set a vcpkg baseline or verify the result (`.github/workflows/build.yml:430-449`).

**Impact**

The Linux “full” artifact is built from a package whose upstream baseline predates both memory-safety fixes, while managed assembly metadata and workflow comments imply 0.17.5. A distro could backport fixes without changing the upstream version, but the workflow neither proves that nor records it. This is also a provenance problem: the release claim cannot be reconstructed from the artifact.

**Recommendation**

Build and pin libass 0.17.5 from an immutable source archive on both release platforms, or consume a pinned binary whose provenance and hash are verified. Add an artifact gate that calls `ass_library_version()` and requires at least 0.17.5; record the returned version and SHA-256 in the native SBOM. Remove `continue-on-error` for a native that the full tier claims to contain. Pin the Windows vcpkg baseline if vcpkg remains the source.

### P1-1: non-power-of-two channel counts can lose interleaved frame alignment

**Evidence**

`AudioBus` converts the requested frame capacity into a float count and rounds that float count up to a power of two (`MediaFramework/Media/S.Media.Core/Audio/AudioBus.cs:50-59`). On overflow, it writes `min(input.Length, freeFloats)` without aligning that amount down to a whole frame (`AudioBus.cs:87-104`). A power of two is not divisible by 3, 5, 6, or 7, so a nearly full six-channel ring can accept only two or four floats from the next six-float frame. A concrete default-bus case is an empty six-channel 32,768-float ring receiving 32,772 floats: the method accepts 32,768, which includes only two floats of the final six-float frame. If the producer submits another complete frame before the ring fully drains, it is concatenated behind that partial frame, so subsequent groups of six no longer represent the original interleaved frames until a complete drain or flush restores the boundary.

The same storage and overflow pattern appears in:

- `S.Media.Audio.MiniAudio/MiniAudioInput.cs`
- `S.Media.Audio.MiniAudio/MiniAudioOutput.cs:56-61,212-230,348-369`
- `S.Media.Audio.PortAudio/PortAudioInput.cs`
- `S.Media.Audio.PortAudio/PortAudioOutput.cs`

The input versions can also advance/drop a partial frame, causing captured channel rotation. Existing tests strongly favor stereo, where a power-of-two float capacity is naturally frame-aligned. In contrast, `S.Media.NDI/Audio/NDIAudioReceiver.cs:525-535` already demonstrates the right model by defining a channel-aligned `UsableFloats` capacity.

**Impact**

Under overrun pressure, 5.1 input/output may map later samples to the wrong speakers until the buffer is reset. This is audible corruption and potentially dangerous in live/show routing where channels have distinct purposes.

**Recommendation**

Represent read/write positions in frames and translate to float offsets, or retain power-of-two backing storage while exposing a usable capacity rounded down to a multiple of the channel count. Round every write, read, oldest-drop, and overflow amount to whole frames. Add parameterized 3-, 5-, 6-, and 7-channel tests that force overflow and wrap-around using a unique marker per channel. Treat those tests as required for all audio-ring implementations.

### P1-2: miniaudio's ABI contract is unsafe

**Evidence**

- `MALib/MiniAudioNative.cs:16-22` explicitly states that the managed layouts are hand-mirrored from miniaudio 0.11.25 and that an offset/size mistake corrupts memory.
- It returns `ma_device_config` by value into a hand-declared structure and uses fixed guessed/derived allocations (`MiniAudioNative.cs:29-40,64-76,94-98`).
- `MALib/MiniAudioLibrary.cs:19-47` sends generic, unversioned names such as `libminiaudio.so` through `SystemFirstNativeLibraryResolver`.
- That shared resolver deliberately chooses system libraries before app-local ones (`MediaFramework/Shared/SystemFirstNativeLibraryResolver.cs:6-10,28-50`). There is no miniaudio version, `sizeof`, or `offsetof` acceptance gate.
- Upstream says that ABI compatibility is not guaranteed between versions and recommends source integration: [miniaudio integration guidance](https://github.com/mackron/miniaudio#building).

**Impact**

If a host has a different unversioned miniaudio build, it wins over the exact app-local 0.11.25 build. Calls may appear to succeed before corrupting memory on an audio callback thread. Managed exception handling cannot make that safe.

**Recommendation**

Prefer a very small, versioned C ABI shim compiled together with the pinned miniaudio header. Keep `ma_device` and configuration opaque to managed code and expose allocation/configuration functions plus explicit ABI/version/size probes. If the direct binding is retained temporarily, load the exact app-local build first and reject any library that cannot prove version 0.11.25 and all required structure sizes/offsets. Do not apply the global system-first policy to this library.

### P1-3: a required full-feature projectM runtime is absent and its build is not reproducible

**Evidence**

- The integration and build script pin projectM 4.1.6 and depend on `Reference/projectm-4.1.6` (`scripts/build-projectm.sh:1-18`). `Reference/` is locally ignored and the source is absent from this checkout, so the script exits at lines 42-45. A fresh clone cannot follow the documented build.
- The artifact workflow never invokes this script and never stages `libprojectM-4` or its preset/texture packs.
- Nevertheless, `.github/workflows/build.yml:510-514` says the full publish contains “every native the app can load.” projectM is not in its listed full tier, native manifest, or required load probe.
- projectM 4.1.7 was released on 2026-07-14 and fixes several crashes and rendering issues, including the null texture-sampler crash addressed by one local patch: [projectM 4.1.7 release](https://github.com/projectM-visualizer/projectm/releases/tag/v4.1.7).
- That release also deliberately resets output to framebuffer 0. HaPlay's offscreen embedding requires the opposite behavior, so the local `projectm-render-to-bound-fbo.patch` must be reviewed/rebased rather than dropping in 4.1.7 blindly.

**Impact**

The visualizer can work on a configured developer machine but silently be unavailable in a nominally full release. The source needed to reproduce the modified native build is not part of the repository workflow. The latest crash fixes are missing.

**Recommendation**

projectM is a required part of the full HaPlay feature set. Make the build acquire an immutable 4.1.7 archive with a checked SHA-256, fetch the pinned preset/texture revisions, apply only still-needed patches, build it in CI, stage the library plus preset/texture assets, and add version/load/render gates. Specifically retest the bound-FBO patch against upstream's new FBO-0 reset. The full artifact should fail rather than silently publish when this feature cannot be staged.

### P2-1: CI serialization and several test completion signals are unreliable

**Evidence**

`.github/workflows/build.yml:96-124` claims `-- RunConfiguration.MaxCpuCount=1` serializes test-assembly execution. In a local full run, multiple project test hosts still executed concurrently and their durations overlapped. This VSTest setting affects a test platform invocation; it does not prevent `dotnet test MFPlayer.sln`/MSBuild from launching separate test projects concurrently. Using `dotnet test ... -m:1` did serialize project execution and all 1,918 tests passed.

The current job then retries the whole test suite twice (`build.yml:126-135`). A retry is useful for infrastructure failure, but it can also convert a genuine timing defect into a green build without preserving a concise flaky-test signal.

The follow-up `-m:1` run found a second, independent synchronization defect. `OSCClientReceiveTests.GetFreeUdpPort()` releases its probe socket before the client binds the chosen number (`MediaFramework/Test/OSCLib.Tests/OSCClientReceiveTests.cs:45-64`). Another socket can claim the port in that interval, and the loopback-only probe does not exactly model `OSCClient`'s wildcard bind. It failed with `Address already in use` in the serialized suite and passed immediately in isolation.

Test-result fidelity is also inconsistent for optional environments. Nine HaPlay GL tests log “GL unavailable, skipping” and `return`, which xUnit reports as passes rather than skips. Environment-gated coverage is legitimate, but the result should say what was actually exercised; a green count currently cannot distinguish an executed GL assertion from an unavailable renderer. P2-9 covers the more serious version of this pattern, where a required guide was deleted and four tests now silently pass without compiling anything.

**Recommendation**

Use MSBuild `-m:1` (and retain VSTest's setting only if needed for a multi-target test project). Fix the fan-out test's completion condition. Replace free-port probe/handoff helpers with a retrying same-bind-semantics helper, or let the component bind port zero and expose the assigned endpoint where the behavior under test permits it. Use real dynamic skips for missing optional renderers/assets. On a failed first attempt, always publish TRX/blame output and emit a prominent “passed only on retry” annotation. Prefer retrying failed test projects or classified infrastructure failures rather than unconditionally rerunning the entire suite.

### P2-2: HLS serving allocates a complete segment for every request, including HEAD

**Evidence**

`S.Media.Stream.Http/HttpMediaServer.cs:306-334` calls `File.ReadAllBytesAsync` for every playlist/segment request and only discards the bytes afterward for `HEAD`. The server permits 64 simultaneous clients (`HttpMediaServer.cs:29,159-169`).

**Impact**

Each `.ts` segment becomes a new large managed array, usually on the large-object heap. Concurrent viewers can retain approximately 64 times the segment size plus socket buffers. HEAD requests incur the same disk read and allocation as GET. This is avoidable GC pressure on the same process performing playback/composition.

**Recommendation**

Use `FileInfo`/`FileStream` to obtain length and stream GET responses through a bounded reusable buffer. HEAD should send headers without reading the body. If closed segments are immutable and repeated frequently, consider a small byte-budgeted cache after measuring; do not use an unbounded file cache.

### P2-3: encode submission allocates and copies every audio chunk

**Evidence**

`S.Media.Encode.FFmpeg/FFmpegEncodeSession.cs:354-388` calls `packedSamples.ToArray()` before acquiring the queue lock and stores that array in a per-leg queue. A conventional 48 kHz stereo pipeline submitting 480-frame chunks produces 100 arrays per second and copies about 384 KiB/s per leg before encoder-side conversion. The exact production rate depends on caller chunk size; this is a code-path estimate, not a benchmark.

**Impact**

Long recordings/streams continuously create Gen0 traffic and memory bandwidth pressure. Multiple audio legs multiply it. The queue is bounded by sample count, so memory use is bounded, but allocation rate is not.

**Recommendation**

Rent exact-or-larger buffers from `ArrayPool<float>` or use a bounded slab/channel owned by the encode session. Return the storage after encoding, dropping, failed initialization, or disposal. Benchmark the whole combined-sink-to-encoder path first, because eliminating an earlier mix/copy may be more valuable than pooling only this final copy.

### P2-4: native artifact production is mutable and version assertions are mostly descriptive

**Evidence**

The release job combines mutable `ubuntu-latest`/`windows-latest`, apt packages, a runner-owned vcpkg checkout, a moving NDI redistributable URL, and BtbN's moving `latest` asset. PortMidi, miniaudio, and Bullet source tags are substantially better pinned. The generated SBOM hashes resulting files, which is useful, but version labels often identify the provider rather than querying the actual binary.

The generic native resolver prioritizes system candidates before bundled candidates. That is sensible for stable soname/major-version contracts, but unsafe for miniaudio and potentially surprising for modified projectM. Most wrappers either expose a version query without enforcing it or rely on export/load success. The manifest checks filename prefixes and presence, not semantic versions.

**Recommendation**

- Pin runner OS versions where possible and all source/binary inputs by tag plus hash or immutable commit.
- Query each loaded library for its actual runtime version/ABI and log both version and resolved path once.
- Give each binding an explicit acceptance policy: exact build for layout-sensitive/custom libraries; compatible ABI major and minimum security patch for stable upstream ABIs.
- Generate the SBOM from those probes, not just intended workflow variables.
- Make the full artifact fail if any promised native is absent; reserve best-effort behavior for reduced/optional tiers.

### P2-5: 33 NuGet packages are generated without a usable public package contract

**Evidence**

`Directory.Build.props:26-38` gives every framework package the same description. `Directory.Build.targets:14-23` makes essentially every non-executable project under `MediaFramework/` packable. A Release pack emitted 33 packages and warned that all lack a README. Inspection found no packaged XML documentation. Low-level P/Invoke packages contain managed binding assemblies but not the native libraries they require.

**Impact**

The modular split is helpful internally, but publishing every leaf assembly makes each one a versioned support promise. Consumers cannot discover which packages are entry points, which are internal glue, what native version is required, how ownership/disposal works, or which packages are safe for NativeAOT. IntelliSense also lacks the XML documentation already present in source comments.

**Recommendation**

Adopt the purpose-led entry/meta package surface specified later in this report while keeping required leaf packages restorable. Classify every package as an entry point, independently supported feature module, or low-level/transitive binding. Add per-package descriptions, README files, XML docs, native prerequisites, supported RID/OS tables, ownership/threading notes, examples, and a compatibility matrix. Do not mark a leaf non-packable merely to hide it: first remove it from published dependency/API graphs or deliberately merge its code into another assembly.

### P2-6: several orchestration classes remain too large to reason about safely

**Evidence**

The low-level module graph is purposeful, but several stateful coordinators have accumulated many responsibilities:

| File | Current lines | Main concern |
|---|---:|---|
| `S.Media.Session/ShowSession.cs` | 2,799 | document topology, transport, audio groups, composition, visualizers, health, and disposal in one runtime owner |
| `HaPlay/ViewModels/CuePlayerViewModel.cs` | 3,023 | cue editing, transport state, persistence-facing model projection, commands, and UI state |
| `HaPlay/ViewModels/MediaPlayerViewModel.cs` plus its show-session partial | 3,988 | playlist/deck state plus runtime construction/reconciliation |
| `HaPlay/ViewModels/OutputManagementViewModel.cs` | 1,862 | output models, native runtime lifecycle, configuration dialogs, health, and route changes |
| `S.Media.Decode.FFmpeg/MediaContainerSharedDemux.cs` | 2,234 | demux ownership, scheduling, seek, buffering, stream coordination, and error policy |
| `S.Media.Routing/Audio/AudioRouter.cs` | 1,764 | topology plus mixing/runtime/metrics concerns |

Large files are not automatically bad, and these have useful regions/partials and tests. The risk here is shared mutable lifecycle state: a change to restore/reconfigure/dispose ordering can affect distant responsibilities, as the amount of seam-specific regression coverage already suggests.

**Recommendation**

Split by runtime responsibility rather than merely adding more partial files. Good candidates are: a show topology/document reconciler; audio-group/transport service; composition/visualizer lifecycle manager; endpoint health/diagnostics adapter; output-runtime factory/reconfiguration service; and narrower view-model state adapters. Keep one explicit high-level owner for sequencing and disposal so decomposition does not obscure ownership.

### P2-7: required unauthenticated LAN control needs an explicit trust-boundary contract

**Evidence**

The remote API is off and loopback-only by default, applies request bounds, uses constant-time token comparison, and requires POST for commands. Those are good controls. However, LAN binding is an explicit checkbox while the token remains optional; with an empty token every request is authorized (`UI/HaPlay/Remote/RestApiServer.cs:12-16,47-53,404-421`). `UI/HaPlay/Models/AppSettings.cs:68-76` says requests require a token, so the settings model's contract also contradicts the actual server behavior. Its endpoints include cue GO/panic, player transport, volume, soundboards, and other mutating show controls. HTTP is plaintext, and query-string token forms are accepted.

**Impact**

On a shared or misconfigured network, any peer can control the show when LAN mode has no token. Even with a token, plaintext transport and query strings make interception/log leakage possible. This may be acceptable on a deliberately isolated show-control VLAN, but it should be an explicit operating contract.

**Recommendation**

Unauthenticated LAN control is a required workflow for low-friction integrations such as Bitfocus Companion, so do not force token provisioning. Preserve the current off-by-default and loopback-by-default behavior, but make the LAN/no-token state unmistakable in the UI and status endpoint. Document that it assumes an isolated/trusted show-control network and is not suitable for untrusted Wi-Fi/VLANs. Keep the optional token mode for installations that want it, prefer the Authorization header there, and discourage query token forms because URLs are commonly logged. For deployments crossing trust boundaries, document a reverse-proxy/TLS pattern rather than implementing custom crypto in HaPlay.

The media-stream HTTP server also binds `IPAddress.Any` by default (`S.Media.Stream.Http/HttpMediaServer.cs:72`) without authentication. That is defensible for a LAN playback output, but the trust boundary and exposure should be prominent in configuration and documentation.

### P2-8: `--media-live-uyvy-passthrough` reports success but does nothing

**Evidence**

`UI/HaPlay.Desktop/Program.cs:80-86` documents the switch as skipping live UYVY-to-BGRA conversion. The handling block at lines 138-143 finds the switch and logs “live video using native pixel format,” but both assignments that would set `PlaybackVideoPipeline.CliRequestedUyvyPassthrough` and `PreferNativePixelFormatForLiveVideo` are commented out. `MainViewModel.cs:134-135` consequently sees no CLI override and replaces the preference with the persisted setting. The parsing is also incorrectly nested inside `ConfigureLogging`: `--media-log off` returns at `Program.cs:90-103` before reaching the playback switch at all.

**Impact**

An operator can start HaPlay with a documented performance/compatibility override, receive an affirmative log message, and still run the conversion path selected by saved settings. This makes live-video diagnosis misleading and could invalidate a workaround chosen for a specific capture/output path.

**Recommendation**

Parse non-logging startup options before logging configuration, set both fields before constructing `MainViewModel`, and emit the success message only after the state is applied. Add a small command-line configuration test for the switch alone, together with `--media-log off`, and with a conflicting persisted preference. If the override is no longer supported, remove the option and success log instead of keeping a no-op compatibility surface.

### P2-9: deleted documentation makes guide tests silently pass without testing

**Evidence**

The repository no longer contains a `Doc/` directory, but source comments still point to at least nine deleted documents, including the architecture, output mapping, multi-output sync, NDI terminology, UI rewrite, and control guides. `Bcf2000GuideScriptsTests` is duplicated in `UI/HaPlay.Tests` and `MediaFramework/Test/S.Control.Tests`; each copy says it compiles scripts embedded in `Doc/HaPlay-Control-X32-BCF2000-Layers.md` so the guide cannot ship invalid code. When the document is absent, both test methods merely write “skipping” and return (`Bcf2000GuideScriptsTests.cs:17-25,97-105`). The four methods are therefore counted as passes even though no guide or script was read. `Program.cs:57-58` similarly points incident rationale at a deleted `Review/CODE-REVIEW-2026-07-12.md`.

**Impact**

The test suite communicates coverage that no longer exists, maintainers cannot follow important design/incident references from the code, and the user-facing control setup material promised by the test has disappeared. This reinforces the package/documentation problem in P2-5 and P3-3, but the false-green test result warrants a separate reliability finding.

**Recommendation**

Decide which deleted documents remain product contracts. Restore and update the control guide and essential architecture/operations documentation, or move required snippets into a tracked canonical fixture and remove claims that a guide is being tested. Keep only one copy of the guide test and make a missing required input fail; optional external assets should use a real skipped result. Add a lightweight link/reference check covering tracked Markdown and source-code `Doc/`/`Review/` references.

### P3-1: `ManualResetEventSlim` is not disposed for shared-output client leases

`S.Media.Routing/Audio/SharedAudioOutput.cs:178` creates one `ManualResetEventSlim` per `ClientInput`. `Dispose` flushes and sets it but never disposes it (`SharedAudioOutput.cs:245-251`). A timed/cancellable wait can inflate the event to a kernel-backed handle. Frequent creation/removal of shared output clients can therefore retain native handles until finalization/GC.

Dispose it only after all possible waiters have exited; blindly disposing immediately can race a current `Wait`. A small waiter count/drain protocol, `SemaphoreSlim`, or an async/channel capacity signal can make the lifetime explicit. Add a churn test that opens, waits, closes, and recreates leases.

### P3-2: fixed-rate duplicate-frame stepping loses fractional 90 kHz ticks for some rates

The normal timestamp path derives PTS from source `TimeSpan`, which is good. The fixed-rate gap-fill/re-anchor path stores a single integer `_videoFrameDuration90k` computed by integer division (`S.Media.Encode.FFmpeg/FFmpegEncodeSession.cs:346-348,565-588`). Common 30000/1001 produces exactly 3003 ticks; 60000/1001 needs 1501.5 ticks and therefore loses half a tick per generated/re-anchor step. Uncommon rates with no exact 90 kHz divisor have the same issue.

Use rational rescaling of a frame ordinal, or accumulate the division remainder, instead of repeatedly adding a truncated duration. Add long-duration 60000/1001 and another non-divisor-rate test. This is a small drift issue, not evidence that ordinary source-timestamp encoding is broken.

### P3-3: package and product documentation lags the implementation

The source contains strong comments, but external documentation does not yet explain the architecture, supported workflows, native provisioning, platform support, lifecycle/ownership rules, or the difference between full/core/minimal bundles. Naming also varies between HaPlay and older HaPlayer references. This raises the cost of using the framework without reading its internals and makes the release promises above ambiguous.

Create an architecture overview, a minimal playback example, a show/session example, a native dependency matrix, an AOT guide, and a release-tier contract. Link those from the root README and package READMEs. State the supported platform policy explicitly: Linux is primary, Windows is supported, and macOS is currently unsupported. Existing macOS resolver branches can remain best-effort portability code, but they should not imply a tested support promise.

### P3-4: the MMD preview can retain bitmaps and publish stale render state

`AddMMDDialogViewModel` creates a new `WriteableBitmap` for each preview and directly assigns it to `PreviewImage` (`UI/HaPlay/ViewModels/Dialogs/AddMMDDialogViewModel.cs:247-255`). The replaced bitmap is never disposed, the final bitmap is not disposed when the dialog closes, and replaced `CancellationTokenSource` instances are cancelled but not disposed (`AddMMDDialogViewModel.cs:190-207,271`; `AddMMDDialog.axaml.cs:15`). Repeated camera/slider edits can therefore defer native pixel-buffer cleanup to finalization/GC.

Cancellation does not fully order the results. An older render can pass the cancellation check at line 242, be cancelled while its UI callback is queued, and still replace a newer preview. Each overlapping render also independently sets `IsRendering = false` in `finally`, so the busy indicator can clear while the newest render is active.

Give the dialog view model an explicit disposal/close lifetime. Cancel and dispose the active CTS, atomically replace and dispose superseded bitmaps after the `Image` releases them, and tag each render with a monotonically increasing generation checked inside the UI-thread publish callback. Track in-flight renders so only the current generation clears `IsRendering`. Add a rapid-edit/close test using a fake renderer that deliberately completes requests out of order.

### P3-5: shared theme, accessibility, and localization contracts are incomplete

The theme bundle architecture itself is good: Classic is intentionally light-only, Simple and Fluent honor light/dark, Fluent alone owns density, and resource-resolution tests cover several previously fragile third-party controls. The application launched successfully in all three representative combinations. The remaining issues are in app-level semantics:

- `Styles/Tokens.axaml:24-52` defines one fixed set of status/state/text brushes outside a `ThemeDictionaries` block. Its comment still says dark values can be added “if/when a real dark theme lands,” although Simple/Dark and Fluent/Dark are already supported. Dark status text chosen for Classic surfaces is reused unchanged on dark surfaces.
- A static scan found 67 direct color literals across 22 view XAML files, including duplicated local `card`/`hint` styles in `OutputManagementView` and `PipelineStatsView`. Some colors are intentionally canvas/media colors, but status, validation, selection, and chrome colors should come from semantic, variant-aware roles.
- `MainView.axaml:27-33` gives the selected sidebar item only semibold text in the shared style; the visible raised/dithered selection is a Classic-template-only selector. In Simple and Fluent captures, the active workspace is consequently much less obvious.
- The icon accessibility test realizes only `MediaPlayerView` and detects `PathIcon`/`Image` descendants (`IconButtonAccessibilityTests.cs:12-55`). It does not cover Cues, Control, I/O, or dialogs and treats symbolic text buttons such as `+`, `–`, `↑`, and `↓` as ordinary text. Several such controls expose only a tooltip, so an automation client may announce the symbol rather than the action.
- Large portions of the visualizer drawer, Control dock panes, script editor, and mapping/visualizer dialogs still contain hard-coded English strings while the primary shell uses `Strings.resx`. This prevents complete localization and means longer translated layouts are not exercised.

Move semantic color roles into light/dark theme dictionaries and migrate view-local operational colors incrementally. Add a base-theme-independent selected-navigation indicator (for example, a left accent bar plus subtle semantic background) while retaining the Classic bevel as an enhancement. Expand accessibility tests to realize every primary workspace and representative dialogs, require meaningful names or labels for symbol/icon controls, and check keyboard focus/order. Move remaining operator-facing strings into resources and add at least one pseudo-localized layout smoke. Automated contrast checks and a small set of image baselines per base theme should complement, not replace, the existing resource-resolution tests.

## HaPlay visual and structural review

Representative real-application captures were taken at 1280×800 for Classic/Light, Simple/Dark, and Fluent/Dark. Players, Cues, Soundboard, Control, I/O, and Project were visited across the matrix. There were no theme-load crashes, missing-control-template failures, or gross overlaps in those captures. Classic has the clearest deliberately retro identity; Simple and Fluent are viable alternatives rather than superficial recolors. A wholesale redesign is not justified, but the information architecture and shared semantic styling would benefit from a focused update.

| Area | Observation | Recommended update |
|---|---|---|
| Navigation | The fixed sidebar is understandable and has useful shortcuts/collapse behavior. Active selection is strong only in Classic. | Add the theme-independent selected marker described in P3-5; keep icons, labels, and shortcuts unchanged. |
| Players | The transport deck is stable and appropriately prioritizes controls, but an empty playlist is a large blank list whose creation actions live only in the toolbar/flyout. | Add a centered empty-state sentence plus “Add media” primary action and a drag/drop hint, reusing the successful Soundboard empty-state pattern. |
| Cues | The master/detail model and large GO/HOLD controls fit show operation. The authoring toolbar and visualizer drawer carry many secondary actions and long explanations in the main surface. | Keep GO/HOLD/Stop fixed; group authoring actions into a primary Add menu plus contextual overflow, and make advanced visualizer help collapsible or link it to Help. Preserve keyboard shortcuts. |
| I/O | The master/detail split is good, but five “Add …” buttons plus health actions form a long non-wrapping header that will be fragile under narrower windows and localization. The empty state has text but no direct action. | Keep the most common Add action visible and move other output types to an Add split/menu; add the same action to the empty state. Let health/status remain separate. |
| Project/settings | One workspace mixes document actions, autosave/recovery, recent projects, appearance, Remote API configuration, a long endpoint cheat sheet, and caches. The two-column layout leaves unused recent-project space while the right column becomes a long scroll. | Keep Project focused on current/recent files and recovery. Move appearance, remote control, and cache management to a Preferences workspace/dialog with sections. Move the endpoint catalogue to searchable Help or an expandable/copyable API reference. Collapse to one column at narrow widths. |
| Soundboard | The tile hierarchy, edit mode, state colors, and empty-state call to action are among the clearest parts of the app. | Reuse its empty-state and direct-manipulation patterns elsewhere; migrate its operational colors to theme dictionaries without changing the layout. |
| Control | Docking suits expert users and rendered under every base theme. The panes and script editor contain dense, mostly hard-coded English UI and several symbol-only actions. | Preserve docking/custom layouts, but resource the strings, add meaningful automation names, and offer a reset-layout command that is easy to discover. |
| Dialogs | Most are structurally consistent and scroll where needed; advanced mapping/MMD/visualizer dialogs mix semantic UI colors with fixed dark canvases. | Keep intentionally dark preview canvases isolated, use shared semantic resources for validation/chrome, and test representative dialogs in both dark-capable themes. |

The update should be staged through shared tokens and reusable empty-state/header components first. That produces consistent improvements across all themes without maintaining three separate visual designs.

## Native/P\Invoke currency review

Versions were checked against authoritative upstream release pages on 2026-07-16. “Current” here means version currency only; it does not prove that the release artifact actually contains that build.

| Dependency | Repository/binding intent | Upstream current | Assessment/action |
|---|---:|---:|---|
| miniaudio | 0.11.25 | [0.11.25](https://github.com/mackron/miniaudio/releases/tag/0.11.25) | Current, but unsafe selection/layout contract; fix P1-2 before upgrading |
| PortAudio | 19.7.0 | [19.7.0](https://github.com/PortAudio/portaudio/releases/tag/v19.7.0) | Current; add runtime path/version logging and real multichannel/device validation |
| PortMidi | 2.0.7 | [2.0.7](https://github.com/PortMidi/portmidi/releases/tag/v2.0.7) | Current; source build is pinned, but hardware/virtual-port smoke remains platform-dependent |
| libass | intent 0.17.5 | [0.17.5](https://github.com/libass/libass/releases/tag/0.17.5) | Binding intent current; Linux artifact source is 0.17.1 and Windows result is unverified—P0-1 |
| projectM | 4.1.6 + local patches | [4.1.7](https://github.com/projectM-visualizer/projectm/releases/tag/v4.1.7) | Update candidate; rebase/test bound-FBO behavior and make distribution decision—P1-3 |
| Bullet | 3.25 through custom ABI-1 shim | [3.25](https://github.com/bulletphysics/bullet3/releases/tag/3.25) | Current; custom shim ABI check is a good pattern to copy elsewhere |
| SDL3 / SDL3-CS | SDL 3.4.12 / binding 3.4.12.6 | [SDL 3.4.12](https://github.com/libsdl-org/SDL/releases/tag/release-3.4.12) | Current |
| FFmpeg / FFmpeg.AutoGen | FFmpeg 8.1 ABI (`avcodec` 62), AutoGen 8.1.0 | [FFmpeg 8.1.2](https://ffmpeg.org/download.html#release_8.1) | ABI line current; verify the moving BtbN asset's exact 8.1.x patch and record it |
| NDI | local SDK/redist intent 6.3.2.0 | vendor redist v6; local SDK reports 6.3.2.0 | Matches the supplied local SDK, but the moving `ndi.link` URL prevents reproducible version proof; query runtime version in artifact gate |

Managed package currency from `dotnet list ... --outdated`:

- `Microsoft.Extensions.Logging`, `.Abstractions`, and `.Console`: 10.0.9 → 10.0.10. This is a low-risk patch update.
- `Microsoft.NET.Test.Sdk`: 18.7.0 → 18.8.1. Update after one normal and one serialized full test pass.
- `SkiaSharp`: 3.119.4 → 4.150.1. This is a major update across rendering/native assets and should be a planned compatibility project with Linux, Windows, JIT, AOT, text, GPU, and screenshot/output validation—not an automatic dependency bump.
- The central package scan reported no newer version for the remaining pinned packages, including FFmpeg.AutoGen and SDL3-CS.

## Area-by-area assessment

### Core, time, frames, and ownership

The basic contracts are small and purposeful. Rational frame rates, explicit frame ownership/disposal, monotonic clocks, bounded buffers, health snapshots, and diagnostics make the higher layers testable. NativeAOT analyzers and warnings-as-errors are applied centrally. The main new defect here is the frame-alignment mistake in `AudioBus`; it should be fixed at the shared abstraction first and turned into a reusable conformance test for backend rings.

### Audio routing and device backends

The router supports real product needs—multiple routes, shared devices, channel mapping, mix groups, latency/capacity signaling, and backend failover. PortAudio and miniaudio are both intentionally retained: PortAudio is the mature primary host API, while miniaudio proved the backend extension seam and remains a useful alternative implementation. That makes a shared backend conformance suite and reusable frame-aligned ring primitive more valuable than maintaining duplicated callback/ring behavior independently. Arbitrary channel counts, including a 32-in/32-out test interface, are supported; the multichannel ring issue and miniaudio ABI boundary are therefore release blockers. Shared-output event lifetime is lower priority.

### FFmpeg decode, players, sync, and sources

Shared demux, decode queues, seek/epoch handling, audio-clock sync, and non-file sources cover a difficult set of cases with good targeted tests. The complexity in `MediaContainerSharedDemux` deserves decomposition only after profiling/ownership seams are written down; splitting it mechanically would make scheduling harder to follow. No new deterministic decoder/player correctness failure appeared in this review.

### Composition, GPU presentation, subtitles, and visualizers

The layered compositor, CPU/GPU paths, output layout, subtitle renderer, SDL presentation, MMD source, and projectM adapter all have understandable purposes. The projectM delivery story and libass artifact version are the material problems. Continue treating actual first-frame rendering as a release gate; load-only probes do not exercise GL context compatibility, FBO integration, fonts, or native callbacks.

### Encode, mux, recording, and streaming

The encode model has explicit options, per-leg bounded queues, timestamp handling, ownership, and output targets. The largest easy allocation win is audio chunk pooling. HLS should stream files instead of materializing them. Fractional fixed-rate stepping is a precision cleanup. Profile before changing FFmpeg frame conversion or mux buffers; those native copies may dominate and are not proven bottlenecks by static review.

### Session/show runtime

The session layer has a legitimate purpose: it owns a document-like show topology and reconciles it with live runtime resources. It should not be removed or folded into HaPlay. Its size shows that it now needs internal services with explicit ownership and transaction/reconciliation boundaries. Preserve the single serialized orchestration point and test detach/reacquire/dispose ordering when splitting it.

### Control, scripting, MIDI/OSC, and C ABI

The control queue, bindings, command dispatch, Mond scripting, MIDI/OSC surfaces, inbound native plugin host, and exported C ABI are distinct integration features rather than obvious dead code. Bounded queues, coalescing, cancellation, and the MMD shim ABI marker are strong patterns. Present them through the recommended `S.Media.Control`, `S.Abi`, and `S.Media.Interop` entry packages while retaining their lower-level contract packages for implementers and transitive dependencies. Fix the racy UDP test helper and restore or deliberately replace the deleted control-guide contract; the runtime tests are broad, but the current green count overstates documentation coverage.

### HaPlay UI, persistence, recovery, and remote control

HaPlay is more than a demo shell: it exercises output lifecycle, cue/show mapping, players, recovery, visualizer settings, live reconfiguration, remote control, and diagnostics. The app's service extraction and restore/reconcile tests are valuable, but several view models still mix UI state with resource lifetime; the MMD preview is a concrete example where cancellation, bitmap ownership, and visible busy state should move behind a narrow render-preview service/lifetime. The REST API is technically hardened against simple resource exhaustion. Its required no-token LAN mode should retain the current opt-in binding while clearly communicating its trusted-network assumption.

The three-theme system is structurally sound and all representative combinations rendered. The main UI work is consolidation rather than reinvention: make semantic colors variant-aware, give Simple/Fluent a strong selected-workspace state, reuse consistent empty states, separate Project from machine-wide preferences, and expand accessibility/localization coverage beyond the media deck and Soundboard. The detailed visual matrix above should be used as a staged backlog.

### Build, tests, packaging, and release

This is one of the repository's strongest areas: clean analyzers, architecture tests, focused native/ABI/subtitle/GL smokes, AOT publishing, manifests, SBOM generation, and launch gates are all appropriate. Correct the ineffective serialization option and misleading skip/pass patterns, make native sources immutable and versions executable assertions, and turn package documentation into part of the pack gate. Add startup-option tests so a diagnostic switch cannot log success while remaining inactive. A release gate should validate what an artifact **contains** and what a test actually exercised, not what the workflow or test name intended.

## Simplification and optimization order

Do not begin with broad rewrites. The highest-return sequence is:

1. Fix and conformance-test whole-frame audio ring behavior across all backends.
2. Replace or strictly gate the miniaudio ABI boundary.
3. Pin/verify libass 0.17.5 and reproducibly stage projectM 4.1.7 plus its assets in the full artifact.
4. Make the UYVY startup override truthful and independent of logging configuration; add startup-option tests.
5. Correct CI serialization, the fan-out predicate, the OSC port helper, and false-pass skip/documentation tests.
6. Stream HLS files and pool encode audio chunks; measure allocation rate and GC pauses before/after.
7. Add the recommended entry/meta packages, restore essential documentation, and define platform/feature tiers without breaking the existing leaf package graph.
8. Consolidate HaPlay's light/dark semantic tokens and reusable empty-state/header patterns, then simplify Project/settings and the I/O/Cue action surfaces without changing show-critical transport placement.
9. Extract runtime services from the largest coordinators one responsibility at a time, keeping ownership and sequencing tests around every extraction.

Useful benchmarks/counters to add before further optimization:

- allocated bytes and Gen0/Gen2/LOH collections during one hour of HLS streaming;
- encode audio allocations per second per leg;
- audio ring overrun/underrun counts by channel layout;
- compositor frame time and dropped/late frames with one, four, and many layers;
- seek-to-first-frame and project restore-to-first-output latency;
- native handle/thread counts across 1,000 output attach/detach cycles.

## Resolved product decisions

The review questions were answered on 2026-07-16 and now form part of this assessment:

1. **projectM is part of the full supported feature set.** Its reproducible build, assets, and render smoke are release requirements rather than optional follow-up work.
2. **The NuGet surface should have a small set of purpose-led entry packages.** The recommended layout is below.
3. **Unauthenticated LAN control is required** for low-friction Bitfocus Companion and similar integrations. It remains opt-in by enabling LAN binding; security work should clarify the trusted-network boundary without imposing mandatory keys.
4. **Arbitrary audio channel counts are supported**, including a known 32-input/32-output interface. Whole-frame behavior must therefore be correct and tested for every channel count from 1 through 32, not just common surround layouts.
5. **macOS is not currently supported.** Linux is the primary target and Windows is supported. macOS code paths are best-effort portability only until that policy changes.
6. **The preferred release is full/self-contained.** Reduced tiers can remain useful secondary artifacts, but the full tier must contain every redistributable supported runtime and asset. Any legal exception, such as a proprietary runtime that cannot be redistributed, must be named explicitly rather than silently omitted.
7. **PortAudio and miniaudio are both retained.** miniaudio also validates that the backend abstraction is genuinely extensible. Both backends should run against the same conformance suite; miniaudio's unsafe native ABI boundary still needs correction.

## Recommended NuGet package surface

Do not immediately merge or delete the existing assemblies. Their modular boundaries are useful, and a package cannot depend on an unpublished `ProjectReference` assembly unless that code is merged or packed another way. Instead, keep leaf packages restorable and introduce a small, documented set of entry/meta packages. Consumers should normally start with one of these:

| Recommended package | Purpose | Current packages it would compose |
|---|---|---|
| `S.Media` | Default playback runtime for applications | Core, Time, Routing, Players, FFmpeg decode/common, PortAudio, and miniaudio |
| `S.Media.Core` | Minimal contracts/frames/formats/diagnostics SDK for backend or source authors | Existing `S.Media.Core`; keep this dependency-light |
| `S.Media.Show` | Cue/show/session and composition runtime | `S.Media`, Session, Compositor, Subtitles, and Source.Text |
| `S.Media.Encoding` | Recording, muxing, and network-stream output | Encode.FFmpeg and Stream.Http |
| `S.Media.Presentation.SDL3` | Standalone/windowed SDL3 and compositor presentation | Present.SDL3 and Present.SDL3.Compositor |
| `S.Media.Presentation.Avalonia` | Embedding video into Avalonia applications | Present.Avalonia |
| `S.Media.Control` | Show control, scripting, MIDI, and OSC integration | `S.Control`, Control.Abstractions, PMLib, and OSCLib |
| `S.Abi` | Load inbound native C-ABI plugins into a managed MFPlayer host | Existing S.Abi plugin-host surface |
| `S.Media.Interop` | Publish the framework as the outbound NativeAOT `s_media_player` C ABI | Existing S.Media.Interop surface |
| `S.Media.Full` | Batteries-included framework feature set used by a full HaPlay-class application | All packages above plus NDI, projectM, MMD, YouTube, both audio backends, subtitles, and other supported optional sources |

`S.Media.Full` describes the full **framework** and should not include the HaPlay executable or HaPlay-specific view models. HaPlay remains the reference/full application.

The existing feature packages should remain independently installable for advanced consumers:

- `S.Media.Audio.PortAudio` and `S.Media.Audio.MiniAudio` for explicit backend selection;
- `S.Media.NDI` for NDI send/receive;
- `S.Media.Subtitles` for libass rendering;
- `S.Media.Visualizer.ProjectM` for visualization;
- `S.Media.Source.MMD`, `S.Media.Source.YouTube`, and `S.Media.Source.Text` for source-specific features;
- `S.Media.Stream.Http` when HTTP/HLS streaming is wanted without the encoding meta-package;
- `S.Media.Compositor`, `S.Media.Gpu`, and `S.Media.Routing` for advanced custom pipelines.

Treat `PALib`, `MALib`, `PMLib`, `NDILib`, `LibAssLib`, `ProjectMLib`, `S.Media.FFmpeg.Common`, and similar low-level assemblies as **transitive/advanced dependency packages**, not recommended starting points. They may still need to be published because higher packages depend on them and some public APIs expose their contracts. Give them explicit descriptions such as “low-level binding; most applications should reference …” rather than pretending they are private or simply setting `IsPackable=false`.

The packaging rollout should be non-breaking:

1. Add the entry/meta packages and per-package READMEs first; keep all existing package IDs.
2. Mark the intended stability level in documentation: public contract, feature module, or low-level binding.
3. Add package tests that restore each entry package into an empty sample and build/publish it.
4. Only make a leaf non-packable after no published package depends on it and no supported public API exposes it, or after its code has deliberately moved into another assembly.
5. For a genuinely self-contained `S.Media.Full`, provide the redistributable PortAudio, miniaudio, PortMidi, libass, projectM, and Bullet assets under correct `runtimes/<rid>/native` paths, or through a clearly named native-assets dependency. Keep NDI separate if its redistribution terms require host installation.

## Proposed release gates

Before calling a binary release-ready:

- all whole-frame ring conformance tests pass for every channel count from 1 through 32, including overflow and wrap-around;
- miniaudio refuses an incompatible build and survives a real-device playback/capture/stop/restart smoke;
- `ass_library_version()` proves at least 0.17.5 in both artifacts;
- the full artifact's manifest lists exact native versions, paths, and hashes and fails on missing promised features;
- projectM 4.1.7, its presets, and its textures are staged and render-smoked through HaPlay's bound-FBO path;
- the full test suite passes with actual project serialization, without relying on a retry;
- required guide/document tests fail when their tracked inputs disappear, while unavailable optional hardware/GL tests are reported as skipped rather than passed;
- startup-option tests prove that `--media-live-uyvy-passthrough` changes the live pipeline both with normal logging and `--media-log off`;
- Linux and Windows AOT artifacts reach the first rendered frame using only their bundled/declared tier dependencies;
- NuGet entry packages contain README/XML docs and document native prerequisites;
- primary workspaces and representative dialogs render in Classic/Light, Simple/Light/Dark, and Fluent/Light/Dark with semantic contrast, visible keyboard focus, and meaningful automation names;
- both supported OS families have build, native-load, and application-launch gates, with Linux receiving the primary/deepest validation.

## Final verdict

The framework has a coherent purpose and a strong foundation; there is no case for a wholesale rewrite. Most modules correspond to real media/show-control responsibilities. The product decisions are now clear: Linux-first plus Windows, a full projectM-capable artifact, arbitrary channel counts, both audio backends, and intentionally optional authentication for LAN control. The critical work is therefore concrete: make all audio queues frame-based, make native ABI/version assumptions executable, make the full artifact match that feature promise, and introduce purpose-led NuGet entry packages. The follow-up adds a smaller but important truthfulness theme: startup flags, skipped tests, guide tests, and artifact labels must report what actually happened. After those items, allocation work, theme-token consolidation, focused UI restructuring, and responsibility-based extraction from the large coordinators should improve long-running reliability and usability without destabilizing the architecture.

## Addendum: independent second-pass verification and additional findings (2026-07-16)

A second, independent pass re-verified this document's claims against the worktree, re-ran the build and the serialized test suite, swept the framework and application for defects the document does not cover, and extended the visual review to all five theme combinations plus a headless dialog gallery. Screenshot evidence referenced below lives in `Review/Screens-2026-07-16/`.

### Verification outcome

Every evidence citation spot-checked in P0-1 through P3-5 matched the code exactly, including the `AudioBus`/`MiniAudioOutput` unaligned overflow truncation, NDI's contrasting channel-aligned `UsableFloats` model, the apt `libass9` vs. claimed 0.17.5 mismatch, the hand-mirrored miniaudio layouts behind an ungated system-first resolver, the commented-out UYVY assignments inside `ConfigureLogging`, the guide tests' silent-return pattern, the undisposed `ManualResetEventSlim` and MMD preview lifetimes, and the Classic-only token/selection styling. The size table is exact (`MediaPlayerViewModel.cs` 2,439 + its ShowSession partial 1,549 = 3,988). Two garbled sentences introduced by the previous edit (executive summary and the P0-1 recommendation) were repaired in place.

Checks that came back clean in this pass, for the record: `HttpMediaServer` request handling is bounded (10 s per-request deadline, 8 KiB request cap, extension/segment allowlist with no URL decoding, so no traversal); `TsFanOutBuffer` uses bounded per-client channels with keyframe-aware eviction; `RestApiServer` never reads request bodies and bounds headers/query before auth; `SessionRecoveryService` is single-flight with an atomic-write path; the OSC codec length-checks strings/blobs; the YouTube module wraps YoutubeExplode with one shared `HttpClient`; and the new `FFmpegRuntime` Windows resolver correctly requires one complete coherent library set per directory. No new memory-safety or protocol-level defect was found in those areas.

Two factual updates to earlier text:

- **`Reference/projectm-4.1.6` is present in this working copy** (git-ignored, alongside the other vendored trees), so `scripts/build-projectm.sh` can run on this machine today. The validation-table row describing it as absent reflects an earlier state. The P1-3 substance is unchanged: `Reference/` is not tracked, so a fresh clone still cannot reproduce the build, and CI still neither builds nor stages projectM.
- **A fresh serialized run (`-m:1`, Release, no retry) passed completely** — 0 failures including `FixedLocalPort_BindsTheClientSourcePort` — consistent with the P2-1 diagnosis that both failures are load-sensitive test races rather than product defects.

### P2-10: the Players transport row clips at the default window size

**Evidence**

`MainWindow.axaml:10` opens at 960×640 with `MinWidth` 720. The transport is a centered horizontal `StackPanel` inside the star column of a two-column grid (`MediaPlayerView.axaml:356-421`), and compact mode only engages below 500 px of view width (`MediaPlayerView.axaml.cs:16`). The row's natural width (Prev · Play · HOLD · ▾ · VIZ · ▾ · Next plus the right-hand Mute/volume/dB/Playback/Stop group) is roughly 1,000 px, so between ~500 and ~1,000 px the centered panel overflows both edges and is clipped by the right group. At the default first-launch size, the VIZ toggle is cut to a sliver and Next is partially eclipsed (`Screens-2026-07-16/classic-light-players-960default.png`); at 1,280 px everything fits (`classic-light-players.png`).

**Impact**

On first launch, before any window resize, transport controls that the view's own comments call panic-path (Next; also the visualizer toggle) are partially invisible and can overlap the Mute/volume group's hit-targets. At `MinWidth` 720 the overlap is worse. This contradicts the deck's otherwise deliberate touch-tier design.

**Recommendation**

Trigger compact mode from measured content width rather than a fixed 500 px constant (or raise the threshold above the row's real natural width), and verify the hide-compact set actually brings the row under the narrow budget. Alternatively let the two transport groups share one wrapping/adaptive container. Add a headless layout test that lays out `MediaPlayerView` at 960 and at 720 and asserts the transport buttons' bounds neither clip nor overlap.

### P3-6: the I/O capability status line wraps mid-item

The Outputs header's module line (`FFmpeg ✓ PortAudio ✓ MiniAudio ✓ NDI ✓ YouTube ✓ MMD ✓`) wraps as plain text, so a module name and its checkmark split across lines (Classic and Simple at 1,280: `NDI` ends line one, its `✓` starts line two — `classic-light-io.png`, `simple-dark-io.png`); under Fluent's larger type the same text crams into three lines squeezed between the description and the six-button Add row (`fluent-dark-io.png`). Render each module+state as an unbreakable unit (chips or a `WrapPanel` of name-check pairs), which also gives the health/tooltip affordance a natural home; this pairs with the Add-row consolidation already recommended in the visual matrix.

### P3-7: smaller cross-theme consistency findings from the capture matrix

The five-combination matrix (Classic/Light, Simple/Light, Simple/Dark, Fluent/Light, Fluent/Dark across all six workspaces) and a 10-dialog × 3-theme headless gallery rendered without a single crash, missing template, or gross overlap beyond P2-10 — confirming the theme architecture is sound. The residual issues are consistency-level and mostly reinforce P3-5 with concrete instances:

- **Fluent keeps its default magenta/pink accent** (selected-tab underline, volume slider track, Edit-cues toggle — `fluent-dark-players.png`, `fluent-dark-cues.png`), which clashes with the app's operational green/amber/red language. Set an explicit accent color in the Fluent bundle so the brand and state colors are deliberate on both variants.
- **Disabled emphasis is inconsistent across themes for the same state.** With no media loaded, Simple/Light renders Play/Stop as washed-out pastels (`simple-light-players.png`) while Classic renders the identical disabled state fully saturated (`classic-light-players.png`). One direction should be chosen: semantic Go/Danger styles need explicit, similar disabled treatments in all three bundles.
- **`CuePlayerView.axaml:954` hard-codes the cue splitter as `#22000000`**, which is nearly invisible on dark surfaces — a concrete instance of the P3-5 color-literal migration, worth using as the template fix (shared `BorderSubtle`-class token).
- **The cue empty state's icon is the plain filled-circle `AppIcons.Cue` glyph at 34 px**, which reads as an unrendered placeholder blob rather than an icon (`classic-light-cues.png`, `fluent-dark-cues.png`). A cue-list glyph (e.g. stacked rows with a GO arrow) would carry the empty state better.
- **The mapping editor's toolbar is symbol-only** (`+`, duplicate, `–`, `↑`, `↓` — `fluent-dark-mapping-editor.png`), confirming the P3-5 accessibility item extends into dialogs, not just workspaces.

The dialog gallery itself is worth keeping: a ~100-line console harness (headless Skia + the public theme bundles + parameterless dialog constructors) rendered every dialog in every theme in seconds. Promoting it into the test tree — asserting a frame renders and, later, comparing against per-theme baselines — would give the "representative dialogs render in every theme" release gate an executable form.

## Implementation log (2026-07-16, same-day fix pass)

The recommendations above were implemented in one pass on `test-enhancements`; the full serialized
suite passes after each group. Status per finding:

| Finding | Status | What was done |
|---|---|---|
| P0-1 libass | **Implemented (CI)** | Linux artifact builds libass **0.17.5 from the pinned, SHA-256-verified source archive** (apt binary dropped); Windows vcpkg staging is now gating (baseline commit logged). New `scripts/check-native-versions.sh` gate calls `ass_library_version()` on the exact shipped binary and fails below 0.17.5. SBOM entry updated. Needs one CI run to validate. |
| P1-1 rings | **Implemented** | New shared `FrameAlignedFloatRing` (S.Media.Core): power-of-two backing with usable capacity rounded down to whole frames; every write/read/drop-oldest is frame-aligned and the read cursor is CAS-guarded against flushes. `AudioBus`, `MiniAudioInput/Output`, `PortAudioInput/Output` all rewired onto it. Conformance tests parameterized over channel counts 1–32 incl. overflow, wrap-around and a sustained 5.1 overrun-pressure test. |
| P1-2 miniaudio | **Implemented** | MALib no longer uses the system-first policy: app-local exact build probes FIRST, and EVERY candidate must prove `ma_version()` == 0.11.25 or it is unloaded and skipped (logged via new `MALibDiagnostics`). The release artifact version gate also asserts 0.11.25. |
| P1-3 projectM | **Implemented (CI, 4.1.6)** | `scripts/build-projectm.sh` now downloads the pinned, hash-verified upstream release archive when `Reference/` is absent, so any checkout (incl. CI) reproduces the build. The Linux artifact job builds it and stages the full install tree (lib + pinned preset/texture packs) under `External/projectm/linux-x64/` inside the publish — exactly where the resolver and the UI's default preset path probe. Version-gated to 4.1.6. **Deliberately NOT bumped to 4.1.7**: upstream 4.1.7 resets output to FBO 0, which conflicts with the bound-FBO patch; the pin comment documents the retest requirement. |
| P2-1 CI/tests | **Implemented** | `dotnet test` now uses `-m:1` (real project serialization) + `--logger trx`; a retry-only green emits a prominent `::warning` annotation and uploads the diagnostics artifact. Fan-out test waits for the expected pixel content, not a frame count. OSC test binds through a retrying same-bind helper instead of the probe/handoff race. The nine GL tests use `Xunit.SkippableFact` and now report **Skipped** when GL is absent. |
| P2-2 HLS | **Implemented** | Playlist/segment GETs stream from a `FileStream` through one rented 64 KiB buffer (bounded to the advertised Content-Length); HEAD sends headers without reading the body. |
| P2-3 encode alloc | **Implemented** | Audio chunks are rented from `ArrayPool<float>` with tracked lengths and returned on every exit path (encoded, dropped, disposed). |
| P2-5 packages | **Implemented** | Six entry/meta packages added (`S.Media`, `S.Media.Show`, `S.Media.Encoding`, `S.Media.Presentation.SDL3`, `S.Media.Control`, `S.Media.Full` — dependency-only, README'd). All 33 leaf packages got purpose-led descriptions + READMEs (entry/feature/low-level classification, native prerequisites). Pack gate: a packable project without a README fails `dotnet pack`; XML docs now ship in every package. 39 packages pack warning-free. Package restore-into-empty-sample tests remain follow-up. |
| P2-6 decomposition | **Started** | First extraction landed: `ShowSessionVisualizerService` owns the visualizer slot lifecycle (attach/replace, per-placement updates, fade snapshots, reload retention, persistent reattach) with explicit tap/metadata delegates; ShowSession keeps serialization + fade policy. All 136 session tests green. Further extractions (demux, view models) remain follow-up. |
| P2-7 LAN trust | **Implemented** | No-token LAN mode kept (product decision) but made unmistakable: warning-styled banner in the Project card when enabled+LAN+no-token, `/api/v1/status` now reports `"lan"` and `"auth":"open"/"token"`, and the `AppSettings` doc comment no longer contradicts the optional-token contract. |
| P2-8 UYVY flag | **Implemented** | Parsing moved out of `ConfigureLogging` into `PlaybackVideoPipeline.ApplyCliStartupOptions` (runs before `MainViewModel`, independent of `--media-log off`); both flags are actually set and the success log fires only after they are. Startup-option tests cover the switch alone, with `--media-log off`, and against a conflicting persisted preference. |
| P2-9 guide tests | **Implemented** | `Doc/` control guides restored from history (BCF2000/X-Touch/setup/scripting/profiles) — the embedded scripts still compile on the current runtime. Duplicate test copy deleted; the surviving copy FAILS when the guide is missing. New `DocReferenceTests` link-checks every `Doc/*.md` reference in source; remaining stale pointers were fixed or restored (`MultiOutput-Sync`, `NDI-Terminology`, `Output-Mapping-Plan`, `Format-Support`, `PixelFormats-OpenGL`). |
| P2-10 transport | **Implemented** | Compact threshold raised to 900 px and a new "tight" tier (<560 px) drops the VIZ group/config dropdowns/options so Prev/Play/HOLD/Next + Mute/volume/Stop always fit down to the 720 px MinWidth. `MediaPlayerTransportLayoutTests` asserts no overlap/clipping at 1100/760/510 px. |
| P3-1 MRES | **Implemented** | Waiter-count drain protocol: the event is disposed only after the last waiter exits; wakes are dispose-race-safe; the post-Reset dispose re-check closes the erased-wake window. Lease-churn test added (50 rounds with genuinely blocked waiters). |
| P3-2 PTS drift | **Implemented** | Generated/re-anchored frames step through a rational remainder accumulator (`AccumulateStep90k`) — exact for 60000/1001-class rates; unit tests cover 600k-frame drift and the 1501/1502 alternation. |
| P3-3 docs | **Implemented** | Stale pre-rewrite docs replaced: new `MediaFramework-Architecture.md`, rewritten `MediaFramework-Quickstart.md` (current registry/`MediaPlayer` API), new `Native-Dependencies.md` (matrix + acceptance policies), `Release-Tiers.md` (tier contract + gates), `NativeAOT.md`, `Packages.md`; all linked from the root README with the explicit platform policy. |
| P3-4 MMD dialog | **Implemented** | Render generation counter (checked inside the UI-thread publish) prevents stale renders from replacing newer previews or clearing the busy flag; replaced bitmaps and CTSes are disposed; the dialog's `Closed` handler disposes the VM (final bitmap + CTS). Fake-renderer out-of-order test remains follow-up. |
| P3-5 themes | **Implemented** | All semantic color tokens moved into `ThemeDictionaries` with Default + Dark values (consumers swept to `DynamicResource`); base-theme-independent selected-workspace marker (left accent bar + selection wash) with the Classic bevel kept as enhancement. Broader localization/accessibility expansion remains follow-up. |
| P3-6 I/O status | **Implemented** | The capability line renders as wrap-safe chips (one unbreakable name+check unit per module; skip reason in the tooltip). |
| P3-7 consistency | **Implemented** | Fluent gets an explicit blue accent palette (light+dark); disabled Go/Danger have one deliberate treatment across all themes (hue kept, uniformly dimmed); cue splitter uses the `BorderSubtle` token; new `CueList` empty-state glyph; mapping-editor toolbar buttons carry automation names. |
| User report: visualizer layer 100 | **Fixed** | New visualizer placements default to layer 0 / the next index, exactly like media placements (`AddVisualizerCue`, `AddVideoPlacement`, legacy fallback). |
| User report: multi-placement | **Fixed** | ShowSession visualizer slots now hold ONE SURFACE LAYER PER PLACEMENT (shared source + audio tap): the executor groups a cue's placements by composition and attaches them all; live-move addresses a specific placement index (per-composition, computed by the VM); fades/preservation/reattach cover every layer. Session tests cover multi-placement attach, per-index live-move and persistent reattach. |

Also fixed on the way: `InternalsVisibleTo("HaPlayer")` (the desktop exe's real assembly name) so `HaPlay.Desktop` can consume internals.

### Addendum verdict

The prior document's findings, priorities, and product decisions all held up under independent re-verification; nothing in it needs to be walked back beyond the two factual updates above. The additions are one new P2 (a first-launch transport clipping defect with screenshot evidence and a clear fix) and consistency-level UI work that slots into the existing P3-5/visual-matrix backlog. The release blockers remain exactly the three named at the top.
