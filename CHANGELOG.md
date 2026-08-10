# Changelog

All notable public changes to RTSS Game Bar are documented here.

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
