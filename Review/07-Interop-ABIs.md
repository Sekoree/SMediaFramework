# Plugin ABI and outbound C ABI review

Scope: `S.Abi`, `mfp_plugin.h`, `S.Media.Interop`, `s_media_player.h`, ABI smoke clients and tests.

## Assessment

The native plugin host contains thoughtful lifetime leasing: a requested unload waits for registered capabilities and opened adapters to release their plugin leases. Native callbacks use AOT-compatible unmanaged entry points, and a pure-C plugin smoke exercises the registration surface. The outbound player ABI, however, has concrete header/implementation mismatches and an unspecified concurrency lifetime.

## Findings

### ABI-01 — Outbound header and implementation disagree (high)

The public header defines `MFP_ERR_NOT_FOUND` and promises it for an unknown cue (`s_media_player.h:33-38, 80-84`). `NativeApi` has no such constant; `SessionFireCue` calls `Run`, whose catch maps every exception to generic `-1` (`NativeApi.cs:21-29, 180-188, 280-292`).

The header also defines idle, playing, paused, ended, and error states (`s_media_player.h:40-45`), while the implementation defines only idle/playing and reduces state to `IsRunning` (`NativeApi.cs:28-29, 212-214`). Consumers can branch on states that the library can never return.

Finally, `mfp_session_create` is documented as including available audio backends (`s_media_player.h:64-67`), but the implementation creates `ShowSession(..., audioBackend: null)` and explicitly describes it as silent/headless (`NativeApi.cs:121-127`).

Recommendation: choose the actual v1 contract before wider release. Implement precise error mapping and real state reporting, and either provide an audio-capable creation option or correct the header. Generate/shared-source status constants where possible and add a C conformance test for every documented return/state.

### ABI-02 — Destroy/shutdown can race active calls (high)

Handle lookup is protected and stale/random tokens are rejected, which is good. After `TryResolve` returns the managed `SessionBox`, however, the call holds no session lease. Another native thread can destroy that handle or call global shutdown and dispose the session/host while the first call uses it.

Impact: object-disposed failures at best; native runtime teardown during active decode/dispatch at worst. The header gives no rule prohibiting concurrent calls.

Recommendation: either implement per-session reference-counted call leases plus a closing state, or explicitly require caller serialization and enforce it with a session lock. `mfp_shutdown` should reject/defer until active calls drain. Add concurrent go/query/destroy/shutdown stress tests from C.

### ABI-03 — The outbound surface is under-tested relative to its stability claim (medium)

The Linux C smoke validates an empty show and treats media playback as best-effort; Windows is deferred. It does not verify documented errors, state transitions, invalid UTF-8/JSON, repeated init/shutdown, double destroy, concurrent access, or last-error thread locality.

Recommendation: make a table-driven pure-C ABI suite on Linux and Windows. Treat the header as the test specification. Run it against the exact published shared library artifact.

### PLUG-01 — Plugin threading/re-entrancy rules need to be in the header (medium)

The host has good unload leasing, but plugin callbacks can be invoked from decode, audio, video, UI/compositor, and disposal paths. `mfp_plugin.h` needs explicit statements for callback concurrency, whether callbacks may re-enter the host, who owns returned buffers, how long pointers remain valid, and which destroy calls may race work.

Recommendation: add a normative ownership/threading section and negative test plugins that return errors, malformed counts/formats, and delayed callbacks. Version all structures using size/version fields before extending them.

### PLUG-02 — Adapter disposal versus active native calls needs audit (medium)

Plugin library unload is leased, but individual adapter objects often use a Boolean disposed flag and call native `CloseHandle`/`Destroy` without an active-call lease (for example `NativeAudioBackend.cs:136-191`). A host that disposes an adapter concurrently with submit/read can close the native handle while the callback is executing.

Recommendation: define host serialization or add per-adapter operation leases. Test dispose during blocked submit/read/render with a deliberately slow test plugin.

## What should remain

- Opaque monotonically increasing tokens are safer than returning raw `GCHandle` pointers to C.
- Thread-local last-error storage is an appropriate C pattern.
- Plugin-level unload leasing and delayed `NativeLibrary.Free` are solid design choices.
- Compiling real C smoke clients is much stronger than managed tests of equivalent declarations.

