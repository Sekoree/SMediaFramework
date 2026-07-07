# Session and show-document review

Scope: `S.Media.Session`, including cue graph, show loading, clip/composition runtime, audio outputs, and serialized show contracts.

## Assessment

Session code has strong recent correctness work: serialized dispatch, cancellation of active fires, stage-before-swap document loading, validation before teardown, bounded cue traversal, and more than one hundred targeted tests. The remaining issues are schema contract gaps and continued concentration of responsibility in `ShowSession` and `ClipCompositionRuntime`.

## Findings

### DOC-01 — Validation permits malformed media and numeric data (medium)

`ShowDocumentValidator` checks IDs, cue references, composition dimensions/rates, audio gains/matrix indices, routes, and follow-on cycles (`ShowDocumentValidator.cs:26-132`). It does not reject:

- empty `ShowClipBinding.MediaPath`;
- negative `StartOffset`, `EndOffset`, `FadeIn`, or `FadeOut`;
- non-finite placement/crop/rotation/opacity values, non-positive destination sizes, or out-of-range opacity/crop;
- negative layer indices;
- `AudioStreamIndex < -1` or subtitle stream index `< -1`;
- non-positive per-route sample rate;
- empty audio-output group IDs.

Direct JSON is a public input through `ShowDocument.FromJson` and the C ABI, so UI validation is not sufficient. Non-finite placement values can propagate into compositor math, while invalid stream/rate values fail later and less clearly.

Recommendation: validate every serialized scalar and required path at load time, returning all errors. Add boundary/NaN/Infinity tests. Values whose validity depends on probed duration can be checked during staged graph creation without tearing down the live graph.

### DOC-02 — `Outputs` and `Devices` are dead schema fields (medium)

`ShowDocument` requires `Outputs`, `Routes`, and `Devices` positional collections (`ShowDocument.cs:220-227`). `ShowSession` normalizes `Outputs` and `Devices` (`ShowSession.cs:379-388`), but no runtime consumer uses them. `Outputs` also has the same `OutputPatchRoute` type as `Routes`, which makes the intended distinction unclear.

Impact: a host can persist configuration that looks authoritative but is silently ignored; the v1 schema carries accidental complexity that becomes harder to remove once external clients exist.

Recommendation: decide before stabilizing v1. Either implement and document these collections or remove/deprecate them with an explicit migration. Do not keep accepting ignored control data.

### SESSION-01 — Session orchestration remains a large change hotspot (medium)

`ShowSession.cs` is over two thousand lines and `ClipCompositionRuntime` is also large. `ShowSession` coordinates dispatch, document staging, cue firing, media opens, audio-output creation, group clocks, clip lifecycle, monitoring, route rebuilds, snapshots, and disposal. Extracted helpers such as `CueFireOrchestrator` and `VoicePlayer` demonstrate the right direction.

Recommendation: continue extracting around stable invariants: document staging/commit, transport-group runtime, clip factory, audio-output topology, and monitoring/end behavior. Keep all state mutations on the existing dispatcher.

### SESSION-02 — Text cues are HaPlay-private, not a framework capability (medium/API design)

HaPlay registers its own `text:` decoder (`UI/HaPlay/MediaRuntime.cs:157-160`) backed by UI-private Skia rendering. A serialized show containing that URI cannot render in the headless framework or outbound C host unless the consumer reproduces HaPlay internals. The architecture map still lists the removed `S.Media.Images.Skia` project.

Recommendation: either move the text source contract/provider into a first-party framework module or explicitly define text URIs as a HaPlay-only schema extension. Avoid presenting the same `ShowDocument` as portable if hosts interpret it differently.

### SESSION-03 — Stale comments weaken the contract (low)

`ShowDocument.cs:7-9` and `121-123` still describe playback behavior as a future “8b convergence slice,” although the runtime now honors these fields. These comments are likely to mislead future changes.

Recommendation: replace migration-phase comments with current behavioral guarantees and threading/ownership notes.

## What should remain

- Validate and stage a replacement document before modifying the live show.
- The dedicated dispatcher and active-fire cancellation solve difficult re-entrancy/order problems.
- Bounded/cycle-validated cue traversal is appropriate for unattended follow-on behavior.
- Source-generated JSON is a good NativeAOT choice.
- Existing failure-path, cancellation, routing, and lifecycle tests should remain gating.

