# MFPlayer / HaPlay critical review

Review date: 2026-07-05

## Scope and method

This review covers every first-party production project in `MediaFramework` and `UI`, the first-party native MMD shim, tests, tools, repository-level build configuration, and release automation. `External/`, `Reference/`, generated output, and third-party implementation details were excluded. The review combined source inspection, project/dependency inventory, test and build execution, NativeAOT publishing, and a live HaPlay run at 1407×944.

Commands used:

```text
dotnet test MFPlayer.sln -c Release --no-restore
dotnet build MFPlayer.sln -c Release --no-restore
dotnet publish MediaFramework/Tools/AotSmoke/AotSmoke.csproj -c Release -r linux-x64
dotnet run --project UI/HaPlay.Desktop/HaPlay.Desktop.csproj -c Release --no-build
```

Results: 1,538 tests discovered, 1,534 passed, 4 skipped, 0 failed. The incremental Release build passed with one warning (`SessionSmoke/Program.cs:222`, unreachable code). The Linux x64 AOT smoke published and ran successfully, with the known Mond `IL2026` trim warning.

Severity means:

- **High**: credible correctness, security, deadlock, unbounded-resource, or release-integrity failure.
- **Medium**: user-visible defect, important reliability gap, or material maintainability/performance risk.
- **Low**: polish, documentation debt, or optimization worth measuring.

## Priority findings

| ID | Severity | Finding | Primary component |
|---|---:|---|---|
| MMD-01 | High | Completed and cancelled physics-bake tasks remain in a process-wide dictionary forever; cancellation also prevents retry | MMD source |
| CTRL-01 | High | Control scripts consume an unbounded event channel, so input floods or a slow script can grow memory and latency without limit | Control runtime |
| ROUTE-01 | High | Video pump pressure callbacks execute while holding the pump lock, creating callback/re-entrancy risk and an ABBA lock path with router metrics | Video routing |
| ABI-01 | High | The outbound C header promises an error/state/audio contract the implementation does not implement | Outbound C ABI |
| API-01 | High when LAN is enabled | Copyable remote-control URLs put a long-lived token in the query string and transmit it over plain HTTP | HaPlay remote API |
| REL-01 | High | Release artifact jobs tolerate missing Windows native dependencies and upload without validating a required-native manifest | CI/release |
| UI-01 | Medium | Theme/density preferences are internally inconsistent; density currently searches for a theme that is not installed | HaPlay appearance |
| DOC-01 | Medium | `ShowDocument` accepts malformed numeric/path data and carries two normalized-but-unused schema collections | Session/document |
| YT-01 | Medium | Broad catches swallow caller cancellation during caption work | YouTube source |
| SET-01 | Medium | App settings overwrite in place and silently reset on a partial/corrupt write | HaPlay persistence |
| LOG-01 | Medium | The desktop logger flushes every line and has an inefficient queue/semaphore implementation | HaPlay diagnostics |
| A11Y-01 | Medium | 46 views contain no automation properties; soundboard tiles are pointer-only `Border` controls | HaPlay UX/accessibility |

No defect was classified as critical in the sense of an immediately exploitable default configuration or guaranteed data loss. The six high findings should nevertheless be addressed before treating the framework or HaPlay artifacts as production-ready.

## Recommended order

1. Fix MMD task eviction/cancellation, bound the control event queue, and move video callbacks outside locks. Add regression/stress tests with each fix.
2. Decide and enforce the outbound C ABI contract; then fix remote API token transport before encouraging LAN use.
3. Add artifact-native validation to CI, especially on Windows.
4. Harden `ShowDocument`, settings persistence, and YouTube cancellation.
5. Fix the appearance settings, keyboard/accessibility baseline, and live-operation control hierarchy.
6. Replace stale rewrite-era documentation and expand hardware/native test coverage.

## Component reports

- [Core, Time, and Players](01-Core-Time-Players.md)
- [Routing and Audio Backends](02-Routing-and-Audio.md)
- [FFmpeg, GPU, Compositor, Presentation, and NDI](03-Video-Decode-Presentation-NDI.md)
- [Session and Show Documents](04-Session-and-Show.md)
- [MMD, YouTube, and Subtitles](05-Sources-and-Subtitles.md)
- [Control, MIDI, and OSC](06-Control-MIDI-OSC.md)
- [Plugin ABI and Outbound C ABI](07-Interop-ABIs.md)
- [HaPlay Application Engineering](08-HaPlay-Application.md)
- [HaPlay UX and Accessibility](09-HaPlay-UX.md)
- [Build, Tests, Documentation, and Release](10-Engineering-Quality.md)

