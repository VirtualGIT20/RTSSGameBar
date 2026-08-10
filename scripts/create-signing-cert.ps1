param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\signing'),
    [string]$Subject = 'CN=VirtualGIT20',
    [ValidateRange(1, 10)][int]$ValidityYears = 5
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $Subject `
    -FriendlyName 'RTSS Game Bar package signing' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyUsage DigitalSignature `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}') `
    -NotAfter (Get-Date).AddYears($ValidityYears)

$password = Read-Host 'Password for the exported signing PFX' -AsSecureString
$pfx = Join-Path $OutputDirectory 'RTSSGameBar-Signing.pfx'
$cer = Join-Path $OutputDirectory 'RTSSGameBar-Signing.cer'

Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null

# New-SelfSignedCertificate must create the key in a certificate store before it can be
# exported. Once both files exist, remove only that exact generated certificate from
# CurrentUser\My so the private key is not left installed in the user's Personal store.
$generatedStorePath = "Cert:\CurrentUser\My\$($cert.Thumbprint)"
if (Test-Path -LiteralPath $generatedStorePath) {
    Remove-Item -LiteralPath $generatedStorePath -Force
}

Write-Host ''
Write-Host 'Created RTSS Game Bar signing certificate:'
Write-Host "  Subject:    $($cert.Subject)"
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  PFX:        $pfx"
Write-Host "  CER:        $cer"
Write-Host ''
Write-Host 'For local sideload testing, import the CER into LocalMachine\TrustedPeople from an elevated PowerShell:'
Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
Write-Host ''
Write-Host 'The temporary CurrentUser\My certificate has been removed after export.'
Write-Host 'The PFX is ignored by git. Never commit or publish it.'
