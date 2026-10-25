# RTSSGameBar.RTSSPlugin v1.1.0

Minimal x86 RTSS client plugin. Pipe: `\\.\pipe\RTSSGameBar.RTSSPlugin.v7`.

Whitelisted commands: `PING`, `GET_STATE`, `SET_FRAME_LIMIT`, `SET_SYNC_LIMITER`, `SET_LIMITER_ENABLED`, `SET_OSD_VISIBLE`, `SET_OSD_ZOOM`, `SET_OSD_POSITION`, `CLOSE_RTSS`.

## Profiles

Profile-scoped commands accept an optional `profile=<percent-encoded name>` parameter. Omitting it targets the Global profile. Non-empty profile names are validated against RTSS `EnumProfiles` before any write, because `LoadProfile` silently falls back to Global for unknown names.

`GET_STATE` also accepts `hint=<percent-encoded executable>`. The plugin compares the hint against the enumerated profiles and returns `activeHint=<name>` when an exact profile match exists. The hint is advisory only; it never selects the profile that is written to.

Flags (`SET_LIMITER_ENABLED`, `SET_OSD_VISIBLE`) are global RTSS runtime flags, not profile properties, and stay profile-independent by design.

`GET_STATE` reports the selected profile's `PositionX`, `PositionY`, `CoordinateSpace`, and the matching semantic position preset when the values match one of RTSS's eight native perimeter positions. `SET_OSD_POSITION` accepts preset indices 0-7 and writes/verifies the native normalized `PositionX` + `PositionY` pair together with `CoordinateSpace=0`.

The plugin intentionally contains no product UI, controller logic, generic property setter, file API, process launcher, installer, or arbitrary privileged command surface.
