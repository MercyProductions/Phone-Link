$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'PackageArtifacts.psm1') -Force
$testTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testDirectory = Join-Path $testTempRoot ('PhoneLinkArtifactTest-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$script:checks = 0
function Assert-ArtifactTest([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Assert-ArtifactFailure([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert-ArtifactTest $failed 'Expected artifact validation failure.'
}
try {
    $root = Join-Path $testDirectory 'artifacts'
    $package = Join-Path $testDirectory 'sample.msix'
    $certificate = Join-Path $testDirectory 'sample.cer'
    $log = Join-Path $testDirectory 'sample.log'
    'fixture-package-A' | Set-Content -LiteralPath $package
    'fixture-certificate-A' | Set-Content -LiteralPath $certificate
    'fixture-log' | Set-Content -LiteralPath $log
    $first = Publish-PhoneLinkArtifact -ArtifactRoot $root -PackagePath $package -CertificatePath $certificate -LogPath $log -SourceRevision 'fixture' -SourceDirty $false
    $resolved = Resolve-PhoneLinkArtifact -ArtifactRoot $root
    $firstId = $resolved.Manifest.BuildId
    Assert-ArtifactTest ($resolved.Directory -eq $first) 'Latest must select the complete first set.'
    Assert-ArtifactTest (-not $resolved.Manifest.SourceDirty) 'Source provenance must be retained.'
    $pointerBefore = Get-Content -LiteralPath (Join-Path $root 'latest.json') -Raw
    Assert-ArtifactFailure { Publish-PhoneLinkArtifact -ArtifactRoot $root -PackagePath (Join-Path $testDirectory 'absent.msix') -CertificatePath $certificate -LogPath $log }
    Assert-ArtifactTest ((Get-Content -LiteralPath (Join-Path $root 'latest.json') -Raw) -eq $pointerBefore) 'Failed publication must preserve last successful pointer.'
    Assert-ArtifactTest ((Resolve-PhoneLinkArtifact -ArtifactRoot $root).Manifest.BuildId -eq $firstId) 'Failed publication must preserve the matched old pair.'
    'fixture-package-B' | Set-Content -LiteralPath $package
    'fixture-certificate-B' | Set-Content -LiteralPath $certificate
    $second = Publish-PhoneLinkArtifact -ArtifactRoot $root -PackagePath $package -CertificatePath $certificate -LogPath $log
    Assert-ArtifactTest ($first -ne $second) 'Successful sets must not overwrite earlier sets.'
    Assert-ArtifactTest ((Resolve-PhoneLinkArtifact -ArtifactRoot $root -BuildId $firstId).Directory -eq $first) 'A prior build must remain selectable.'
    'corruption' | Add-Content -LiteralPath (Join-Path $second 'PhoneLink-x64.msix')
    Assert-ArtifactFailure { Resolve-PhoneLinkArtifact -ArtifactRoot $root }
    Assert-ArtifactFailure { Resolve-PhoneLinkArtifact -ArtifactRoot $root -BuildId '..\..\elsewhere' }
    '{"FormatVersion":99,"BuildId":"bad"}' | Set-Content -LiteralPath (Join-Path $root 'latest.json')
    Assert-ArtifactFailure { Resolve-PhoneLinkArtifact -ArtifactRoot $root }
    Write-Output "Package artifact checks passed: $script:checks. Fixtures only; no certificate trust or installation occurred."
}
finally {
    $resolvedTestDirectory = [IO.Path]::GetFullPath($testDirectory)
    $testBoundary = $testTempRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTestDirectory.StartsWith($testBoundary, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTestDirectory) -notlike 'PhoneLinkArtifactTest-*') { throw 'Unsafe test cleanup target.' }
    Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force
}
