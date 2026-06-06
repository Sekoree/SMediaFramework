# HaPlay Control Device Profiles

Device profiles describe known MIDI and OSC devices as suggestions for scripts,
UI browsers, and learn-mode output. They are not hard compatibility gates: a
project can still use raw MIDI and OSC scripts when a profile is missing or
incomplete.

Profiles can live in three places:

- **Built-in profiles** shipped as JSON under `MediaFramework/Control/S.Control/Profiles/`.
  These files are embedded in `S.Control` and copied next to the library at build
  time. The runtime loader prefers a `Profiles/` folder beside the assembly when
  present; otherwise it reads the embedded resources. Regenerate the files from
  `BuiltInControlDeviceProfileFactory` via **Export built-ins** in the control
  workspace or `DirectoryControlDeviceProfileRepository.ExportBuiltInProfiles`.
- User/app-level profile JSON files loaded from a profile directory.
- Project-level profile overrides stored inside the project file.

When repositories are combined, project profiles override user/app profiles, and
user/app profiles override built-ins with the same profile id.

Built-in catalog (as of this writing):

| Profile id | Device |
| --- | --- |
| `behringer.xtouch-mini.mc` | X-Touch Mini (MC mode) |
| `behringer.bcf2000` | BCF2000 |
| `behringer.x32.osc` | X32 / M32 OSC |
| `behringer.xair.osc` | X-Air / M-Air OSC |

## External JSON Files

External profiles are plain JSON files using camelCase property names. Enums are
stored as numeric values (same as control system project files). A minimal MIDI
profile looks like this:

```json
{
  "id": "user.xtouch-custom",
  "displayName": "User X-Touch Custom",
  "protocol": 0,
  "version": "1.0",
  "ports": [
    {
      "id": "midi-in",
      "displayName": "MIDI Input",
      "kind": 0
    }
  ],
  "controls": [
    {
      "id": "button.1",
      "displayName": "Button 1",
      "kind": 0,
      "midiNote": 60,
      "valueMode": 0
    }
  ]
}
```

An OSC command profile can include commands, periodic tasks, and optional
**behaviors** that tell the runtime how to treat the device:

```json
{
  "id": "user.osc-console",
  "displayName": "User OSC Console",
  "protocol": 1,
  "defaultOscPort": 10023,
  "ports": [
    {
      "id": "osc-remote",
      "displayName": "OSC Remote",
      "kind": 2
    }
  ],
  "commands": [
    {
      "id": "main.fader",
      "displayName": "Main Fader",
      "address": "/main/fader",
      "valueKind": 1,
      "access": 2,
      "minValue": 0,
      "maxValue": 1,
      "cacheKey": "/main/fader"
    }
  ],
  "tasks": [
    {
      "id": "keepalive",
      "displayName": "Maintain /xremote",
      "isDefaultEnabled": true,
      "kind": 1,
      "address": "/xremote",
      "intervalMs": 8000
    }
  ],
  "behaviors": {
    "protocolMaintenance": {
      "renewIntervalMs": 8000,
      "maintenanceAddresses": ["/xremote", "/subscribe", "/meters"]
    },
    "meterBlobDecoder": "x32"
  }
}
```

### Task kinds

| Value | Name | Purpose |
| --- | --- | --- |
| `0` | `PeriodicOscSend` | Generic periodic OSC send |
| `1` | `ProtocolMaintenance` | X32/X-Air style keep-alive and subscriptions |

Tasks with `isDefaultEnabled: true` are copied into a new OSC device's
`periodicOscSends` when the device is added in the control workspace.

### Behaviors

| Field | Purpose |
| --- | --- |
| `protocolMaintenance` | Marks maintenance sends (`/xremote`, `/subscribe`, `/meters`) so the maintenance manager can route them separately from user periodic sends |
| `meterBlobDecoder` | When set to `"x32"`, incoming OSC meter blobs for that device are decoded using the X32 meter parser |

## Import And Export

The profile repository loads every `*.json` file in a directory. Invalid files
are skipped and reported as load issues; valid files are available by id.

The same JSON shape is used for sharing profiles produced by learn mode:

- `DirectoryControlDeviceProfileRepository.SaveProfile(directory, profile)`
  writes one validated profile to `<profile-id>.json`.
- `DirectoryControlDeviceProfileRepository.ExportBuiltInProfiles(directory)`
  writes all built-in profiles from `BuiltInControlDeviceProfileFactory` as
  external JSON files (use this to refresh `S.Control/Profiles/` after editing
  the factory).

This keeps learned mappings portable: a user can save a learned surface profile,
copy the JSON file to another machine, and load it as an app-level profile or a
project-level override.
