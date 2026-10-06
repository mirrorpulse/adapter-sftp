[CmdletBinding()]
param([string]$AssetDirectory, [string]$SourceSha)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'adapter-publication-policy.ps1')
. (Join-Path $PSScriptRoot 'adapter-signature-policy.ps1')
$inventory = @(foreach ($path in @('worker/a.dll', 'worker/B.dll', 'worker/A.dll', 'locales/é&.txt')) {
    [ordered]@{ path = $path; length = 7L; sha256 = ('A' * 64) }
})
$canonical = Get-AdapterSignatureCanonical -Inventory $inventory
$expected = '[{"path":"locales/\u00E9\u0026.txt","length":7,"sha256":"' + ('A' * 64) + '"},' +
    '{"path":"worker/A.dll","length":7,"sha256":"' + ('A' * 64) + '"},' +
    '{"path":"worker/B.dll","length":7,"sha256":"' + ('A' * 64) + '"},' +
    '{"path":"worker/a.dll","length":7,"sha256":"' + ('A' * 64) + '"}]'
if ($canonical -cne $expected) { throw 'Signature canonicalization differs from the product ordinal JSON contract.' }
$previousCulture = [Globalization.CultureInfo]::CurrentCulture
try {
    foreach ($name in @('en-US', 'tr-TR', 'fr-FR')) {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($name)
        if ((Get-AdapterSignatureCanonical -Inventory $inventory) -cne $expected) { throw 'The signature depends on machine culture.' }
    }
} finally { [Globalization.CultureInfo]::CurrentCulture = $previousCulture }
foreach ($channel in @('stable', 'preview')) {
    $arguments = @(Get-AdapterReleaseCreateArguments -Repository 'MirrorPulse/adapter-template' -Tag 'v1.2.3' -Version '1.2.3' -Channel $channel -Assets @('C:\candidate with spaces\adapter.mpadapter'))
    if ($arguments -contains '-' -or $arguments[0] -cne 'release' -or $arguments[1] -cne 'create' -or
        $arguments[3] -cne 'C:\candidate with spaces\adapter.mpadapter' -or @($arguments | Where-Object { $_.StartsWith('--latest=') }).Count -ne 1) {
        throw 'The native release argument array was split or changed.'
    }
    if ($channel -ceq 'stable' -and ($arguments[-1] -cne '--latest=true' -or $arguments -contains '--prerelease')) { throw 'Stable release options were corrupted.' }
    if ($channel -ceq 'preview' -and ($arguments[-1] -cne '--latest=false' -or $arguments[-2] -cne '--prerelease')) { throw 'Preview release options were corrupted.' }
}
$identity = @{ SourceSha = ('a' * 40); EventSha = ('a' * 40); Repository = 'MirrorPulse/adapter-template' }
foreach ($case in @(
    @{ event = 'push'; ref = 'refs/heads/main'; channel = ''; publish = $false; confirm = ''; expected = $true; expectedChannel = 'Stable' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/develop'; channel = 'Preview'; publish = $true; confirm = 'PUBLISH'; expected = $true; expectedChannel = 'Preview' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/develop'; channel = 'Preview'; publish = $false; confirm = ''; expected = $false; expectedChannel = 'Preview' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/main'; channel = 'Stable'; publish = $false; confirm = ''; expected = $false; expectedChannel = 'Stable' }
)) {
    $result = Get-AdapterPublicationRequest @identity -Event $case.event -Ref $case.ref -Channel $case.channel -Publish $case.publish -Confirm $case.confirm
    if ($result.Publish -ne $case.expected -or $result.Channel -cne $case.expectedChannel) { throw 'A valid Provider publication request was misclassified.' }
}
foreach ($case in @(
    @{ event = 'push'; ref = 'refs/tags/v1.0.0'; channel = 'Stable'; publish = $true; confirm = 'PUBLISH' },
    @{ event = 'push'; ref = 'refs/heads/develop'; channel = 'Stable'; publish = $true; confirm = 'PUBLISH' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/main'; channel = 'Preview'; publish = $true; confirm = 'PUBLISH' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/develop'; channel = 'Stable'; publish = $true; confirm = 'PUBLISH' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/main'; channel = 'Stable'; publish = $true; confirm = 'PUBLISH' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/develop'; channel = 'Preview'; publish = $true; confirm = '' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/develop'; channel = 'Preview'; publish = $true; confirm = 'publish' },
    @{ event = 'workflow_dispatch'; ref = 'refs/heads/develop'; channel = 'preview'; publish = $true; confirm = 'PUBLISH' },
    @{ event = 'pull_request_target'; ref = 'refs/heads/main'; channel = 'Stable'; publish = $true; confirm = 'PUBLISH' }
)) {
    $rejected = $false
    try { $null = Get-AdapterPublicationRequest @identity -Event $case.event -Ref $case.ref -Channel $case.channel -Publish $case.publish -Confirm $case.confirm } catch { $rejected = $true }
    if (-not $rejected) { throw 'A tag, branch, channel, event, or confirmation bypass was accepted.' }
}
foreach ($case in @(
    @{ SourceSha = ('b' * 40); EventSha = ('a' * 40); Repository = 'MirrorPulse/adapter-template' },
    @{ SourceSha = 'not-a-commit'; EventSha = 'not-a-commit'; Repository = 'MirrorPulse/adapter-template' },
    @{ SourceSha = ('a' * 40); EventSha = ('a' * 40); Repository = "MirrorPulse/adapter-template`n" }
)) {
    $rejected = $false
    try { $null = Get-AdapterPublicationRequest @case -Event 'push' -Ref 'refs/heads/main' } catch { $rejected = $true }
    if (-not $rejected) { throw 'An ambiguous publication source was accepted.' }
}
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'A Provider release script has invalid PowerShell syntax.' }
}
$workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/release.yml') -Raw
foreach ($action in [regex]::Matches($workflow, 'uses:\s+[^@\s]+@([^\s]+)')) {
    if ($action.Groups[1].Value -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Provider release Actions require immutable commit SHAs.' }
}
foreach ($run in [regex]::Matches($workflow, '(?ms)^\s{8}run:.*?(?=^\s{6}- |^\s{2}\S|\z)')) {
    if ($run.Value.Contains('${{')) { throw 'Workflow inputs must enter executable scripts as environment data.' }
}
if ($AssetDirectory) {
    & (Join-Path $PSScriptRoot 'publish-adapter-release.ps1') -AssetDirectory $AssetDirectory -SourceSha $SourceSha -DryRun
    $root = Join-Path ([IO.Path]::GetFullPath('artifacts/provider-rejection')) ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    Get-ChildItem -LiteralPath $AssetDirectory -File | Copy-Item -Destination $root
    $manifest = Get-Content -LiteralPath (Join-Path $root 'provider-release.json') -Raw | ConvertFrom-Json
    $package = Join-Path $root (@($manifest.files | Where-Object { $_.name.EndsWith('.mpadapter') })[0].name)
    $bytes = [IO.File]::ReadAllBytes($package)
    $bytes[0] = $bytes[0] -bxor 1
    [IO.File]::WriteAllBytes($package, $bytes)
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'publish-adapter-release.ps1') -AssetDirectory $root -SourceSha $SourceSha -DryRun } catch { $rejected = $true }
    if (-not $rejected) { throw 'A modified release candidate was accepted.' }
}
Write-Host 'Provider source/channel checks passed: 4 accepted requests, 12 rejected bypasses, and immutable Action references.'
