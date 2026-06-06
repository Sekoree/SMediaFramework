# HaPlay Control Device Profiles

Device profiles describe known MIDI and OSC devices as suggestions for scripts,
UI browsers, and learn-mode output. They are not hard compatibility gates: a
project can still use raw MIDI and OSC scripts when a profile is missing or
incomplete.

Profiles can live in three places:

- Built-in app profiles, currently generated for the Behringer X-Touch Mini (MC
  mode), the Behringer BCF2000 (14-bit motor faders + encoders), the X32/M32 OSC
  console, and the Behringer X-Air / Midas M-Air OSC console.
- User/app-level profile JSON files loaded from a profile directory.
- Project-level profile overrides stored inside the project file.

When repositories are combined, project profiles override user/app profiles, and
user/app profiles override built-ins with the same profile id.

## External JSON Files

External profiles are plain JSON files using camelCase property names. A minimal
MIDI profile looks like this:

```json
{
  "id": "user.xtouch-custom",
  "displayName": "User X-Touch Custom",
  "protocol": "Midi",
  "version": "1.0",
  "ports": [
    {
      "id": "midi-in",
      "displayName": "MIDI Input",
      "kind": "MidiInput"
    }
  ],
  "controls": [
    {
      "id": "button.1",
      "displayName": "Button 1",
      "kind": "Button",
      "midiNote": 60,
      "valueMode": "NoteMomentary"
    }
  ]
}
```

An OSC command profile entry can include commands and periodic tasks:

```json
{
  "id": "user.osc-device",
  "displayName": "User OSC Device",
  "protocol": "Osc",
  "ports": [
    {
      "id": "osc-remote",
      "displayName": "OSC Remote",
      "kind": "OscRemote"
    }
  ],
  "commands": [
    {
      "id": "main.fader",
      "displayName": "Main Fader",
      "address": "/main/fader",
      "valueKind": "NormalizedFloat",
      "access": "ReadWrite",
      "minValue": 0,
      "maxValue": 1,
      "cacheKey": "/main/fader"
    }
  ],
  "tasks": [
    {
      "id": "keepalive",
      "displayName": "Keep Alive",
      "isDefaultEnabled": true,
      "kind": "PeriodicOscSend",
      "address": "/xremote",
      "intervalMs": 8000
    }
  ]
}
```

## Import And Export

The profile repository loads every `*.json` file in a directory. Invalid files
are skipped and reported as load issues; valid files are available by id.

The same JSON shape is used for sharing profiles produced by learn mode:

- `DirectoryControlDeviceProfileRepository.SaveProfile(directory, profile)`
  writes one validated profile to `<profile-id>.json`.
- `DirectoryControlDeviceProfileRepository.ExportBuiltInProfiles(directory)`
  writes the built-in X-Touch Mini and X32 profiles as external JSON files.

This keeps learned mappings portable: a user can save a learned surface profile,
copy the JSON file to another machine, and load it as an app-level profile or a
project-level override.
