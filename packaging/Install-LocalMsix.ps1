[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repoRoot 'artifacts\windows\msix'
$packagePath = Join-Path $artifactDirectory 'PhoneLink-x64.msix'
$certificatePath = Join-Path $artifactDirectory 'PhoneLink-Development.cer'

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw 'Build the local package with packaging\Build-LocalMsix.ps1 first.'
}

$publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath
)
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if ($null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint) {
    throw 'The package signature does not match the supplied development certificate.'
}

if ($PSCmdlet.ShouldProcess(
    'CurrentUser\TrustedPeople',
    "Trust development certificate $($publicCertificate.Thumbprint)")) {
    Import-Certificate `
        -FilePath $certificatePath `
        -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
}

if ($PSCmdlet.ShouldProcess($packagePath, 'Install Phone Link MSIX')) {
    Add-AppxPackage -Path $packagePath
}
