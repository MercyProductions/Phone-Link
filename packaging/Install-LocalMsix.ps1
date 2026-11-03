[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([string]$BuildId)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactDirectory = Join-Path $repoRoot 'artifacts\windows\msix'
Import-Module (Join-Path $PSScriptRoot 'PackageArtifacts.psm1') -Force
$artifact = Resolve-PhoneLinkArtifact -ArtifactRoot $artifactDirectory -BuildId $BuildId
$packagePath = $artifact.PackagePath
$certificatePath = $artifact.CertificatePath

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw 'Build the local package with packaging\Build-LocalMsix.ps1 first.'
}

$publicCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath
)
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if ($publicCertificate.Subject -ne 'CN=AppPublisher') { throw 'Unexpected development certificate subject.' }
if ($null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $publicCertificate.Thumbprint) {
    throw 'The package signature does not match the supplied development certificate.'
}
if ($signature.Status -in @('HashMismatch', 'NotSigned', 'NotSupportedFileFormat', 'Incompatible')) {
    throw "The package signature is invalid ($($signature.Status))."
}
Write-Output "Selected build: $($artifact.Manifest.BuildId); source $($artifact.Manifest.SourceRevision); dirty=$($artifact.Manifest.SourceDirty)."
Write-Output "Local development certificate: $($publicCertificate.Subject); thumbprint $($publicCertificate.Thumbprint)."

if ($PSCmdlet.ShouldProcess(
    'CurrentUser\TrustedPeople',
    "Trust development certificate $($publicCertificate.Thumbprint)")) {
    Import-Certificate `
        -FilePath $certificatePath `
        -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
}

if ($PSCmdlet.ShouldProcess($packagePath, 'Install Phone Link MSIX')) {
    # The user must have approved trust (or already trust this certificate).
    # Validate full Windows signature policy before asking AppX to install it.
    $verified = Get-AuthenticodeSignature -LiteralPath $packagePath
    if ($verified.Status -ne 'Valid') { throw "Windows does not validate this package ($($verified.Status)). No package installation was attempted." }
    Add-AppxPackage -Path $packagePath
}
