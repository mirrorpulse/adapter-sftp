[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$version = Resolve-AdapterReleaseVersion
$tag = "v$version"
if ($env:MP_RELEASE_EVENT -eq 'workflow_dispatch') {
    & git tag -- $tag
    if ($LASTEXITCODE -ne 0) { throw 'Release tag creation failed.' }
    & git push origin "refs/tags/$tag"
    if ($LASTEXITCODE -ne 0) { throw 'Release tag push failed.' }
}
& gh release create $tag $PackagePath "$PackagePath.signature.json" --title "Adapter $version" --notes "Signed MirrorPulse Adapter release $version." --verify-tag
if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }
