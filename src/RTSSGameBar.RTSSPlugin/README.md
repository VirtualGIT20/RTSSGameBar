# RTSSGameBar.RTSSPlugin v1.0.0

Minimal x86 RTSS client plugin. Pipe: `\\.\pipe\RTSSGameBar.RTSSPlugin.v6`.

Whitelisted commands: `PING`, `GET_STATE`, `SET_FRAME_LIMIT`, `SET_SYNC_LIMITER`, `SET_LIMITER_ENABLED`, `SET_OSD_VISIBLE`, `SET_OSD_ZOOM`, `SET_OSD_POSITION`, `CLOSE_RTSS`.

`GET_STATE` reports Global-profile `PositionX`, `PositionY`, `CoordinateSpace`, and the matching semantic position preset when the values match one of RTSS's eight native perimeter positions. `SET_OSD_POSITION` accepts preset indices 0-7 and writes/verifies the native normalized `PositionX` + `PositionY` pair together with `CoordinateSpace=0`.

The plugin intentionally contains no product UI, controller logic, generic property setter, file API, process launcher, installer, or arbitrary privileged command surface.
