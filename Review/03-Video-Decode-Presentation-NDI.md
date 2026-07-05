# FFmpeg, GPU, compositor, presentation, and NDI review

Scope: `S.Media.FFmpeg.Common`, `S.Media.Decode.FFmpeg`, `S.Media.Gpu`, `S.Media.Compositor`, `S.Media.Present.SDL3`, `S.Media.Present.SDL3.Compositor`, `S.Media.Present.Avalonia`, `S.Media.NDI`, `NDILib`.

## Assessment

This is ambitious, platform-sensitive code with a generally sensible separation between decoding, GPU handles, composition, and presentation. The compositor/GPU projects have useful unit tests and several real-context smoke tools. FFmpeg and NDI carry much more behavior than their automated test coverage suggests, and the release pipeline does not yet make their native runtime guarantees reliable.

## Findings

### FFMPEG-01 — Decoder risk is concentrated behind sparse direct tests (medium)

The FFmpeg projects include demux/decode, stream selection, hardware/native frame handling, conversion, resampling, adaptive-rate output, seeking, and cleanup, but the dedicated `S.Media.Decode.FFmpeg.Tests` project is small relative to this surface. Many important paths are exercised only indirectly or by smoke tools.

Recommendation: build a generated-fixture matrix covering audio-only/video-only/A+V, variable frame rate, missing timestamps, multi-track selection, corrupted/truncated input, repeated seek, cancellation during open/read, EOF, and CPU versus supported hardware-frame paths. Assert buffer ownership and native allocation stability across loops.

### FFMPEG-02 — Large demux/decode classes obscure state transitions (medium)

The shared demux implementation is a multi-thousand-line lifecycle/state machine. Native media code does require detail, but open, probe, packet ownership, decoder drain, seek flush, timestamp repair, and teardown should be independently reasoned about.

Recommendation: extract packet/codec ownership, timestamp normalization, and seek/drain coordination into internal components with invariant tests. Keep the public provider API unchanged.

### GPU-01 — Resource-context rules need public documentation (low)

GPU and presentation code necessarily depends on current GL/context/thread ownership. The implementation contains careful guards, but repository-level consumer documentation does not explain which thread may configure, submit, render, abandon, or dispose each output/surface.

Recommendation: publish one ownership/threading table for CPU frames, GL textures, DMABUF/D3D handles, compositor surfaces, and presenters. This is more valuable than scattered implementation comments for plugin and host authors.

### NDI-01 — No dedicated NDI test assembly (medium)

`S.Media.NDI` contains live receive, A/V correlation, buffering, video unpack/pack, sender timing, reconnect/fault signaling, and native-frame release logic. There are useful loopback/probe tools, but no `S.Media.NDI.Tests` project and the CI environment does not gate an NDI runtime path.

Recommendation: factor native capture/send calls behind a small adapter and unit-test timestamp correlation, buffer limits, format changes, reconnect, cancellation, and exactly-once frame release. Retain an opt-in real-runtime loopback soak.

### NDI-02 — Polling and sleep-based waits deserve soak telemetry (low)

NDI receive/probe paths use short sleeps and timed monitor waits (`NDIAudioBufferProbe.cs:99,139`; `NDISource.cs:313,425`; `NDIVideoReceiver.cs:185`). These can be reasonable around a polling native API, but behavior under disconnect/reconnect and shutdown should be measured rather than inferred.

Recommendation: include wakeup rate, buffered duration, dropped frames, reconnect count, and shutdown latency in diagnostics and soak assertions.

### PRESENT-01 — Real presenter launch gates are non-gating (medium)

SDL/Avalonia/GL correctness depends on real native/display contexts. The repository has good smoke executables, but several CI invocations use `continue-on-error`; see the release report for the consequence.

Recommendation: make one Linux software-GL presentation path gating and establish a proven Windows launch lane. Keep hardware-specific acceleration paths optional but visible.

## What should remain

- Hardware-frame types and CPU fallback paths are explicitly represented rather than hidden behind unsafe casts.
- The compositor is isolated from SDL/Avalonia, with a small SDL-compositor bridge project.
- Format-switch, multi-output, frame-dump, GL, and live-receive tools are useful diagnostic assets.
- NDI is optional at composition time, which prevents a missing runtime from breaking unrelated playback.

