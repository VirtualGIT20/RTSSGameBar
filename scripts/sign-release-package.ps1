param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$PfxPath
)

$ErrorActionPreference = 'Stop'
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$PfxPath = [IO.Path]::GetFullPath($PfxPath)

if (-not (Test-Path -LiteralPath $PackagePath)) { throw "Package not found: $PackagePath" }
if (-not (Test-Path -LiteralPath $PfxPath)) { throw "PFX not found: $PfxPath" }

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $sdkBin) {
        $candidate = Get-ChildItem -LiteralPath $sdkBin -Filter signtool.exe -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw 'signtool.exe was not found. Install the Windows SDK or Visual Studio Windows development tools.'
}

$signtool = Find-SignTool
Write-Host "SignTool: $signtool"

$secure = Read-Host 'PFX password' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    $pfxCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($PfxPath, $plain)
    if ($pfxCert.Subject -ne 'CN=VirtualGIT20') {
        throw "Signing certificate Subject must be CN=VirtualGIT20, found: $($pfxCert.Subject)"
    }

    & $signtool sign /fd SHA256 /f $PfxPath /p $plain $PackagePath
    if ($LASTEXITCODE -ne 0) { throw "SignTool signing failed with exit code $LASTEXITCODE" }

    Write-Host ''
    Write-Host 'Verifying package signature...'
    $verifyOutput = & $signtool verify /pa /v $PackagePath 2>&1
    $verifyExit = $LASTEXITCODE
    $verifyOutput | ForEach-Object { Write-Host $_ }

    if ($verifyExit -eq 0) {
        Write-Host ''
        Write-Host 'Signature verification succeeded.'
        exit 0
    }

    $verifyText = ($verifyOutput | Out-String)
    $trustedPeople = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $pfxCert.Thumbprint } |
        Select-Object -First 1

    $isUntrustedRootOnly = $verifyText -match 'root certificate which is not trusted|CERT_E_UNTRUSTEDROOT|0x800B0109'
    if ($isUntrustedRootOnly -and $trustedPeople) {
        Write-Warning 'SignTool Authenticode chain verification reports an untrusted self-signed root, but the exact signing certificate is present in LocalMachine\TrustedPeople. This is expected for the local MSIX sideload workflow. The package was signed successfully.'
        Write-Host "TrustedPeople certificate: $($trustedPeople.Subject) [$($trustedPeople.Thumbprint)]"
        exit 0
    }

    throw "SignTool verification failed with exit code $verifyExit"
}
finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $plain = $null
}
