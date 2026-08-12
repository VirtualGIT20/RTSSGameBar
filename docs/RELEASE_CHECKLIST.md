# GitHub release checklist

## Source

- Package identity is `VirtualGIT20.RTSSGameBar`.
- Publisher is `CN=VirtualGIT20`.
- Package and assembly version is `1.0.1.0` for release `v1.0.1`.
- Widget/Helper protocol is v19 on `RTSSGameBar.v19`.
- RTSS plugin protocol remains v6 and bundled plugin version is `1.0.0`.
- Xbox Game Bar SDK remains pinned to `7.3.2506120`.
- No POC/RC product-facing metadata remains.
- `python scripts/static_check.py` passes.

## Build and signing

- Build `Release|x64` from a Visual Studio Developer Command Prompt.
- Sign with the private PFX whose Subject is `CN=VirtualGIT20`.
- Verify the signed package with SignTool.
- Never commit or upload the PFX/private key.
- Include only the public CER with a self-signed GitHub sideload release.
- Import the CER into `LocalMachine\TrustedPeople` on a clean test machine.

## Runtime smoke test

- Remove the old POC package before the public identity test.
- Confirm Game Bar discovers exactly one `RTSS Game Bar` widget.
- Test controller navigation and native B/Back.
- Test limiter slider and all presets.
- Test limiter type/state, OSD state/size, and all eight OSD positions.
- Test direct RTSS position changes and normal widget read-back.
- Confirm no periodic RTSS reads while the widget is hidden.
- Test RTSS start/close.
- Test Integration Install/Update/Remove and RTSS restart behavior.

## GitHub release

- Run `scripts\prepare-github-release.ps1` on the signed package.
- Verify `SHA256SUMS.txt` against the release files.
- Publish source tag `v1.0.1` and matching release notes.
- State clearly that RTSS is required and not bundled.
- State clearly that the project is independent/unofficial.
- Document that Integration -> Remove should be used before uninstalling the app package if the user wants the external RTSS plugin removed.
