param(
    [string]$PackagePath,
    [string]$PfxPath = (Join-Path $PSScriptRoot '..\artifacts\signing\RTSSGameBar-Signing.pfx'),
    [string]$PackageName = 'VirtualGIT20.RTSSGameBar',
    [switch]$CleanInstall,
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'

function Resolve-LatestPackage {
    param([string]$Root)

    $candidate = Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Extension -in '.msix', '.appx' -and
            $_.DirectoryName -notmatch '\\Dependencies(\\|$)'
        } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if (-not $candidate) {
        throw "No .msix/.appx package was found under $Root. Run scripts\build-release-package.cmd first."
    }

    return $candidate.FullName
}

function Get-PackageVersionFromFileName {
    param([string]$Path)

    $name = [IO.Path]::GetFileName($Path)
    if ($name -match '_(\d+\.\d+\.\d+\.\d+)_') {
        return [Version]$Matches[1]
    }

    return $null
}

function Confirm-Removal {
    param(
        [string]$Message,
        [switch]$AssumeYes
    )

    if ($AssumeYes) { return $true }

    $answer = Read-Host "$Message [y/N]"
    return $answer -match '^(?i:y|yes)$'
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\AppxPackages'))

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Resolve-LatestPackage -Root $artifactRoot
}
else {
    $PackagePath = [IO.Path]::GetFullPath($PackagePath)
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package not found: $PackagePath"
}

$PfxPath = [IO.Path]::GetFullPath($PfxPath)
if (-not $SkipSign -and -not (Test-Path -LiteralPath $PfxPath)) {
    throw "PFX not found: $PfxPath"
}

Write-Host ''
Write-Host 'RTSS Game Bar local package install'
Write-Host "  Package: $PackagePath"
Write-Host "  Identity: $PackageName"

if (-not $SkipSign) {
    $signScript = Join-Path $PSScriptRoot 'sign-release-package.ps1'
    if (-not (Test-Path -LiteralPath $signScript)) {
        throw "Signing helper not found: $signScript"
    }

    Write-Host ''
    Write-Host 'Signing package...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript -PackagePath $PackagePath -PfxPath $PfxPath
    if ($LASTEXITCODE -ne 0) {
        throw "Package signing failed with exit code $LASTEXITCODE"
    }
}
else {
    Write-Warning 'Skipping package signing because -SkipSign was specified.'
}

$targetVersion = Get-PackageVersionFromFileName -Path $PackagePath
$installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue | Select-Object -First 1

if ($installed) {
    Write-Host ''
    Write-Host 'Installed package detected:'
    Write-Host "  Version:     $($installed.Version)"
    Write-Host "  PackageFull: $($installed.PackageFullName)"

    $installedVersion = [Version]$installed.Version
    $shouldRemove = $CleanInstall

    if (-not $shouldRemove -and $targetVersion) {
        if ($installedVersion -gt $targetVersion) {
            $shouldRemove = Confirm-Removal -Message "Installed version $installedVersion is newer than package version $targetVersion. Remove it for a clean install?"
            if (-not $shouldRemove) {
                throw 'Installation cancelled because MSIX cannot normally downgrade an installed package.'
            }
        }
        elseif ($installedVersion -eq $targetVersion) {
            $shouldRemove = Confirm-Removal -Message "Version $targetVersion is already installed. Remove and reinstall it?"
            if (-not $shouldRemove) {
                Write-Host 'Nothing to do.'
                exit 0
            }
        }
    }

    if ($shouldRemove) {
        Write-Host ''
        Write-Host 'Removing installed package...'
        Remove-AppxPackage -Package $installed.PackageFullName
        $installed = $null
    }
}

Write-Host ''
Write-Host 'Installing package...'
Write-Host 'Close Xbox Game Bar / App Installer first if Windows reports resources in use.'

try {
    Add-AppxPackage -Path $PackagePath
}
catch {
    $text = $_ | Out-String
    if ($text -match '0x80073D02') {
        Write-Warning 'Windows reports package resources in use. Close Xbox Game Bar and App Installer, then run this script again.'
    }
    elseif ($text -match '0x800B0109') {
        Write-Warning 'The signing certificate is not trusted for local-machine deployment. Run scripts\trust-signing-cert.ps1 from an elevated PowerShell.'
    }
    throw
}

$installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $installed) {
    throw 'Add-AppxPackage returned without an error, but the package identity was not found afterward.'
}

Write-Host ''
Write-Host 'Installation completed successfully:'
Write-Host "  Name:            $($installed.Name)"
Write-Host "  Version:         $($installed.Version)"
Write-Host "  PackageFullName: $($installed.PackageFullName)"
Write-Host "  InstallLocation: $($installed.InstallLocation)"
Write-Host ''
Write-Host 'Open Xbox Game Bar and run the RTSS Game Bar smoke test.'
