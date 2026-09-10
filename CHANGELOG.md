# Changelog

All notable public changes to RTSS Game Bar are documented here.

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
