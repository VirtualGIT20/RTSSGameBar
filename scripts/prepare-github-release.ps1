param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [string]$CerPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CerPath)) {
    $CerPath = Join-Path $PSScriptRoot '..\artifacts\signing\RTSSGameBar-Signing.cer'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts\GitHubRelease\v1.0.0'
}

$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$CerPath = [IO.Path]::GetFullPath($CerPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Package not found: $PackagePath" }
if (-not (Test-Path -LiteralPath $CerPath)) { throw "CER not found: $CerPath" }

$packageExtension = [IO.Path]::GetExtension($PackagePath).ToLowerInvariant()
if ($packageExtension -notin '.msix', '.appx', '.msixbundle', '.appxbundle') {
    throw "Unsupported package extension: $packageExtension"
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CerPath)
if ($cert.Subject -ne 'CN=VirtualGIT20') {
    throw "Certificate Subject must be CN=VirtualGIT20, found: $($cert.Subject)"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$packageTarget = Join-Path $OutputDirectory ([IO.Path]::GetFileName($PackagePath))
$cerTarget = Join-Path $OutputDirectory 'RTSSGameBar-Signing.cer'
Copy-Item -LiteralPath $PackagePath -Destination $packageTarget -Force
Copy-Item -LiteralPath $CerPath -Destination $cerTarget -Force

$installText = @'
RTSS Game Bar v1.0.0 - GitHub sideload

Requirements:
- Windows 10 build 19041 or newer
- Xbox Game Bar
- RivaTuner Statistics Server (RTSS), installed separately

Install:
1. Open an elevated PowerShell in this directory.
2. Trust the public signing certificate:
   Import-Certificate -FilePath .\RTSSGameBar-Signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
3. Install the .msix/.appx package in this directory.
4. Open Xbox Game Bar and launch RTSS Game Bar.
5. If Integration shows Install or Update, run that action once.

Complete removal:
Use Integration -> Remove before uninstalling the app package if you also want the external RTSS plugin removed.

RTSS Game Bar is an independent third-party project. RTSS is required and is not included.
'@
Set-Content -LiteralPath (Join-Path $OutputDirectory 'INSTALL.txt') -Value $installText -Encoding UTF8

$publicFiles = Get-ChildItem -LiteralPath $OutputDirectory -File |
    Where-Object { $_.Extension -notin '.pfx', '.p12', '.key' -and $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name

$checksumLines = foreach ($file in $publicFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($file.Name)"
}
Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Value $checksumLines -Encoding ASCII

$privateMaterial = Get-ChildItem -LiteralPath $OutputDirectory -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.pfx', '.p12', '.key' }
if ($privateMaterial) {
    throw 'Private signing material was found in the public release directory. Remove it before publishing.'
}

Write-Host ''
Write-Host 'GitHub release directory prepared:'
Write-Host "  $OutputDirectory"
Write-Host ''
Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name)"
}
