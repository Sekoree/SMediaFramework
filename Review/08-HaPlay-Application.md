# HaPlay application engineering review

Scope: `HaPlay`, `HaPlay.Desktop`, application services, view models, persistence, remote API, diagnostics, and application-specific playback providers. Visual/interaction findings are in the separate UX report.

## Assessment

HaPlay is no longer merely a tiny demo: it has project persistence, players, cues, soundboards, output management, MIDI/OSC scripting, remote control, cache management, and substantial headless view-model tests. The main engineering risks are remote-control security/overload, broken appearance settings, fragile per-machine persistence, logging overhead, and very large coordinator/view-model classes.

## Findings

### API-01 — Copyable control URLs disclose a long-lived token over HTTP (high when LAN is enabled)

`RemoteApi` appends `?key=<token>` to every copied action URL (`RemoteApi.cs:17-31`). The server accepts query tokens and binds LAN as plain `http://*` when enabled (`RestApiServer.cs:43-75, 224-237`). Query secrets leak into clipboard history, browser history, screenshots, access/proxy/router logs, and potentially referrer headers. Anyone able to observe LAN traffic can capture a reusable control token.

The UI also renders the complete token as selectable text (`MainView.axaml:349-354`), and `AppSettings` stores it in plain JSON (`AppSettings.cs:43-58`). Loopback/off-by-default reduces the default exposure but does not make the LAN option safe.

Recommendation: default copied documentation to header-authenticated `POST` examples and mask the token with explicit reveal/copy/regenerate actions. For LAN, require HTTPS via an embedded certificate or clearly supported reverse proxy; alternatively use short-lived, scoped action tokens in URLs. Store the long-lived secret using OS credential storage when available and warn when falling back to a file.

### API-02 — State changes accept GET (medium/security semantics)

`RemoteApiDispatcher` accepts GET or POST for every endpoint (`RemoteApiDispatcher.cs:55-61`). GET requests can fire cues, tap soundboard tiles, and start player items. Browsers, previews, crawlers, caches, and link checkers may issue GET without an operator intending a state change.

Recommendation: make status/read endpoints GET and mutations POST only. Return `405` with `Allow`. Consider idempotency/request IDs for control integrations that retry.

### API-03 — Request concurrency and shutdown are unbounded/untracked (medium)

The accept loop launches each request as an untracked task (`RestApiServer.cs:115-139`). There is no concurrency limit, rate limit, request deadline, in-flight tracking, or cancellation passed to the UI dispatcher. `Stop` cancels/closes the listener without awaiting handlers (`:80-95`). A flood can allocate tasks and enqueue UI work; a handler may still mutate the view model after the API is disabled or restarted.

Recommendation: add a bounded concurrency gate, per-request timeout/shutdown token, maximum header/query limits, and in-flight task tracking. Stop should prevent new dispatch, cancel pending work, and await handlers with a bounded deadline.

### UI-01 — Theme and density settings do not match the installed theme (medium)

`App.axaml:7-10` installs `ClassicTheme` and says the app is forced to light because dark resources produce white-on-white controls. `AppearanceController.ApplyTheme` nevertheless selects system/dark variants (`AppearanceController.cs:10-23`). `ApplyDensity` searches `app.Styles` for `FluentTheme` (`:25-40`), but no Fluent theme is installed, so density is a no-op. The settings model still describes “Fluent density” (`AppSettings.cs:28-35`).

Recommendation: either remove/disable unsupported choices or implement them against Classic theme resources/classes. Add a headless UI test that changes each preference and asserts a measurable resource/control property, not just persisted enum state.

### SET-01 — Per-machine settings are overwritten non-atomically (medium)

`AppSettings.Save` calls `File.Create` on the live file and serializes in place, while swallowing all exceptions (`AppSettings.cs:78-92`). A crash, full disk, or interruption can leave a truncated file; `Load` catches everything and silently returns defaults (`:62-75`). This can lose window/dialog state, API configuration, and the token without diagnosis.

Recommendation: reuse the application's existing atomic temp-and-move approach (`ProjectIO.cs:278-319`), optionally retain one backup, and log/recover corrupt settings. Coalesce frequent window-placement writes.

### LOG-01 — File logging is expensive and queue signaling is inefficient (medium)

The logger flushes the file stream after every line (`RollingFileLogger.cs:70-83`). Its custom bounded queue repeatedly calls `ConcurrentQueue.Count` to enforce capacity and releases one semaphore permit per enqueue, while a single wake drains the entire queue (`:131-165`). This makes capacity approximate under concurrent writers and leaves surplus permits that can cause empty wake loops. The desktop default is 131,072 queued strings (`Program.cs:67-80`).

Recommendation: use `Channel.CreateBounded<string>` with `SingleReader` and `DropOldest`, track dropped lines, batch writes/flushes on a short interval, and force-flush only on warning/error or shutdown. Bound message length and reduce the default capacity after measuring bursts.

### APP-01 — Empty endpoint health is polled and traced every five seconds (low)

`MainViewModel` starts a five-second endpoint timer unconditionally (`MainViewModel.cs:84-92`). `RefreshAllEndpointHealthAsync` creates/replaces a CTS and emits timed-operation trace/debug records even when there are zero rows (`:781-820`). The live run produced two log entries every five seconds for `probed=0`.

Recommendation: disable the timer when there are no enabled endpoints, restart it on collection changes, and avoid success logs for empty/unchanged sweeps at normal trace settings.

### APP-02 — Composition-root and coordinator classes are oversized (medium)

`MainViewModel` is split into partial files, but it remains the hub for project state, endpoint health, REST lifecycle, players, cue/soundboard coordination, output state, settings, and navigation. `CueShowSessionCoordinator` also bridges many domains.

Recommendation: extract owned services with explicit lifetimes: remote API host, endpoint health monitor, project session, and workspace registry. Keep observable UI state in view models, but move timers/native lifecycle and persistence orchestration into testable services.

## What should remain

- Project persistence already uses source-generated JSON and atomic replacement.
- Optional module probing allows HaPlay to start without NDI or every native backend.
- Headless view-model tests are broad and valuable.
- The single `ShowSession` path for cues/soundboards avoids duplicate playback engines.
- Remote API is disabled and loopback-only by default; preserve those defaults.

