# Architecture

RTSS Game Bar uses three small runtime components plus an elevation-only setup executable.

## Widget

`RTSSGameBar.Widget` is the UWP Xbox Game Bar UI. It contains the controller-first focus graph, selects which RTSS profile is being edited, and displays/updates that profile's settings (Global by default).

The widget does not access RTSS directly. It communicates with the helper through Widget/Helper protocol v20 on the per-user named pipe `RTSSGameBar.v20`.

Status reconciliation runs every five seconds only while the widget is visible. Returning to a visible state triggers an immediate refresh. Individual features, including OSD position, do not create their own polling loops.

## Helper

`RTSSGameBar.Helper` is a non-elevated x64 full-trust process packaged with the widget. It discovers RTSS, manages the local IPC bridge, starts/closes RTSS when requested, and launches integration maintenance.

The helper normally waits on named pipes when idle. Integration Install/Update/Remove uses the separate setup executable with an explicit `runas` elevation request.

## RTSS integration plugin

`RTSSGameBar.RTSSPlugin` is a Win32/x86 RTSS client plugin installed into RTSS's `Plugins\Client` directory. Its public integration version is 1.1.0 and its wire protocol is v7 on `RTSSGameBar.RTSSPlugin.v7`.

The plugin exposes only a small whitelist of commands for state, profile enumeration, frame limiting, limiter type/state, OSD visibility/size/position, and graceful RTSS close. It has no generic property setter, installer, process launcher, or general-purpose file API.

## Profiles

RTSS does not expose a public API for "which profile is currently applied to the hooked 3D application". The plugin therefore returns two separate pieces of information:

- `profile`: the profile that was explicitly requested by the widget (validated against `EnumProfiles`), with Global fallback.
- `activeHint`: an advisory match between the enumerated profile names and the executable that most likely owns the game window. Matching is name-based only, so a profile renamed in RTSS produces no hint, and neither does an application whose window belongs to a helper process that RTSS does not hook (for example the Steam client UI, owned by `steamwebhelper.exe`).

The hint is resolved by the helper only during an on-demand status read, never from a background timer or watcher. When the most likely game executable matches an enumerated profile name, the widget preselects it; a manual selection always wins until the running game changes. When the executable is resolved but RTSS has no matching profile, the widget falls back to Global. When the game window cannot be resolved at all (for example a full-screen exclusive game while Game Bar is open), the helper keeps the last resolved hint instead of flapping back to Global, and write responses carry that same hint instead of clearing it.

Profile-scoped mutations are written to the selected profile only; the limiter-enabled and OSD-visible bits remain global RTSS runtime flags because RTSS does not expose them as profile properties.

OSD position uses RTSS's native normalized coordinates with `CoordinateSpace=0`:

- Top left: `(+1,+1)`
- Top center: `(0,+1)`
- Top right: `(-1,+1)`
- Middle left: `(+1,0)`
- Middle right: `(-1,0)`
- Bottom left: `(+1,-1)`
- Bottom center: `(0,-1)`
- Bottom right: `(-1,-1)`

The normal state request also reads the native RTSS position and resolves it back to a semantic preset, so changes made directly in RTSS are reflected by the widget without a second polling mechanism.

## Setup

`RTSSGameBar.Setup` is a native x64 Win32 executable with `requireAdministrator`. It runs only for explicit Integration Install/Update/Remove actions and copies or removes `RTSSGameBarPlugin.dll` in the RTSS installation directory. The native implementation intentionally keeps the existing Helper/Setup command-line and exit-code contract while avoiding a .NET Framework/CLR bootstrap dependency in the elevated packaged process. RTSS restart is delegated back to the normal helper.

## Focus/input principles

The UI uses a status-aware vertical XYFocus graph built from enabled/visible controls. It does not use custom Gamepad B handling, global PreviewKeyDown interception, GettingFocus redirects, `TryMoveFocus`, or controller-entry debounce.

When integration Install/Update is blocking normal controls, Refresh is removed from controller/tab focus and Integration self-links only downward. This makes a duplicate downward ingress harmless while leaving upward navigation native so focus can exit the widget.
