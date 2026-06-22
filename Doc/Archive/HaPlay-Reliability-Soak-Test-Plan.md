# HaPlay Reliability Soak Test Plan

This plan is for long real-world playback sessions where the goal is to catch drift, leaks, stuck native resources, UI stalls, and crashes with enough evidence to explain the failure afterward.

The default HaPlay desktop logging level is `Trace`. For soak runs, keep that default unless disk space is the thing being tested.

## Logging Setup

Use a dedicated log directory per run:

```bash
DOTNET_CLI_HOME=/tmp dotnet run --project UI/HaPlay.Desktop/HaPlay.Desktop.csproj -- \
  --media-log-level trace \
  --media-log-dir /tmp/haplay-soak/$(date +%Y%m%d-%H%M%S)
```

For crash hunts where expected exception-control-flow noise is acceptable, add:

```bash
--media-log-first-chance
```

This logs first-chance exceptions at `Trace`. It can produce very large logs and can affect timing, so use it for targeted reproduction passes, not every overnight run.

Each run should preserve:

- `haplay-*.log` rolling log.
- `haplay-crash-*.log` crash-grade synchronous log.
- The `.haplay` project file.
- The exact media file list and device/output configuration.
- Operator notes with wall-clock timestamps for anything that felt wrong.

## Pass/Fail Signals

Treat any of these as a failure requiring log review:

- Process crash, forced close, or UI thread unhandled exception.
- `NativeResourceHealth` stuck-resource record.
- UI action blocks visibly for more than 500 ms, or transport/output teardown blocks for more than 2 s.
- A/V drift is audible/visible, or measured drift exceeds 20 ms after steady playback.
- Audio underruns, repeated output-pump pressure, dropped-frame bursts, or NDI receiver loss that does not recover.
- Memory, thread count, handle count, or log-reported queue depth grows continuously over a steady-state hour.
- Project save/load, output edit, panic, stop, or device reconnect leaves playback partially running or unrecoverable.

## Media Corpus

Keep a stable corpus so failures can be compared across commits:

- Long H.264/H.265 files: 2-4 hours, 1080p and 4K, CBR and VBR.
- High frame rate and odd frame rate files: 23.976, 29.97, 50, 59.94, VFR.
- Audio-only: WAV, FLAC, MP3/AAC, multichannel, very long duration.
- Still/attached-picture media and title/text cues.
- Alpha/high-bit-depth clips used by compositions.
- Known-problem files: corrupt tail, missing duration, timestamp gaps, format switches, weird channel layouts.
- Live sources: NDI sender and PortAudio input that can be disconnected/reconnected during a run.

## Hardware Matrix

Document the physical setup at the start of each run:

- CPU/GPU, OS, driver versions.
- PortAudio host API and device names.
- Output sample rate, channel count, buffer size/latency.
- NDI sender/receiver machines, wired vs Wi-Fi, switch/router model if relevant.
- Monitor/preview outputs and whether local SDL/OpenGL preview is enabled.
- Whether remote API is enabled, loopback or LAN, and controller type.

## Core Soak Scenarios

### 1. Idle Output Hold

Goal: prove persistent preview/output runtimes stay alive without leaks.

- Open the project.
- Enable the normal output set: PortAudio program, NDI output, local preview, headphones if used.
- Do not play anything for 8 hours.
- Every 30 minutes, note memory, thread count, CPU, and whether receivers still see stable signal.

Failure examples: output disappears, idle CPU rises, thread/memory count grows, stuck native-resource record appears.

### 2. Long Playlist Playback

Goal: prove normal deck playback survives real duration and transitions.

- Build a 4-hour playlist from mixed codecs and durations.
- Play through the whole list with local preview, PortAudio, and NDI active.
- Every 15 minutes, perform one normal operator action: pause/resume, seek +/- 30 s, next/previous, volume change, output hold toggle.

Failure examples: dead UI, A/V drift, wrong frame after seek, unreleased output, delayed stop/pause.

### 3. Cue Stack GO Run

Goal: stress cue engine, pre-roll, output mappings, composition runtimes, and fades.

- Use at least 40 cues: file cues, image/text cues, grouped cues, fades, mapped outputs, audio-only cues.
- Run the stack for 2 hours using realistic GO timing.
- Include panic/stop/restart cycles every 20-30 minutes.
- Change selected cue list and re-arm prepared cues during idle gaps.

Failure examples: cue starts late, group members visibly stagger, pre-roll refresh stalls UI, composition output maps incorrectly, stopped cue keeps audio/video alive.

### 4. Live Input Reconnect

Goal: prove live NDI/PortAudio inputs fail and recover cleanly.

- Play an NDI input cue for at least 30 minutes.
- Disconnect sender for 30 seconds, reconnect, repeat 10 times.
- Do the same with a PortAudio input if possible.
- Alternate between preview, cue playback, and player deck playback.

Failure examples: adapter exposes no real format, reconnect wires wrong format, capture thread gets stuck, stale receiver keeps device/network handle.

### 5. Remote API Controller Run

Goal: prove the token-protected control surface does not destabilize the UI.

- Enable Remote API with LAN mode only on a trusted test network.
- Use Companion, curl, or a small script to trigger cue GO, soundboard tiles, player transport, and status polling.
- Keep request rate realistic: 1-5 requests per second with occasional bursts.
- Use the UI normally during the run.

Failure examples: unauthorized requests accepted, authorized triggers block, UI and remote state diverge, API request throws outside dispatcher.

### 6. Project Save/Load During Output Churn

Goal: prove persistence and teardown stay coherent while devices are being edited.

- Start from a full project.
- Add/remove/reorder outputs, rename lines, edit audio matrices, change cue mappings.
- Save, close, reopen, and compare visible state.
- Repeat while playback is idle, while preview outputs are open, and immediately after stopping playback.

Failure examples: saved project loses routes, output teardown blocks, detached player remains alive after removal, stale devices keep exclusive handles.

## Optional Stress Scenarios

- Overnight random transport: scripted next/prev/seek/pause/stop/play on a copy of the project.
- NDI multi-receiver wall: several receivers connected for 4+ hours while cues play mapped compositions.
- Audio-device churn: unplug/replug or restart JACK/Pulse/PipeWire between cue runs.
- First-chance exception run: repeat a known repro with `--media-log-first-chance`.

## Collection Template

Use this template for each issue:

```text
Run id:
Commit:
OS / machine:
Project:
Media files:
Outputs / inputs:
Remote API mode:
Start time:
Failure time:
Observed symptom:
What the operator was doing:
Expected behavior:
Recovered without restart?:
Relevant log files:
Notes:
```

## Triage Order

1. Check `haplay-crash-*.log` for fatal, UI dispatcher, unobserved task, or native-resource health records.
2. Search the rolling log around the failure timestamp for `slow completion`, `NativeResourceHealth`, `Faulted`, `stuck`, `timed out`, `PumpPressure`, `underrun`, `overflow`, `Stop`, `Dispose`, `Seek`, and `Execute`.
3. Compare memory/thread/handle trend from the beginning and end of the run.
4. Reproduce with the same project and media but fewer outputs to isolate device/network vs decoder/compositor.
5. If crash details are missing, repeat once with `--media-log-first-chance`.
