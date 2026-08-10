# Packaging and GitHub sideload releases

## Public identity

The first public identity is frozen as:

- Package name: `VirtualGIT20.RTSSGameBar`
- Publisher: `CN=VirtualGIT20`
- Publisher display name: `VirtualGIT20`
- Product display name: `RTSS Game Bar`
- Initial package version: `1.0.0.0`

The signing certificate Subject must exactly match the manifest Publisher (`CN=VirtualGIT20`).

Older development packages used a different POC identity. Because the public identity is different, Windows can keep both installed at once. Remove the old development package before validating the public build to avoid duplicate Game Bar entries:

```powershell
Get-AppxPackage VirtualGIT20.RTSSGameBar.POC | Remove-AppxPackage
```

The public Widget/Helper pipe and helper mutex also use the v19 public namespace, so an accidental side-by-side development install cannot steal the new helper connection.

## Build an unsigned package

From a Visual Studio Developer Command Prompt:

```cmd
scripts\build-release-package.cmd
```

The script builds `Release|x64`, stages the x64 helper/setup executables and Win32 RTSS plugin, and requests an unsigned sideload package under `artifacts\AppxPackages`.

## Create the project signing identity

Generate the signing key on the Windows machine that will own the release identity:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\create-signing-cert.ps1
```

The script creates:

- `artifacts\signing\RTSSGameBar-Signing.pfx` - private signing key; never publish or commit it.
- `artifacts\signing\RTSSGameBar-Signing.cer` - public certificate; safe to distribute with GitHub sideload releases.

The generated Subject defaults to `CN=VirtualGIT20`, matching the manifest. After export, the temporary certificate/private key created in `CurrentUser\My` is removed; the PFX becomes the private copy that must be backed up securely.

For local sideload testing, trust only the public certificate in `LocalMachine\TrustedPeople` from an elevated PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\trust-signing-cert.ps1
```

Do not place the certificate in Trusted Root Certification Authorities.

## Sign and install locally

After building:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-local-package.ps1
```

This finds the newest main package, signs it with `RTSSGameBar-Signing.pfx`, detects the public package identity, and installs it. The PFX password is requested interactively and is not stored by the script.

To sign a specific package without installing it:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\sign-release-package.ps1 `
  -PackagePath <path-to-msix-or-appx> `
  -PfxPath artifacts\signing\RTSSGameBar-Signing.pfx
```

## Prepare a GitHub Release directory

After the package has been signed, assemble the public release files:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\prepare-github-release.ps1 `
  -PackagePath <path-to-signed-msix-or-appx>
```

The script copies the signed package and public CER into `artifacts\GitHubRelease\v1.0.0`, creates `SHA256SUMS.txt`, and refuses to copy any PFX/private key.

## Public release contents

A GitHub sideload release should contain only public material, for example:

```text
RTSSGameBar.Widget_1.0.0.0_x64.msix
RTSSGameBar-Signing.cer
SHA256SUMS.txt
```

The PFX must never appear in the repository, GitHub Actions artifacts, release assets, logs, or issue attachments.

## Versioning

Use semantic Git tags such as `v1.0.0`, while the Windows manifest uses four numeric components such as `1.0.0.0`.

Suggested mapping:

- `v1.0.0` -> `1.0.0.0`
- `v1.0.1` -> `1.0.1.0`
- `v1.1.0` -> `1.1.0.0`
- `v2.0.0` -> `2.0.0.0`

Keep versions monotonic for packages that share the same identity.

## Integration lifecycle note

The RTSS plugin is installed into the RTSS installation directory, outside the MSIX package. A normal Windows uninstall of RTSS Game Bar cannot automatically remove that external file. Users who want a complete removal should choose **Integration -> Remove** before uninstalling the app package.
