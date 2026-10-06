$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$root = Join-Path ([IO.Path]::GetFullPath('artifacts/adapter-version-verification')) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$cases = @(
    @{ github = @(); channel = 'Preview'; bump = 'Fix'; expected = '0.1.0-preview.1' },
    @{ github = @(); channel = 'Stable'; bump = 'Fix'; expected = '0.0.1' },
    @{ github = @('0.2.0'); channel = 'Stable'; bump = 'Fix'; expected = '0.2.1' },
    @{ github = @('0.2.0', '0.3.0-preview.2', '0.3.0-preview.10'); channel = 'Preview'; bump = 'Fix'; expected = '0.3.0-preview.11' },
    @{ github = @('0.3.0', '0.3.0-preview.9'); channel = 'Preview'; bump = 'Fix'; expected = '0.4.0-preview.1' },
    @{ github = @('0.2.0', '0.2.1'); channel = 'Stable'; bump = 'Feature'; expected = '0.3.0' },
    @{ github = @('0.2.0'); channel = 'Stable'; bump = 'Breaking'; expected = '1.0.0' },
    @{ github = @('1.2.3.4'); channel = 'Stable'; bump = 'Fix'; expected = '1.2.4' }
)
foreach ($case in $cases) {
    $fixture = Join-Path $root ([Guid]::NewGuid().ToString('N') + '.json')
    @{ github = $case.github } | ConvertTo-Json | Set-Content -LiteralPath $fixture -Encoding utf8
    $result = & (Join-Path $PSScriptRoot 'resolve-adapter-version.ps1') -Channel $case.channel -Bump $case.bump -VersionsFile $fixture | ConvertFrom-Json
    if ($result.version -cne $case.expected -or $result.tag -cne "v$($case.expected)") { throw 'Provider version resolution differs from the CfSharp policy.' }
}
foreach ($value in @('01.2.3', '1.2.3-preview.0', '1.2.3-preview.01', '1.2.3-preview.2147483648',
        '2147483648.2.3', '1.2.3.4-preview.1', '1.2.3;throw 1', '../1.2.3', "1.2.3`n")) {
    $rejected = $false
    try { $null = Get-AdapterReleaseVersion $value } catch { $rejected = $true }
    if (-not $rejected) { throw 'An unsafe Adapter version was accepted.' }
}
foreach ($value in @('1.2.3', '1.2.3.0', '1.2.3-preview.10')) {
    if ((Get-AdapterReleaseVersion $value) -cne $value) { throw 'An existing package identity was normalized.' }
}
Write-Host 'Provider version increments, numeric previews, legacy identity, and hostile input checks passed.'
