[CmdletBinding()]
param(
    [ValidateSet('x64')]
    [string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\PhoneLink.Windows\PhoneLink.Windows.csproj'
$artifactDirectory = Join-Path $repoRoot 'artifacts\windows\msix'
$msbuildPath = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryDirectory = Join-Path $temporaryRoot ("PhoneLinkMsix\" + [Guid]::NewGuid().ToString('N'))
$packageOutputDirectory = Join-Path $temporaryDirectory 'Packages'
$buildLog = Join-Path $temporaryDirectory 'package-build.log'
$certificate = $null

if (-not (Test-Path -LiteralPath $msbuildPath -PathType Leaf)) {
    throw "MSBuild was not found at $msbuildPath. Install Visual Studio with .NET desktop and Windows app development workloads."
}

New-Item -ItemType Directory -Force -Path $artifactDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $temporaryDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $packageOutputDirectory | Out-Null

try {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject 'CN=AppPublisher' `
        -FriendlyName 'Phone Link local development signing' `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @(
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
            '2.5.29.19={text}'
        )

    Export-Certificate `
        -Cert $certificate `
        -FilePath (Join-Path $artifactDirectory 'PhoneLink-Development.cer') `
        -Force | Out-Null

    $arguments = @(
        $projectPath,
        '/restore',
        '/t:Build',
        '/p:Configuration=Release',
        "/p:Platform=$Architecture",
        "/p:RuntimeIdentifier=win-$Architecture",
        '/p:GenerateAppxPackageOnBuild=true',
        "/p:AppxPackageDir=$packageOutputDirectory/",
        '/p:UapAppxPackageBuildMode=SideloadOnly',
        '/p:AppxBundle=Never',
        '/p:AppxPackageSigningEnabled=true',
        "/p:PackageCertificateThumbprint=$($certificate.Thumbprint)",
        '/m',
        '/v:minimal',
        '/fl',
        "/flp:logfile=$buildLog;verbosity=normal"
    )
    & $msbuildPath @arguments
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $buildLog -PathType Leaf) {
            Select-String -LiteralPath $buildLog -Pattern ': error ' |
                Select-Object -Last 30 |
                ForEach-Object { Write-Error $_.Line }
        }
        throw "MSIX packaging failed with exit code $LASTEXITCODE."
    }

    $packages = Get-ChildItem -LiteralPath $packageOutputDirectory -Recurse -Filter '*.msix' -File
    if ($packages.Count -ne 1) {
        throw "Expected one MSIX package but found $($packages.Count)."
    }
    $destination = Join-Path $artifactDirectory "PhoneLink-$Architecture.msix"
    Copy-Item -LiteralPath $packages[0].FullName -Destination $destination -Force
    Copy-Item -LiteralPath $buildLog -Destination (Join-Path $artifactDirectory 'package-build.log') -Force

    Write-Output $destination
}
finally {
    if ($certificate) {
        $certificatePath = "Cert:\CurrentUser\My\$($certificate.Thumbprint)"
        if (Test-Path -LiteralPath $certificatePath) {
            Remove-Item -LiteralPath $certificatePath -Force
        }
    }

    $resolvedTemporaryDirectory = [System.IO.Path]::GetFullPath($temporaryDirectory)
    if ($resolvedTemporaryDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        $resolvedTemporaryDirectory -ne $temporaryRoot -and
        (Test-Path -LiteralPath $resolvedTemporaryDirectory)) {
        Remove-Item -LiteralPath $resolvedTemporaryDirectory -Recurse -Force
    }
}
