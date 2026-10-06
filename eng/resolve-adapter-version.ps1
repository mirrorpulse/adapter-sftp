[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Stable', 'Preview')][string]$Channel,
    [ValidateSet('Breaking', 'Feature', 'Fix')][string]$Bump = 'Fix',
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$VersionsFile,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')

if ($VersionsFile) {
    $fixture = Get-Content -LiteralPath $VersionsFile -Raw | ConvertFrom-Json
    $published = @($fixture.github)
} else {
    if ($Repository -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') { throw 'A canonical GitHub repository is required.' }
    $headers = @{ Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    if ($env:GITHUB_TOKEN) { $headers.Authorization = 'Bearer ' + $env:GITHUB_TOKEN }
    $published = @()
    try {
        for ($page = 1; $page -le 20; $page++) {
            $response = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases?per_page=100&page=$page" -Headers $headers
            $releases = @($response | ForEach-Object { $_ })
            foreach ($release in $releases) {
                if (-not $release.draft -and $release.tag_name.StartsWith('v', [StringComparison]::Ordinal)) {
                    $published += $release.tag_name.Substring(1)
                }
            }
            if ($releases.Count -lt 100) { break }
            if ($page -eq 20) { throw 'Provider release history exceeds the bounded query; review before publishing.' }
        }
    } finally { $headers.Clear() }
}

$versions = @(foreach ($value in $published) {
    # Ignore other prerelease channels without normalizing or rewriting their identities.
    if ($value -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)|-preview\.([1-9][0-9]*))?\z') { continue }
    $text = Get-AdapterReleaseVersion $value
    $parts = $text -split '-preview\.'
    [pscustomobject]@{ Base = [Version]$parts[0]; Preview = $(if ($parts.Count -eq 2) { [int]$parts[1] } else { $null }); Text = $text }
})
$stable = $versions | Where-Object { $null -eq $_.Preview } | Sort-Object Base -Descending | Select-Object -First 1
$base = if ($stable) { $stable.Base } else { [Version]'0.0.0' }
if ($Channel -eq 'Stable') {
    $version = switch ($Bump) {
        'Breaking' { "$($base.Major + 1).0.0" }
        'Feature' { "$($base.Major).$($base.Minor + 1).0" }
        'Fix' { "$($base.Major).$($base.Minor).$($base.Build + 1)" }
    }
} else {
    $preview = $versions | Where-Object { $null -ne $_.Preview } | Sort-Object Base, Preview -Descending | Select-Object -First 1
    $version = if ($preview -and $preview.Base -gt $base) {
        "$($preview.Base)-preview.$($preview.Preview + 1)"
    } else { "$($base.Major).$($base.Minor + 1).0-preview.1" }
}
$null = Get-AdapterReleaseVersion $version
$result = [ordered]@{
    schemaVersion = 1; repository = $Repository; channel = $Channel.ToLowerInvariant(); bump = $Bump.ToLowerInvariant()
    version = $version; tag = "v$version"; latestStable = $(if ($stable) { $stable.Text } else { $null }); githubVersions = $published
    reference = 'https://github.com/MirrorPulse/CfSharp/blob/f640b353eb2f18f0abba80c53feef123d1d80e49/eng/resolve-nuget-version.ps1'
}
$json = $result | ConvertTo-Json -Depth 6
if ($OutputPath) {
    New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))) -Force | Out-Null
    $json | Set-Content -LiteralPath $OutputPath -Encoding utf8
}
$json
