# RTSS Game Bar v1.0.3

Feature release adding RTSS profile awareness on top of the existing Global controls.

Highlights:

- The widget can now show and edit the RTSS profile that most likely belongs to the running game instead of only Global. Profile names are enumerated from RTSS itself; the likely game profile is preselected from an on-demand foreground hint, and Global remains the fallback.
- A manual profile choice stays in effect until the running game changes. If the game window cannot be resolved at all (for example a full-screen exclusive game while Game Bar is open), the widget keeps the last known hint instead of flipping back to Global.
- Profile names are validated against RTSS before any write. A missing or mistyped profile is refused and can never silently modify Global.
- The limiter-enabled and OSD-visible switches remain global RTSS runtime flags, because RTSS does not expose them as profile properties.
- Widget/Helper IPC is now protocol v20 (`RTSSGameBar.v20`).
- RTSS plugin IPC is now protocol v7 (`RTSSGameBar.RTSSPlugin.v7`) and the bundled plugin is v1.1.0. Update the integration from the widget after upgrading: the previous plugin is not compatible with this widget.
- Per-profile control requires an RTSS build that exports `EnumProfiles`. On older RTSS builds the widget stays on Global.
- The native x64 integration Setup and the Xbox Game Bar lifecycle hardening from v1.0.2 are unchanged.

RTSS is required and is not bundled. RTSS Game Bar is an independent third-party project.

For self-signed GitHub sideload builds, import the supplied public `RTSSGameBar-Signing.cer` into `LocalMachine\TrustedPeople` before installing the signed app package. The private PFX is never distributed.
