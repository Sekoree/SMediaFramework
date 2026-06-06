# HaPlay Full UI/UX Review Findings

Date: 2026-06-06

## Scope

Reviewed the HaPlay UI after the latest control and cue fixes, focusing on
what an end user would see rather than only whether the implementation is wired.

Checked surfaces:

- Main navigation, project status, saved window restore, and workspace switching.
- Players, playlist, player settings, routing, and audio matrix entry points.
- Cues, cue details drawer, cue action editing, cue target configuration, and
  related action target dialogs.
- Outputs, MIDI ports, Control, Project, and the control script editor.

Method:

- Static XAML/view-model review under `UI/HaPlay`.
- Clean first-run launch with temporary app settings under Xvfb at 1280x900.
- Existing-settings launch to catch saved-window and saved-workspace behavior.
- Log scan for obvious Avalonia binding/runtime errors.

The launch logs only showed ALSA device-probe errors from the headless test
environment. No Avalonia binding failures were found in the captured logs. No
physical MIDI controller, OSC device, X32/M32, NDI source, or real audio device
was exercised.

## Implementation Status

Implemented the first fix batch on 2026-06-06. The detailed findings below are
kept as the review snapshot that drove the changes.

Completed:

- Cues now use compact `Add cue` and `Selected cue...` menus, hide the detail
  editor when no cue is selected, show an empty state, and surface the selected
  action target in the action tab.
- Action targets are labeled consistently, the action builder filters endpoints
  by OSC/MIDI kind, and cue MIDI actions share a parser/builder with support for
  the wider MIDI message set.
- Control has an above-the-fold `Add script...` entry point, clearer structure
  guidance, row-specific context actions, and a compact empty monitor state.
- Outputs, MIDI Ports, and Project now have explicit first-run empty states.
  Output advanced routing is behind an `Advanced routing` expander and the
  user-facing `§8.2` marker was removed.
- The restored main window size is clamped to the active screen working area.
- The duplicate direct `Audio Matrix...` player button was removed, and cue
  text labels now use less terse wording.

Validation:

- `dotnet test UI/HaPlay.Tests/HaPlay.Tests.csproj --no-restore -v:m` passed:
  397 tests, 0 failures.
- `dotnet build UI/HaPlay.Desktop/HaPlay.Desktop.csproj --no-restore -v:m`
  passed with 0 warnings and 0 errors.
- `dotnet build MFPlayer.sln -m:1 --no-restore -v:m` passed with 0 errors and
  3 existing FFmpeg obsolete API warnings in `FfmpegAudioEncoder.cs`.
- Headless screenshot passes checked Cues, Outputs, MIDI Ports, Control, and
  Project at 1280x900. The only log matches were ALSA device-probe messages
  from the headless environment; no Avalonia binding or XAML failures were
  found.

## Overall Read

The player deck is the most mature surface. The transport area, playlist add
menu, routing strip, and overflow settings menu are reasonably understandable
for a first-time user.

The largest UX risk is now in the cue and control surfaces. They have gained
powerful behavior, but several entry points still expose internal concepts or
old terminology. A new user can reach states where the app appears editable but
has no target selected, or where the label promises one thing but the dialog
configures a broader and different thing.

## Priority Findings

### P1: Cue details drawer shows editable controls when no cue is selected

When the Cues workspace opens with no selected cue, the drawer title correctly
says to select a cue, but editable General controls can still appear below it.
This makes the lower pane look active even though there is no object being
edited.

Evidence:

- The drawer header is always present and the `TabControl` is always visible
  (`UI/HaPlay/Views/CuePlayerView.axaml:170-175`).
- Only individual `TabItem`s are hidden by selection state
  (`UI/HaPlay/Views/CuePlayerView.axaml:176-178`), which can leave stale tab
  content visible.
- The view model exposes a clear `HasSelectedCue` boolean that could be used to
  gate the whole editor (`UI/HaPlay/ViewModels/CuePlayerViewModel.cs:1412-1415`).

Recommendation:

- Bind the whole details `TabControl` to `HasSelectedCue`.
- Show one simple empty state below the drawer title when no cue is selected.
- Reset the selected tab when `SelectedCueNode` changes so hidden tab content
  cannot remain selected.

### P1: Cue command bars are too crowded and mix creation, editing, and transport

The Cues workspace has a long row of cue creation and edit commands followed by
a separate transport row. On narrower windows this becomes wrapped/clipped, and
many buttons are disabled until a cue is selected.

Evidence:

- The creation/edit row has 12 direct buttons:
  `Add group`, `Add media`, `Add image`, `Add text`, `Add NDI input`,
  `Add PortAudio input`, `Add action`, `Add comment`, `Browse media`,
  `Edit action`, `Renumber`, and `Remove`
  (`UI/HaPlay/Views/CuePlayerView.axaml:54-67`).
- Transport controls sit directly below in another dense row
  (`UI/HaPlay/Views/CuePlayerView.axaml:69-77`).

Recommendation:

- Replace the add buttons with one `Add cue` menu or split button grouped by
  cue type.
- Move selection-only actions like browse/edit/remove into the selected-cue
  drawer and the cue row context menu.
- Keep transport controls as a compact, stable command bar.

### P1: Action cue target/type UX can produce confusing missing-endpoint states

The current action cue flow splits the important information across three
places: a top-level `OSC Targets...` button, the selected cue drawer's raw
action text fields, and the action builder dialog. The selected endpoint is not
visible in the drawer. The builder dialog lists all endpoints regardless of the
selected action kind, but runtime execution resolves OSC and MIDI endpoints by
type.

Evidence:

- The Cues workspace button says `OSC Targets...`, but its tooltip says it
  configures OSC/MIDI targets (`UI/HaPlay/Views/MainView.axaml:211-214`).
- The target configuration dialog contains both OSC and MIDI tabs
  (`UI/HaPlay/Views/Dialogs/TargetConfigurationDialog.axaml:21-27`).
- The action drawer exposes `Action kind` and raw `Action command`, but not the
  selected endpoint (`UI/HaPlay/Views/CuePlayerView.axaml:556-570`).
- The action builder dialog endpoint combo is populated from all endpoints
  (`UI/HaPlay/Views/Dialogs/ActionCueBuilderDialog.axaml:28-39`).
- The dialog view model loads every endpoint without filtering by action kind
  (`UI/HaPlay/ViewModels/Dialogs/ActionCueBuilderDialogViewModel.cs:66-73`).
- MIDI execution only resolves `MidiActionEndpoint`; an OSC endpoint id on a
  MIDI action returns a missing-endpoint error
  (`UI/HaPlay/ViewModels/MainViewModel.cs:1144-1151`).

Recommendation:

- Rename the top-level button to `Action Targets...` or `OSC/MIDI Targets...`.
- Show the selected target name directly in the action cue drawer.
- Filter the builder endpoint list by the selected action kind.
- If the action kind changes, clear incompatible endpoint selections or offer a
  matching replacement.

### P1: Control says "script-centric" but hides scripts below the first screen

The Control workspace headline says the system is script-centric. On a clean
first run, the visible top of the workspace is arm/status, project structure,
and monitor; the Scripts card is below the first viewport. This makes the
answer to "how do I create a script?" harder to find.

Evidence:

- The header explains script-centric control
  (`UI/HaPlay/Views/ControlWorkspaceView.axaml:20-24`).
- The first major card is project structure with buttons for layer, OSC device,
  OSC listener, and MIDI resolution
  (`UI/HaPlay/Views/ControlWorkspaceView.axaml:47-64`).
- The Scripts card and add-script button are much lower in the scroll content
  (`UI/HaPlay/Views/ControlWorkspaceView.axaml:236-247`).

Recommendation:

- Move the Scripts card above Project structure, or add a top-row
  `Add script...` action beside the arm/status summary.
- Add a short empty-state line in Project structure that explains that layer
  scripts are added from a layer row or from the script editor scope selector.

### P1: Saved window size is restored without clamping to the current screen

Saved window position is clamped, but saved width and height are applied as-is.
If the app was last used on a larger display, a later launch on a smaller
display can open wider than the current screen and clip toolbar buttons.

Evidence:

- `OnOpened` applies saved width and height directly
  (`UI/HaPlay/Views/MainWindow.axaml.cs:57-62`).
- Only the top-left position is checked against visible screens
  (`UI/HaPlay/Views/MainWindow.axaml.cs:64-68`).
- This was observable in the existing-settings launch where the top Cues action
  target button was partially off-screen.

Recommendation:

- Clamp saved normal width and height to the working area of the target screen
  before applying them.
- If the saved position is invalid, center the clamped size on the primary
  screen.

### P2: Outputs first-run state is blank and exposes advanced routing too early

On a clean first run, the main output list is empty without a direct inline
empty state. At the same time, the advanced Shared Headphones and Virtual Audio
Channels sections are visible even though there are no outputs to configure.

Evidence:

- The output list has no empty-state content
  (`UI/HaPlay/Views/OutputManagementView.axaml:51-54`).
- Shared Headphones Buses and Virtual Audio Channels are always visible below
  the list (`UI/HaPlay/Views/OutputManagementView.axaml:117-184`).
- The Shared Headphones hint includes an implementation marker:
  `§8.2` (`UI/HaPlay/Resources/Strings.resx:81-82`).

Recommendation:

- Add an inline empty state: "No outputs yet. Add Local Video, PortAudio, or
  NDI output."
- Remove the `§8.2` marker from user-facing resources.
- Collapse Shared Headphones and Virtual Audio Channels under an `Advanced
  routing` expander until at least one compatible output exists.

### P2: MIDI Ports project lists are blank when empty and the role labels are close

The MIDI workspace has good top-level device discovery, but the project input
and output lists are blank when empty. The buttons `Use for Control` and
`Use for Control + Cues` are better than before, but still make users infer the
difference between a control device and a cue action target.

Evidence:

- Project MIDI input/output cards contain only list boxes when empty
  (`UI/HaPlay/Views/MidiDevicesView.axaml:79-139`).
- The add buttons are terse and sit under the available port lists
  (`UI/HaPlay/Views/MidiDevicesView.axaml:39-62`).

Recommendation:

- Add empty-state text to the project input/output cards.
- Consider labels like `Add as Control Input` and
  `Add as Control/Cue Output`.
- Add a short note that cue action outputs are the MIDI targets used by action
  cues.

### P2: Target configuration labels still sound OSC-only or too generic

There are now OSC and MIDI action targets, but visible names still vary between
`OSC Targets...`, `Target Configuration`, and `Cue OSC Connections`.

Evidence:

- Menu entry opens target configuration from the View menu
  (`UI/HaPlay/Views/MainView.axaml:75-78`).
- Cues workspace button says `OSC Targets...`
  (`UI/HaPlay/Views/MainView.axaml:211-214`).
- Dialog title resource is generic `Target Configuration`
  (`UI/HaPlay/Resources/Strings.resx:630-631`).
- OSC view header is `Cue OSC Connections`
  (`UI/HaPlay/Resources/Strings.resx:168-170`).

Recommendation:

- Standardize this area as `Action Targets`.
- Use tab labels `OSC Targets` and `MIDI Targets`.
- Keep any "connections" wording inside implementation comments, not primary UI.

### P2: Project Recent Projects empty hint appears to be unreachable

The Project workspace has an empty hint in XAML and resources, but the clean
first-run screenshot showed a blank Recent Projects card.

Evidence:

- The empty hint is declared in the Project workspace
  (`UI/HaPlay/Views/MainView.axaml:237-244`).
- The resource text exists
  (`UI/HaPlay/Resources/Strings.resx:151-154`).

Likely cause:

- `ConverterParameter=0` may be compared as a string parameter rather than an
  integer count by `ObjectConverters.Equal`.

Recommendation:

- Expose a `HasRecentProjects` / `HasNoRecentProjects` boolean on the view
  model, or use a converter that handles numeric parameters explicitly.

### P2: Control project structure row actions are duplicated and too broad

The Project structure row context menu is powerful, but it mixes project script
actions, helper files, layer actions, OSC listener actions, periodic sends,
device edits, and tests in one menu. Many of those actions also appear as inline
row buttons.

Evidence:

- Context menu contains a broad command set
  (`UI/HaPlay/Views/ControlWorkspaceView.axaml:78-100`).
- Inline row actions duplicate edit/remove/activate commands
  (`UI/HaPlay/Views/ControlWorkspaceView.axaml:123-142`).

Recommendation:

- Keep the context menu for row-specific actions only.
- Move global actions like "Add project script" and "Add imported helper file"
  to the Scripts card.
- Consider a selected-row details/action panel instead of many inline buttons.

### P2: Live monitor takes permanent space even when empty

The Control live monitor is useful, but on first run it reserves a large area
while the system is unarmed and there are no entries.

Evidence:

- The live monitor toolbar and list are fixed outside the configuration
  scroller (`UI/HaPlay/Views/ControlWorkspaceView.axaml:268-312`).

Recommendation:

- Show a compact empty state when unarmed and no entries exist.
- Let the monitor expand or pin only after entries arrive or the user opts in.

### P2: Cue action MIDI support is narrower than the new control MIDI surface

The Control scripting work has moved toward broader MIDI support, but cue action
MIDI still appears limited to note on, note off, CC, and program change. This
can confuse users who expect the same MIDI vocabulary everywhere.

Evidence:

- The action builder exposes `CueMidiCommandType` values only
  (`UI/HaPlay/Views/Dialogs/ActionCueBuilderDialog.axaml:47-55`,
  `UI/HaPlay/ViewModels/Dialogs/ActionCueBuilderDialogViewModel.cs:42-43`).
- MIDI command text is generated only for note on/off, CC, and program change
  (`UI/HaPlay/ViewModels/Dialogs/ActionCueBuilderDialogViewModel.cs:100-110`).
- Runtime parsing rejects anything outside `noteon`, `noteoff`, `cc`, and
  `pc/program` (`UI/HaPlay/ViewModels/MainViewModel.cs:1277-1286`).
- The error string says "Use noteon/noteoff/cc/pc"
  (`UI/HaPlay/Resources/Strings.resx:1503-1505`).

Recommendation:

- Either extend cue action MIDI to the same set as control scripts, or label it
  clearly as "simple cue MIDI action" and link users to script actions for full
  MIDI behavior.

### P3: Player settings are mostly good, with one duplicate advanced entry point

The Players workspace is relatively clean. The main concern is that
`Audio Matrix...` appears both as a direct button and inside the gear menu,
which makes it feel more important than it probably is for a first-run user.

Evidence:

- The direct `Audio Matrix...` button is in the player header
  (`UI/HaPlay/Views/MediaPlayerView.axaml:178-182`).
- The gear menu also includes `Audio Matrix...`
  (`UI/HaPlay/Views/MediaPlayerView.axaml:185-193`).

Recommendation:

- Keep `Audio Matrix...` in the gear menu only until outputs exist or advanced
  routing is enabled.

### P3: Cue text/image controls use terse technical labels

The text cue editor has compact labels such as `Align H`, `Align V`, `Wrap`,
and a hex color hint. This is acceptable for a power-user editor but rough for a
new cue operator.

Evidence:

- Text controls and color format hint
  (`UI/HaPlay/Views/CuePlayerView.axaml:540-551`).

Recommendation:

- Use labels like `Horizontal align`, `Vertical align`, and `Wrap width`.
- Add color picker buttons later, or at least place the hex hint directly near
  the color fields.

### P3: Some old inline action-builder view-model surface looks stale

The current UI routes action cue editing through `ActionCueBuilderDialog`, but
`CuePlayerViewModel` still has older inline builder fields and an
`ApplyActionBuilderCommand`. Some pieces are still used indirectly for the raw
Action tab, so this should be cleaned carefully rather than deleted blindly.

Evidence:

- Old builder fields exist on the cue player view model
  (`UI/HaPlay/ViewModels/CuePlayerViewModel.cs:1337-1357`).
- `ApplyActionBuilderCommand` writes the cue directly
  (`UI/HaPlay/ViewModels/CuePlayerViewModel.cs:3110-3125`).
- The dialog path writes the same cue state separately
  (`UI/HaPlay/ViewModels/CuePlayerViewModel.cs:3127-3155`).
- The old parser/generator still mirrors only the simple MIDI subset
  (`UI/HaPlay/ViewModels/CuePlayerViewModel.cs:3344-3436`).

Recommendation:

- Consolidate action cue editing around the dialog model or a shared action
  command editor model.
- Remove unused old command/properties after confirming no XAML or tests rely on
  them.

## Suggested First Fix Batch

1. Fix the no-selected-cue drawer state and tab reset.
2. Rename target configuration to Action Targets and filter endpoints by action
   kind in the action builder.
3. Move/add a visible Control `Add script...` CTA above the fold.
4. Clamp restored main-window size to the active screen.
5. Add empty states for Outputs, MIDI project lists, and Project Recent
   Projects.
6. Remove the `§8.2` resource marker and move Outputs advanced routing sections
   behind an expander.

These changes should remove the main user-facing confusion without touching the
core playback/control runtime.
