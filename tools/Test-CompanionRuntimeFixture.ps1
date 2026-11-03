#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BinaryPath,
    [Parameter(Mandatory)][string] $SnapshotPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$binary = Get-Item -LiteralPath (Resolve-Path -LiteralPath $BinaryPath).Path
$snapshot = Get-Item -LiteralPath (Resolve-Path -LiteralPath $SnapshotPath).Path
if ($binary.PSIsContainer -or $snapshot.PSIsContainer -or $binary.Length -lt 32 -or
    $binary.Length -gt 128MB -or $snapshot.Length -gt 8192) {
    throw 'Choose a bounded own-image fixture and runtime JSON, not directories or unrelated dumps.'
}
$binaryBefore = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash
$snapshotBefore = (Get-FileHash -LiteralPath $snapshot.FullName -Algorithm SHA256).Hash
Push-Location -LiteralPath $repositoryRoot
try {
    # Build the solution before this check. The file is parsed by our CLI, never executed.
    $output = & dotnet run --project tools/PhoneLink.OffsetDumper --no-build -- $binary.FullName --runtime $snapshot.FullName --symbol _phonelink_runtime_probe_marker
    if ($LASTEXITCODE -ne 0) { throw 'OffsetDumper rejected the fixture; runtime interoperability is not verified.' }
    $report = ($output -join [Environment]::NewLine) | ConvertFrom-Json -Depth 64
    $native = Get-Content -LiteralPath $snapshot.FullName -Raw | ConvertFrom-Json -Depth 4
    $mapping = $report.RuntimeTextMapping
    if ($mapping.ExactSymbolName -cne '_phonelink_runtime_probe_marker') { throw 'The CLI did not apply the exact marker filter.' }
    if ($report.StaticInspection.RuntimeAddressesAvailable -ne $false -or
        $mapping.SnapshotOriginAuthenticated -ne $false -or $mapping.LiveProcessVerified -ne $false) {
        throw 'Fixture output incorrectly claims authenticated or live-verified runtime access.'
    }
    $markers = @($mapping.TextEntries | Where-Object { $_.StaticEntry.Name -ceq '_phonelink_runtime_probe_marker' })
    if ($markers.Count -eq 0) { throw 'The companion marker was not mapped; this does not verify the roundtrip.' }
    foreach ($marker in $markers) {
        if ($marker.SnapshotDerivedAddress -cne $native.markerRuntimeAddress -or
            $marker.StaticEntry.ImageRelativeOffset -cne $native.markerImageOffset) {
            throw 'The file marker and the actual captured marker address do not agree.'
        }
    }
    if ((Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash -ne $binaryBefore -or
        (Get-FileHash -LiteralPath $snapshot.FullName -Algorithm SHA256).Hash -ne $snapshotBefore) {
        throw 'An input changed during verification; discard the result.'
    }
    [pscustomobject]@{
        FixtureMarkerMatches = $true
        MatchedMarkerRecords = $markers.Count
        ImageUuid = $mapping.ImageUuid
        Architecture = $mapping.Architecture
        BinarySha256 = $binaryBefore
        InputsUnchanged = $true
        SnapshotOriginAuthenticated = $false
        LiveProcessVerified = $false
        Note = 'File/snapshot interoperability only. This script does not authenticate fixture provenance or inspect a live phone.'
    }
}
finally { Pop-Location }
