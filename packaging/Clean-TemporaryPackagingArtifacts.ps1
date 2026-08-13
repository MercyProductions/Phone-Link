$ErrorActionPreference = 'Stop'
$temporaryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path ([System.IO.Path]::GetTempPath()) 'PhoneLinkMsix')
)

if (-not (Test-Path -LiteralPath $temporaryRoot -PathType Container)) {
    return
}

$targets = Get-ChildItem -LiteralPath $temporaryRoot -Directory |
    Where-Object { $_.Name -match '^[0-9a-f]{32}PhoneLink\.Windows_' }
foreach ($target in $targets) {
    $resolved = [System.IO.Path]::GetFullPath($target.FullName)
    $requiredPrefix = $temporaryRoot + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected path $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
    Write-Output "Removed temporary package folder: $resolved"
}
