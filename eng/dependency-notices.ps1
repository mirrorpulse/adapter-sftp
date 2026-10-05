function Copy-AdapterDependencyNotices {
    param([Parameter(Mandatory)][string]$AssetsPath, [Parameter(Mandatory)][string]$PackageRoot)
    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    $policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependency-notices.json') -Raw | ConvertFrom-Json
    $destination = Join-Path $PackageRoot 'licenses'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($notice in $policy) {
        if ($null -eq $assets.libraries.PSObject.Properties["$($notice.packageId)/$($notice.version)"]) {
            throw 'A redistribution notice does not match the locked dependency graph.'
        }
        foreach ($name in $notice.files) {
            if ($notice.PSObject.Properties['vendored']) {
                $source = Join-Path $PSScriptRoot $notice.vendored
                if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $notice.sha256) {
                    throw 'The exact upstream dependency license hash differs.'
                }
            } else {
                $sources = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
                    $path = Join-Path $_ ($notice.packageId.ToLowerInvariant() + '/' + $notice.version + '/' + $name)
                    if (Test-Path -LiteralPath $path -PathType Leaf) { $path }
                })
                if ($sources.Count -ne 1) { throw 'Exactly one restored dependency notice is required.' }
                $source = $sources[0]
            }
            Copy-Item -LiteralPath $source -Destination (Join-Path $destination ($notice.packageId + '-' + $notice.version + '-' + $name))
        }
    }
}
