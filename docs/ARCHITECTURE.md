# Architecture

RTSS Game Bar uses three small runtime components plus an elevation-only setup executable.

## Widget

`RTSSGameBar.Widget` is the UWP Xbox Game Bar UI. It contains the controller-first focus graph and displays/updates RTSS Global settings.

The widget does not access RTSS directly. It communicates with the helper through Widget/Helper protocol v19 on the per-user named pipe `RTSSGameBar.v19`.

Status reconciliation runs every five seconds only while the widget is visible. Returning to a visible state triggers an immediate refresh. Individual features, including OSD position, do not create their own polling loops.

## Helper

`RTSSGameBar.Helper` is a non-elevated x64 full-trust process packaged with the widget. It discovers RTSS, manages the local IPC bridge, starts/closes RTSS when requested, and launches integration maintenance.

The helper normally waits on named pipes when idle. Integration Install/Update/Remove uses the separate setup executable with an explicit `runas` elevation request.

## RTSS integration plugin

`RTSSGameBar.RTSSPlugin` is a Win32/x86 RTSS client plugin installed into RTSS's `Plugins\Client` directory. Its public integration version is 1.0.0 and its wire protocol remains v6 on `RTSSGameBar.RTSSPlugin.v6`.

The plugin exposes only a small whitelist of commands for state, frame limiting, limiter type/state, OSD visibility/size/position, and graceful RTSS close. It has no generic property setter, installer, process launcher, or general-purpose file API.

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

`RTSSGameBar.Setup` is an x64 executable with `requireAdministrator`. It runs only for explicit Integration Install/Update/Remove actions and copies or removes `RTSSGameBarPlugin.dll` in the RTSS installation directory. RTSS restart is delegated back to the normal helper.

## Focus/input principles

The UI uses a status-aware vertical XYFocus graph built from enabled/visible controls. It does not use custom Gamepad B handling, global PreviewKeyDown interception, GettingFocus redirects, `TryMoveFocus`, or controller-entry debounce.

When integration Install/Update is blocking normal controls, Refresh is removed from controller/tab focus and Integration self-links only downward. This makes a duplicate downward ingress harmless while leaving upward navigation native so focus can exit the widget.
