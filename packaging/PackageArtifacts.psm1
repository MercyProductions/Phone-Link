Set-StrictMode -Version Latest

function Publish-PhoneLinkArtifact {
    param([Parameter(Mandatory)][string]$ArtifactRoot, [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$CertificatePath, [Parameter(Mandatory)][string]$LogPath,
        [string]$SourceRevision = 'unknown', [bool]$SourceDirty = $true)
    $root = [IO.Path]::GetFullPath($ArtifactRoot)
    $buildId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
    $directory = Join-Path $root "builds\$buildId"
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $package = Join-Path $directory 'PhoneLink-x64.msix'
    $certificate = Join-Path $directory 'PhoneLink-Development.cer'
    Copy-Item -LiteralPath $PackagePath -Destination $package -ErrorAction Stop
    Copy-Item -LiteralPath $CertificatePath -Destination $certificate -ErrorAction Stop
    Copy-Item -LiteralPath $LogPath -Destination (Join-Path $directory 'package-build.log') -ErrorAction Stop
    $manifest = [ordered]@{ FormatVersion = 1; BuildId = $buildId; CreatedUtc = [DateTime]::UtcNow.ToString('O');
        SourceRevision = $SourceRevision; SourceDirty = $SourceDirty;
        PackageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash;
        CertificateSha256 = (Get-FileHash -LiteralPath $certificate -Algorithm SHA256).Hash }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'build.json') -Encoding utf8
    # Publish only after the complete matched set exists. A failed build/copy never
    # replaces the last successful selection or its package/certificate files.
    $pointer = Join-Path $root ("latest-" + [Guid]::NewGuid().ToString('N') + '.tmp')
    @{ FormatVersion = 1; BuildId = $buildId } | ConvertTo-Json | Set-Content -LiteralPath $pointer -Encoding utf8
    $latestPath = Join-Path $root 'latest.json'
    if ([IO.File]::Exists($latestPath)) {
        $backup = Join-Path $root ('latest.previous-' + [Guid]::NewGuid().ToString('N') + '.json')
        [IO.File]::Replace($pointer, $latestPath, $backup)
    }
    else { [IO.File]::Move($pointer, $latestPath) }
    return $directory
}

function Resolve-PhoneLinkArtifact {
    param([Parameter(Mandatory)][string]$ArtifactRoot, [string]$BuildId)
    $root = [IO.Path]::GetFullPath($ArtifactRoot)
    if (-not $BuildId) {
        $pointer = Join-Path $root 'latest.json'
        if ((Get-Item -LiteralPath $pointer -ErrorAction Stop).Length -gt 4096) { throw 'Artifact pointer exceeds its limit.' }
        $latest = Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json
        if ($latest.FormatVersion -ne 1) { throw 'Unsupported artifact pointer.' }
        $BuildId = $latest.BuildId
    }
    if ($BuildId -notmatch '^\d{8}-\d{6}-[0-9a-f]{32}$') { throw 'Invalid build ID; paths are not accepted.' }
    $directory = [IO.Path]::GetFullPath((Join-Path $root "builds\$BuildId"))
    $boundary = $root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $directory.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build directory escapes artifact root.' }
    $manifestPath = Join-Path $directory 'build.json'
    if ((Get-Item -LiteralPath $manifestPath -ErrorAction Stop).Length -gt 16384) { throw 'Build manifest exceeds its limit.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.FormatVersion -ne 1 -or $manifest.BuildId -ne $BuildId) { throw 'Unsupported or mismatched build manifest.' }
    $package = Join-Path $directory 'PhoneLink-x64.msix'
    $certificate = Join-Path $directory 'PhoneLink-Development.cer'
    foreach ($entry in @(@($package, $manifest.PackageSha256), @($certificate, $manifest.CertificateSha256))) {
        if ($entry[1] -notmatch '^[0-9A-Fa-f]{64}$' -or
            (Get-FileHash -LiteralPath $entry[0] -Algorithm SHA256 -ErrorAction Stop).Hash -ne $entry[1]) { throw 'Artifact hash mismatch. Rebuild; no installation was attempted.' }
    }
    return [pscustomobject]@{ Directory = $directory; PackagePath = $package; CertificatePath = $certificate; Manifest = $manifest }
}

Export-ModuleMember -Function Publish-PhoneLinkArtifact, Resolve-PhoneLinkArtifact
