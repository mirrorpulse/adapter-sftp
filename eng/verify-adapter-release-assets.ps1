[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory, [Parameter(Mandatory)][string]$SourceSha)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$manifest = Get-Content -LiteralPath (Join-Path $AssetDirectory 'provider-release.json') -Raw | ConvertFrom-Json
$version = Get-AdapterReleaseVersion $manifest.version
if ($manifest.schemaVersion -ne 1 -or $manifest.publish -isnot [bool] -or $manifest.sourceSha -cne $SourceSha -or $SourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or
    $manifest.repository -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z' -or $manifest.tag -cne "v$version" -or
    $manifest.channel -cnotin @('stable', 'preview') -or ($version.Contains('-preview.') -ne ($manifest.channel -ceq 'preview'))) {
    throw 'The release identity, source, or channel is inconsistent.'
}
if (@($manifest.files).Count -ne 3 -or @($manifest.files.name | Select-Object -Unique).Count -ne 3) { throw 'The release inventory must contain exactly the package, signature, and public key.' }
foreach ($file in $manifest.files) {
    if ([IO.Path]::GetFileName($file.name) -cne $file.name -or $file.name.Contains('\') -or $file.name.Contains('/') -or
        $file.sha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'The release inventory contains an invalid identity.' }
    $path = Join-Path $AssetDirectory $file.name
    if ((Get-Item -LiteralPath $path).Length -ne $file.length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw 'A signed release asset differs from the verified candidate.' }
}
$packages = @($manifest.files | Where-Object { $_.name.EndsWith('.mpadapter', [StringComparison]::Ordinal) })
if ($packages.Count -ne 1) { throw 'The release inventory must identify one package.' }
$package = Join-Path $AssetDirectory $packages[0].name
foreach ($expected in @(($packages[0].name + '.signature.json'), ($packages[0].name + '.public.pem'))) {
    if ($expected -cnotin @($manifest.files.name)) { throw 'The release inventory contains an unrelated signature or public key.' }
}
Assert-AdapterPackageIdentity -PackagePath $package -ExpectedVersion $version
& (Join-Path $PSScriptRoot 'verify-adapter-signature.ps1') -PackagePath $package
Write-Host "Verified immutable Provider candidate $($manifest.tag) from $SourceSha."
