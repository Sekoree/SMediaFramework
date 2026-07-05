# Build, tests, documentation, and release review

Scope: solution/project graph, repository configuration, tests, smoke tools, CI, artifacts, and first-party documentation. External/reference implementations were excluded.

## Verified baseline

- Release tests: 1,538 total; 1,534 passed; 4 skipped; 0 failed.
- Incremental Release build: succeeded with one warning, unreachable code at `MediaFramework/Tools/SessionSmoke/Program.cs:222`.
- Linux x64 NativeAOT smoke: published and ran successfully.
- AOT warning: Mond `IL2026` on a stack-frame/error path.
- HaPlay: launched successfully and shut down normally after UI inspection. Linux ALSA emitted expected device-probe warnings in the current environment.

## Findings

### REL-01 — Artifacts can be published without required native libraries (high)

The Windows PortAudio, libass, and NDI staging steps are `continue-on-error` (`.github/workflows/build.yml:282-345`). The bundle step copies whatever happens to exist and then only lists native files (`:353-371`). There is no platform manifest asserting that the required libraries and dependency closure are present before upload.

Impact: CI can produce a green artifact job and upload an app that publishes successfully but cannot use core audio/subtitle/NDI capabilities—or may fail at startup depending on resolver behavior.

Recommendation: define required and optional native manifests per RID. Fail artifact creation when required files are absent, load-probe them from the publish directory, and run the published app plus backend enumeration against that exact directory. ASIO may remain explicitly optional; base PortAudio must not be hidden behind the same best-effort step.

### REL-02 — Several end-to-end checks are permanently best-effort (medium)

Subtitle decode, ABI GL, and HaPlay launch checks use `continue-on-error` (`build.yml:103-167`). This preserves CI momentum but means compile/link success is stronger than runtime confidence. Comments dated 2026-07-02 describe intended promotion but no issue/deadline enforces it.

Recommendation: make one hermetic Linux subtitle/GL/HaPlay launch path gating now that dependencies are installed in the workflow. Establish a separate non-gating hardware matrix. Turn deferred comments into tracked issues with owners/exit criteria.

### TEST-01 — Native/hardware component coverage is uneven (medium)

Strong areas include Core, Session, Control, GPU/Compositor, MMD parsing/physics, and broad HaPlay view-model behavior. Direct gaps include no dedicated NDI or MiniAudio test assembly, sparse dedicated FFmpeg tests relative to its surface, and only a small outbound ABI test suite. Presentation and native subtitle behavior rely heavily on smoke tools.

Recommendation: prioritize contract/fake-adapter tests that do not require hardware, then gate one real-runtime path per platform. Track coverage by behavioral contract, not raw line percentage.

### TEST-02 — Architecture tests omit first-party wrapper trees and retain nonexistent projects (medium)

Architecture discovery only scans `Media`, `Control`, and `Interop` (`ArchitectureTests.cs:64-87`), so first-party `Audio/PALib`, `Audio/MALib`, `MIDI/PMLib`, `NDI/NDILib`, `OSC/OSCLib`, and `Subtitles/LibAssLib` are not required to appear in the dependency rules. The allowed map still includes removed `S.Media.Encode.FFmpeg` and `S.Media.Images.Skia` entries (`:39,48,58-60`) and error/comments refer to `Next/` documentation that does not exist (`:7-15,103`).

Recommendation: enumerate every non-test/tool first-party project from the repository or solution, assert exactly one rule per project, reject stale rules, and store the current architecture contract in a real document.

### TEST-03 — High-risk findings lack targeted regression tests (medium)

Current green tests do not catch MMD bake-task retention/cancel retry, control queue overload, video pressure re-entrancy, C ABI header conformance/concurrent destroy, or appearance density no-op.

Recommendation: add a regression test with each fix rather than relying on the full suite. For concurrency/resource bugs, require repeated stress runs and bounded completion/memory assertions.

### DOCS-01 — Current documentation is insufficient and stale (medium)

The root README is the only current top-level guide and is a short informal description with stale `HaPlayer` naming, duplicated dependency entries, spelling errors, and no build/runtime/native-dependency instructions. `Directory.Build.props:3-5` says design docs live in `./Next/`, but that directory does not exist. Architecture/CI/AOT output still contain rewrite-era `Next` naming. The substantial `Old_Rewrite_Docs` content is explicitly stale.

Recommendation: replace the README with:

- supported OS/RID and required/optional native dependency matrix;
- quick-start host composition and basic playback example;
- ownership, threading, disposal, and real-time callback rules;
- project/module map and extension/plugin guidance;
- HaPlay build/run/config/security instructions;
- test/smoke commands and known hardware requirements.

Move current architecture decisions into maintained docs and remove migration-phase comments once verified.

### DOCS-02 — Public packages are not release-shaped (low/medium)

Most framework projects inherit build settings but do not provide package metadata, public API documentation output, compatibility policy, source-link/package validation, or a clear distribution model. This is acceptable for a monorepo-internal framework but conflicts with presenting it as a consumable framework.

Recommendation: decide whether distribution is source/project-reference, NuGet packages, or a single host SDK. Then add only the packaging metadata and API compatibility checks appropriate to that decision; avoid publishing dozens of accidental packages.

### BUILD-01 — AOT policy is broad but warning policy is loose (low)

Repository properties enable NativeAOT analyzers for all libraries (`Directory.Build.props:7-18`), which is good. Warnings are not treated as errors, stale/unreachable tool code remains, and the known Mond warning is accepted by comment. This can allow new AOT warnings to blend into existing output.

Recommendation: make compiler/analyzer warnings errors in first-party production projects with narrow, documented suppressions. Capture expected AOT warning IDs/counts so new warnings fail CI. Tools may use a more permissive policy where justified.

### BUILD-02 — CI downloads mutable/unverified native inputs (medium, supply chain)

Artifact jobs download scripts and “latest” FFmpeg archives directly during CI (`build.yml:225-250, 261-345`) without checksums. A mutable upstream asset or compromised endpoint changes release contents without a repository change.

Recommendation: pin immutable release URLs/commits, verify SHA-256 checksums, record licenses/versions in the artifact, and preferably mirror/cache approved native inputs. Generate a software bill of materials for each artifact.

## Recommended quality gates

1. Required-native manifest and load probe for each uploaded RID.
2. Zero unexpected build/AOT warnings.
3. Full unit/architecture suite on Linux and Windows.
4. Gating Linux software-GL HaPlay launch, subtitle render, and C ABI conformance.
5. Windows published-app launch plus PortAudio/libass load probes.
6. Stress suite for routing/control/session cancellation and disposal races.
7. Review every best-effort CI step quarterly; best-effort must not become an undocumented permanent state.

