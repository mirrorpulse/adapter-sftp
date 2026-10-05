[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory, [Parameter(Mandatory)][string]$SourceSha)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$metadata = Get-Content -LiteralPath (Join-Path $AssetDirectory 'provider-version.json') -Raw | ConvertFrom-Json
if ($metadata.schemaVersion -ne 1 -or $metadata.sourceSha -cne $SourceSha -or $SourceSha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'The signed candidate source differs from its resolved source.' }
$packages = @(Get-ChildItem -LiteralPath $AssetDirectory -File -Filter '*.mpadapter')
if ($packages.Count -ne 1) { throw 'Exactly one signed Provider candidate is required.' }
$package = $packages[0]
Assert-AdapterPackageIdentity -PackagePath $package.FullName -ExpectedVersion $metadata.version
& (Join-Path $PSScriptRoot 'verify-adapter-signature.ps1') -PackagePath $package.FullName
$files = @($package.FullName, ($package.FullName + '.signature.json'), ($package.FullName + '.public.pem'))
$inventory = @(foreach ($file in $files) {
    $item = Get-Item -LiteralPath $file
    [ordered]@{ name = $item.Name; length = $item.Length; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
})
[ordered]@{
    schemaVersion = 1; repository = $metadata.repository; sourceSha = $SourceSha
    version = $metadata.version; tag = $metadata.tag; channel = $metadata.channel
    publish = $metadata.publish; files = $inventory
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $AssetDirectory 'provider-release.json') -Encoding utf8
