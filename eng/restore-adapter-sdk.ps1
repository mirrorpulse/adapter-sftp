[CmdletBinding()]
param([string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'adapter-sdk.lock.json') -Raw | ConvertFrom-Json
if ($pin.schemaVersion -ne 1 -or $pin.packageId -cne 'MirrorPulse.Adapter.Sdk' -or
    $pin.version.Length -gt 64 -or $pin.version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.[1-9][0-9]*)?\z' -or
    $pin.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or $pin.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $pin.length -le 0 -or $pin.length -gt 4MB) { throw 'The SDK pin is invalid.' }
$asset = "$($pin.packageId).$($pin.version).nupkg"
if ($pin.asset -cne $asset -or $pin.tag -cne "sdk-v$($pin.version)") { throw 'The fixed SDK asset address is invalid.' }
$feed = Join-Path $repository 'artifacts/sdk-feed'
New-Item -ItemType Directory -Path $feed -Force | Out-Null
$destination = Join-Path $feed $asset
function Assert-PinnedPackage([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -ne $pin.length -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) {
        throw 'The fixed SDK package does not match its pinned length and SHA256.'
    }
}
if (Test-Path -LiteralPath $destination) {
    Assert-PinnedPackage $destination
} else {
    $temporary = Join-Path $feed ([Guid]::NewGuid().ToString('N') + '.download')
    try {
        if ($PackagePath) { Copy-Item -LiteralPath $PackagePath -Destination $temporary }
        else {
            $url = "https://github.com/MirrorPulse/adapter-template/releases/download/$($pin.tag)/$asset"
            Invoke-WebRequest -Uri $url -OutFile $temporary -MaximumRetryCount 3 -RetryIntervalSec 2
        }
        Assert-PinnedPackage $temporary
        Move-Item -LiteralPath $temporary -Destination $destination
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-Host "Verified pinned $($pin.packageId) $($pin.version) in the local SDK feed."
