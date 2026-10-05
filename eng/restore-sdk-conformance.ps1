[CmdletBinding()]
param([string]$ArtifactDirectory)
$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'sdk-conformance.lock.json') -Raw | ConvertFrom-Json
$sdk = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'adapter-sdk.lock.json') -Raw | ConvertFrom-Json
if ($pin.schemaVersion -ne 1 -or $pin.version -cne $sdk.version -or $pin.tag -cne $sdk.tag -or
    $pin.sourceSha -cne $sdk.sourceSha -or $pin.assets.Count -ne 3) { throw 'Conformance and SDK must use the same fixed published source.' }
$expected = @("MirrorPulse.Adapter.Conformance-$($pin.version)-win-arm64.zip", "MirrorPulse.Adapter.Conformance-$($pin.version)-win-x64.zip", "MirrorPulse.Worker.Spec-$($pin.version).zip")
if (@(Compare-Object @($pin.assets.name) $expected).Count -ne 0) { throw 'The conformance asset addresses are invalid.' }
$destination = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) "artifacts/sdk-conformance/$($pin.version)"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($asset in $pin.assets) {
    if ($asset.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or $asset.length -le 0 -or $asset.length -gt 64MB) { throw 'The conformance asset pin is invalid.' }
    $path = Join-Path $destination $asset.name
    if (-not (Test-Path -LiteralPath $path)) {
        $temporary = Join-Path $destination ([Guid]::NewGuid().ToString('N') + '.download')
        try {
            if ($ArtifactDirectory) { Copy-Item -LiteralPath (Join-Path $ArtifactDirectory $asset.name) -Destination $temporary }
            else { Invoke-WebRequest -Uri "https://github.com/MirrorPulse/adapter-template/releases/download/$($pin.tag)/$($asset.name)" -OutFile $temporary -MaximumRetryCount 3 -RetryIntervalSec 2 }
            if ((Get-Item -LiteralPath $temporary).Length -ne $asset.length -or
                (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) { throw 'The conformance asset differs from its fixed published hash.' }
            Move-Item -LiteralPath $temporary -Destination $path
        } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    }
    if ((Get-Item -LiteralPath $path).Length -ne $asset.length -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) { throw 'The cached conformance asset differs from its fixed published hash.' }
}
Write-Host "Verified original SDK conformance and specification assets $($pin.version)."
