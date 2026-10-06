[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw 'A release script has invalid PowerShell syntax.' }
}
$workflow = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/workflows/release.yml') -Raw
foreach ($block in [regex]::Matches($workflow, '(?ms)^\s{6,}- name:.*?(?=^\s{6,}- |^\s{2}[a-z]+:|\z)')) {
    $run = [regex]::Match($block.Value, '(?ms)^\s+run:.*')
    if ($run.Success -and $run.Value.Contains('${{')) { throw 'GitHub expressions must not enter executable script text.' }
}
foreach ($action in [regex]::Matches($workflow, 'uses:\s+actions/[^@\s]+@([^\s]+)')) {
    if ($action.Groups[1].Value -cnotmatch '^[0-9a-f]{40}$') { throw 'Every Action must use a full commit SHA.' }
}
foreach ($value in @('1.0.0; Write-Output injected', "1.0.0`nWrite-Output injected", '$(Write-Output injected)', "1.0.0'", '../1.0.0', '01.0.0')) {
    $rejected = $false
    try { $null = Get-AdapterReleaseVersion $value } catch { $rejected = $true }
    if (-not $rejected) { throw 'An unsafe release version was accepted.' }
    $previousValue = $env:MP_RELEASE_VERSION
    $previousEventName = $env:MP_RELEASE_EVENT
    try {
        $env:MP_RELEASE_EVENT = 'workflow_dispatch'
        $env:MP_RELEASE_VERSION = $value
        $rejected = $false
        try { $null = & (Join-Path $PSScriptRoot 'pack-adapter.ps1') } catch { $rejected = $true }
        if (-not $rejected) { throw 'The pack entry point accepted an unsafe release input.' }
    } finally { $env:MP_RELEASE_VERSION = $previousValue; $env:MP_RELEASE_EVENT = $previousEventName }
}
if ((Get-AdapterReleaseVersion '1.2.3') -cne '1.2.3') { throw 'The valid release version changed.' }
$previous = $env:MP_RELEASE_VERSION
$previousEvent = $env:MP_RELEASE_EVENT
try {
    $env:MP_RELEASE_EVENT = 'workflow_dispatch'
    $env:MP_RELEASE_VERSION = '0.1.0'
    $output = @(& (Join-Path $PSScriptRoot 'pack-adapter.ps1'))
    $package = [string]$output[-1]
    & (Join-Path $PSScriptRoot 'sign-adapter.ps1') -PackagePath $package -DryRun
    $native = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
    $unpacked = Join-Path (Split-Path $package) 'verified-signed-payload'
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $unpacked)
    & dotnet restore tools/MirrorPulse.Adapter.Sftp.Conformance/MirrorPulse.Adapter.Sftp.Conformance.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'SFTP native conformance locked restore failed.' }
    & dotnet run --project tools/MirrorPulse.Adapter.Sftp.Conformance -c Release --no-restore -- --worker (Join-Path $unpacked "worker/$native/MirrorPulse.Adapter.Worker.exe")
    if ($LASTEXITCODE -ne 0) { throw 'The actual signed self-contained SFTP Worker failed conformance.' }
    if ((Get-Item -LiteralPath $package).Length -gt 256MB) { throw 'The dual-RID package exceeds its size boundary.' }
    [ordered]@{ schemaVersion = 1; runtime = $native; version = '0.1.0'; protocolVersion = 2;
        packageLength = (Get-Item -LiteralPath $package).Length;
        packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant();
        privateRuntimeVerified = $true; conformanceCasesPassed = 15; signing = 'disposable'; published = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path $package) 'native-conformance.json') -Encoding utf8
} finally { $env:MP_RELEASE_VERSION = $previous; $env:MP_RELEASE_EVENT = $previousEvent }
