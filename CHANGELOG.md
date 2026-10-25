# Changelog

All notable public changes to RTSS Game Bar are documented here.

## 1.0.3 - 2026-10-24

### Added

- Profile awareness: the widget lists the RTSS application profiles and lets the user edit the one that most likely belongs to the running game, instead of only Global.
- The likely game profile is preselected from an on-demand foreground hint that is matched against the RTSS profile list inside the plugin. There is no background watcher: the hint is resolved only during an explicit refresh or when the widget becomes visible.
- A manual profile selection overrides the hint until the running game changes.

### Documented

- Recorded the limits of the profile hint in `README.md` and `docs/ARCHITECTURE.md`: matching is name-based, and applications whose window belongs to a helper process (for example the Steam client UI in `steamwebhelper.exe`) are not matched.

### Changed

- Widget/Helper IPC moved to protocol v20 on `RTSSGameBar.v20`.
- RTSS plugin IPC moved to protocol v7 on `RTSSGameBar.RTSSPlugin.v7`; the bundled plugin is now v1.1.0 and must be updated from the widget after upgrading.
- Writes to a non-empty profile name are validated against RTSS `EnumProfiles` first, because `LoadProfile` silently falls back to Global for unknown names.
- The limiter-enabled and OSD-visible bits stay global RTSS runtime flags; they are not profile properties.
- The profile hint now states whether the scope is automatic or a manual override (`Auto · detected game profile: …` / `Manual · editing … · Detected: …`), so it is always clear which profile the controls write to and why.

### Fixed

- Startup helper probes no longer record the expected cold-start named-pipe timeout as an IPC error in `widget-ipc.log`; only a launch that never succeeds is reported, through the launch result and one diagnostic line.
- RTSS plugin errors now reach the UI as readable text instead of the raw pipe payload: a refused write to a missing profile shows which profile was not found, and the technical code stays in `helper.log` as a single line without a stack trace.
- Switching profile while a write is still in flight no longer discards the profile list and the active profile hint; only the values of the profile that was written are dropped.
- The active profile hint is cleared when RTSS stops or is restarted, so a hint from a previous RTSS session cannot survive into the next one.
- Slow RTSS plugin reads now warn at 400 ms instead of 100 ms, because normal RTSS latency (100-200 ms) was filling `helper.log` with warnings against a 1500 ms read budget.
- The widget no longer drops the profile list or the active hint when a failed write is rolled back.
- A transient inability to resolve the game window no longer clears the hinted profile and moves the selection back to Global.

## 1.0.2 - 2026-09-10

### Fixed

- Made the Widget/Helper named-pipe server multi-client so a stale Game Bar widget process can no longer block a replacement widget from connecting to the helper.
- Hardened Game Bar lifecycle callbacks against WinRT/COM objects being invalidated while asynchronous dispatcher work is still queued.
- Stopped caching `SolidColorBrush` dependency objects in static fields; status brushes are now recreated for the active XAML view to avoid `InvalidComObjectException` after Game Bar view teardown/recreation.

### Changed

- Kept targeted local diagnostics for IPC failures/timeouts and lifecycle exceptions while removing verbose per-request success tracing used during investigation.
- Widget/Helper IPC remains protocol v19; RTSS plugin IPC remains protocol v6 and the bundled RTSS plugin remains v1.0.0.

## 1.0.1 - 2026-08-12

### Fixed

- Replaced the elevated .NET Framework integration setup executable with a native x64 Win32 implementation while preserving the existing `install` / `update` / `remove` command-line and exit-code contract. This avoids a Windows 11 25H2 packaged/elevated CLR bootstrap regression reproduced on builds 26200.8973 and 26200.9168.

## 1.0.0 - 2026-08-10

First public baseline.

### Added

- Xbox Game Bar widget for RTSS Global frame limiter, limiter mode, limiter state, OSD visibility, OSD size, and OSD position.
- Frame-limit presets for Unlimited, 30, 40, 60, 90, 120, 144, 165, 240, and 360 FPS.
- Eight native RTSS OSD position presets with read-back of external RTSS changes.
- RTSS start/close and integration install/update/remove actions.
- Status-aware controller focus graph and native Game Bar Back/B behavior.
- Visibility-aware five-second status reconciliation with no periodic RTSS reads while the widget is hidden.
- GitHub-oriented package identity, signing scripts, release documentation, and static CI checks.

### Changed

- Public package identity is now `VirtualGIT20.RTSSGameBar` with publisher `CN=VirtualGIT20` and package version `1.0.0.0`.
- Widget/Helper IPC moved from the development POC namespace to `RTSSGameBar.v19`, preventing collisions with older development packages.
- Bundled RTSS integration plugin version aligned to `1.0.0`; RTSS plugin wire protocol remains v6.
