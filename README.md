# RTSS Game Bar

RTSS Game Bar is an independent Xbox Game Bar widget for controlling common RivaTuner Statistics Server (RTSS) Global settings directly with a gamepad, mouse, or keyboard.

The project is designed around controller-first navigation and a lightweight runtime: RTSS state is reconciled only while the widget is visible, and the helper/plugin spend their idle time waiting on named pipes rather than polling continuously.

> RTSS Game Bar is an independent third-party project. RivaTuner Statistics Server is required and is not included with this repository or its releases.

## Features

- Global RTSS frame limiter with precise slider control.
- Common frame-rate presets: Unlimited, 30, 40, 60, 90, 120, 144, 165, 240, and 360 FPS.
- RTSS limiter type selection.
- Limiter and OSD enable/disable controls.
- OSD size control.
- Eight native RTSS OSD position presets with read-back of changes made directly in RTSS.
- RTSS start/close control.
- Install, update, and remove the small RTSS integration plugin from the widget.
- Status-aware Xbox controller navigation.
- Native Xbox Game Bar Back/B behavior.

## Requirements

- Windows 10 version 2004 (build 19041) or newer.
- Xbox Game Bar.
- RivaTuner Statistics Server installed locally.
- x64 Windows for the packaged widget/helper. The RTSS client plugin is built as Win32/x86 for the RTSS plugin host.

## GitHub release installation

GitHub sideload releases can be signed with the project's self-signed code-signing certificate. A release should contain the signed `.msix` or `.appx`, the public `.cer`, and checksums. The private `.pfx` is never distributed.

For a self-signed release, import the supplied public certificate into `LocalMachine\TrustedPeople` from an elevated PowerShell, then install the package:

```powershell
Import-Certificate -FilePath .\RTSSGameBar-Signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\RTSSGameBar.Widget_1.0.0.0_x64.msix
```

After installation, open Xbox Game Bar and launch **RTSS Game Bar** from the widget menu. If Integration reports `Install` or `Update`, run that action once so the bundled RTSS plugin matches the widget.

If you want to remove RTSS Game Bar completely, use **Integration -> Remove** before uninstalling the app package. The RTSS plugin is stored in the RTSS installation directory and is therefore outside the MSIX package lifecycle.

## Build from source

Use Visual Studio 2022 / MSBuild with the UWP, C++, .NET Framework 4.8, and Windows SDK components required by the solution. The widget is intentionally pinned to `Microsoft.Gaming.XboxGameBar 7.3.2506120`.

From a Visual Studio Developer Command Prompt:

```cmd
scripts\build-release-package.cmd
```

For a local sideload build, create a signing identity once, trust its public certificate, then sign/install the generated package:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\create-signing-cert.ps1
# Run the next command from an elevated PowerShell:
powershell -ExecutionPolicy Bypass -File scripts\trust-signing-cert.ps1

powershell -ExecutionPolicy Bypass -File scripts\install-local-package.ps1
```

The signing script generates `artifacts\signing\RTSSGameBar-Signing.pfx` and `.cer`. `artifacts/` and all `.pfx` files are ignored by Git. Keep the PFX and its password private; only the CER may be published.

## Architecture

The UWP Game Bar widget communicates with a non-elevated full-trust helper through a per-user named pipe. The helper communicates with a minimal RTSS client plugin through a second named pipe. Elevation is requested only for explicit integration Install/Update/Remove operations that write the plugin into the RTSS installation directory.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for details, [docs/PACKAGING.md](docs/PACKAGING.md) for signing/sideloading, and [docs/PUBLISHING.md](docs/PUBLISHING.md) for the GitHub publication flow.

## Privacy

RTSS Game Bar has no telemetry, analytics, account system, or network service. Runtime state and logs remain local to the PC. See [docs/PRIVACY.md](docs/PRIVACY.md).

## Development status

`v1.0.0` is the first public baseline. The public package identity is `VirtualGIT20.RTSSGameBar`, publisher `CN=VirtualGIT20`, and package version `1.0.0.0`.

The public Widget/Helper IPC is protocol v19. The RTSS plugin IPC remains protocol v6. The bundled RTSS integration plugin is v1.0.0.

## License

RTSS Game Bar is released under the [MIT License](LICENSE).
