# Publishing to GitHub

## Create the repository

From the repository root after reviewing the public files:

```powershell
git init
git add .
git commit -m "Initial public release"
git branch -M main

gh repo create RTSSGameBar --public --source=. --remote=origin --push
```

If the repository already exists on GitHub, add it as `origin` instead of using `gh repo create`.

## Create the v1.0.1 tag

```powershell
git tag -a v1.0.1 -m "RTSS Game Bar v1.0.1"
git push origin v1.0.1
```

## Build the release package

On the Windows release machine:

```powershell
scripts\build-release-package.cmd
powershell -ExecutionPolicy Bypass -File scripts\sign-release-package.ps1 `
  -PackagePath <path-to-package> `
  -PfxPath artifacts\signing\RTSSGameBar-Signing.pfx

powershell -ExecutionPolicy Bypass -File scripts\prepare-github-release.ps1 `
  -PackagePath <path-to-signed-package>
```

The public release directory must contain the signed package, public CER, install instructions, and checksums. It must not contain a PFX or private key.

## Create the GitHub Release

After the tag and public release directory are ready:

```powershell
gh release create v1.0.1 `
  artifacts\GitHubRelease\v1.0.1\* `
  --title "RTSS Game Bar v1.0.1" `
  --notes-file RELEASE_NOTES.md `
  --verify-tag
```

Review the generated release before announcing it publicly. The release description should state that RTSS is required, RTSS is not bundled, the project is independent/unofficial, and the supplied CER is only the public half of the self-signed package signing identity.
