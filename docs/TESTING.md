# Testing

## Public v1.0.0 smoke test

1. Remove any old `VirtualGIT20.RTSSGameBar.POC` package so only the public widget is present.
2. Build/sign/install package `1.0.0.0` with a certificate whose Subject is `CN=VirtualGIT20`.
3. Open Xbox Game Bar and confirm the widget appears as `RTSS Game Bar`.
4. If the previously installed RTSS plugin is an older development build, confirm Integration shows `Update`, perform the update, and confirm the plugin reports v1.0.0 afterward.
5. Verify controller navigation through Frame limiter, Preset, Limiter type, Limiter, Overlay, OSD size, OSD position, RTSS, Integration, and Refresh.
6. In Install/Update-required state, confirm repeated Down stays on Integration while Up can exit the widget.
7. Verify native B/Back closes/exits according to Xbox Game Bar behavior.
8. Test all frame-limit presets and arbitrary slider values.
9. Test all eight OSD positions and change position directly in RTSS; while the widget is visible it should reconcile on the next normal refresh.
10. Hide the widget and confirm there is no periodic RTSS state polling; show it again and confirm immediate reconciliation.
11. Test RTSS start/close and integration Install/Update/Remove. For each integration action, confirm `%LOCALAPPDATA%\RTSSGameBar\setup.log` reaches `Setup started`, completes the requested file operation, and exits with code 0.
12. On Windows 11 25H2 build 26200.9168 or newer, repeat at least one Integration Update plus Remove/Install cycle and confirm the native `RTSSGameBar.Setup.exe` returns promptly with no lingering setup process. This specifically guards the packaged/elevated .NET Framework bootstrap regression reproduced on 26200.8973 and 26200.9168.
13. Before package uninstall, test Integration -> Remove and confirm the RTSS plugin file is removed.

## Static validation

Run from the repository root:

```powershell
python scripts\static_check.py
```

The checker validates package identity/version, XML syntax, protocol constants, controller/navigation invariants, OSD mapping, limiter presets, lightweight polling, signing/release scripts, and key integration constraints.
