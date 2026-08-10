#Requires -RunAsAdministrator
param(
    [string]$CerPath = (Join-Path $PSScriptRoot '..\artifacts\signing\RTSSGameBar-Signing.cer')
)

$ErrorActionPreference = 'Stop'
$CerPath = [IO.Path]::GetFullPath($CerPath)
if (-not (Test-Path -LiteralPath $CerPath)) { throw "CER not found: $CerPath" }

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CerPath)
$thumb = $cert.Thumbprint
$storePath = 'Cert:\LocalMachine\TrustedPeople'

$existing = Get-ChildItem $storePath -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $thumb } |
    Select-Object -First 1

if (-not $existing) {
    Import-Certificate -FilePath $CerPath -CertStoreLocation $storePath | Out-Null
    $existing = Get-ChildItem $storePath |
        Where-Object { $_.Thumbprint -eq $thumb } |
        Select-Object -First 1
}

if (-not $existing) { throw 'Certificate import did not appear in LocalMachine\TrustedPeople.' }

Write-Host 'Local sideload trust is ready:'
Write-Host "  Subject:       $($existing.Subject)"
Write-Host "  Thumbprint:    $($existing.Thumbprint)"
Write-Host "  HasPrivateKey: $($existing.HasPrivateKey)"
Write-Host 'No CurrentUser certificate store is modified by this helper.'
