# MediaFramework trigger bus

`S.Media.Core.Triggers.TriggerBus` is the stable, allocation-free control surface for scripts, OSC, and MIDI. Protocol adapters and UI bindings call `Fire`; handlers register on ids.

## API

- `Register(triggerId, TriggerHandler)` / `Unregister(triggerId)`
- `Fire(triggerId, in TriggerPayload payload = default)` — returns `false` when nothing is registered (non-throwing)
- `TriggerPayload` — `None`, `Numeric` (double), or `Text` (short tail)
- `AudioTriggerRegistration.RegisterAudioClipPlayer(bus, id, player, router, outputId, …)` binds:
  - `{id}.fire`, `{id}.stop`, `{id}.stopAll`, `{id}.loop`

## Id naming convention

Use dot-separated paths so OSC addresses and MIDI maps stay aligned:

| Pattern | Example | Meaning |
|---------|---------|---------|
| `pad.<name>.fire` | `pad.kick.fire` | One-shot clip |
| `pad.<name>.stop` | `pad.kick.stop` | Stop that clip’s routes |
| `pad.<name>.loop` | `pad.hat.loop` | Latched loop + fire |
| `out.<id>.gain` | `out.ndi1.gain` | Route/output gain (host binds) |
| `transport.play` | `transport.play` | Session play (host binds) |
| `layer.<n>.opacity` | `layer.2.opacity` | Compositor layer (host binds) |

OSC: `OscTriggerBridge` fires `bus.Fire(oscAddress, payload)` using the message address as the id (pattern `//` catch-all).

MIDI: `MidiTriggerBridge` + `MidiTriggerProfile` map NoteOn / CC / ProgramChange to explicit ids.

## Mond / scripting rules

- Prefer `Fire` (void) over `Task` on hot paths.
- Avoid `params object[]`; use `TriggerPayload` or dedicated handlers.
- Bindings live in the host app (e.g. HaPlay), not in `S.Media.Core`.

## Related

- `MediaPlayer.Triggers` — per-player bus instance
- `Doc/MediaFramework-Refactor-Checklist-2026-05-22.md` — Phase 11
