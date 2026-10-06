[CmdletBinding()]
param([Parameter(Mandatory)][string]$AssetDirectory, [Parameter(Mandatory)][string]$SourceSha,
    [Parameter(Mandatory)][string]$ProductRepositoryPath)
$ErrorActionPreference = 'Stop'
$assets = [IO.Path]::GetFullPath($AssetDirectory)
$product = [IO.Path]::GetFullPath($ProductRepositoryPath)
& (Join-Path $PSScriptRoot 'verify-adapter-release-assets.ps1') -AssetDirectory $assets -SourceSha $SourceSha
$metadata = Get-Content -LiteralPath (Join-Path $assets 'provider-release.json') -Raw | ConvertFrom-Json
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restore-adapter-sdk.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Fixed SDK verification failed.' }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restore-sdk-conformance.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Fixed SDK conformance verification failed.' }
$packageRecord = @($metadata.files | Where-Object { $_.name.EndsWith('.mpadapter', [StringComparison]::Ordinal) })[0]
$package = Join-Path $assets $packageRecord.name
if ($packageRecord.length -gt 256MB) { throw 'The dual-RID package exceeds its size boundary.' }
$runtime = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }
$unpacked = Join-Path ([IO.Path]::GetFullPath('artifacts/signed-process')) ([Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($package, $unpacked)
$project = 'tools/MirrorPulse.Adapter.Sftp.Conformance/MirrorPulse.Adapter.Sftp.Conformance.csproj'
& dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'SFTP native conformance locked restore failed.' }
& dotnet run --project $project -c Release --no-restore -- --worker (Join-Path $unpacked "worker/$runtime/MirrorPulse.Adapter.Worker.exe")
if ($LASTEXITCODE -ne 0) { throw 'The exact signed SFTP Worker failed its real SFTP share profile.' }
$names = @('MP_SFTP_V2_PACKAGE', 'MP_SFTP_V2_PUBLIC_KEY', 'MP_SFTP_V2_OFFICIAL_SIGNED')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:MP_SFTP_V2_PACKAGE = $package
    $env:MP_SFTP_V2_PUBLIC_KEY = $package + '.public.pem'
    $env:MP_SFTP_V2_OFFICIAL_SIGNED = ([bool]$metadata.publish).ToString().ToLowerInvariant()
    Push-Location $product
    try {
        $productSha = (& git rev-parse HEAD | Out-String).Trim()
        if ($productSha -cne '7f73b90f197f1505839c79c3753965e1e63e95cc') { throw 'The product verifier source differs from its fixed contract.' }
        & pwsh -NoProfile -File eng/restore-adapter-sdk.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Pinned product SDK verification failed.' }
        & dotnet restore tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj --locked-mode --packages artifacts/provider-process-packages
        if ($LASTEXITCODE -ne 0) { throw 'Pinned product locked restore failed.' }
        $results = Join-Path $assets "process-$runtime"
        & dotnet test tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~SignedNetworkV2WorkerProcessTests.SignedSftpV2 --logger 'trx;LogFileName=sftp-v2.trx' --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw 'Production signature/install/Host conformance failed.' }
        [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'sftp-v2.trx') -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        if ($counts.total -ne 1 -or $counts.executed -ne 1 -or $counts.passed -ne 1 -or $counts.notExecuted -ne 0) { throw 'The product profile must execute without skips.' }
        [ordered]@{ schemaVersion = 1; adapterSourceSha = $SourceSha; mirrorPulseSourceSha = $productSha;
            runtime = $runtime; sdkVersion = '0.2.1'; sdkSourceSha = 'ce74cd358de148f148ce89ffc8478f6fe1920281';
            version = $metadata.version; packageSha256 = $packageRecord.sha256; packageLength = $packageRecord.length;
            sftpConformanceCases = 24; productCases = 1; privateRuntimeVerified = $true;
            officialTrustVerified = [bool]$metadata.publish; published = $false
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'sftp-v2-evidence.json') -Encoding utf8
    } finally { Pop-Location }
} finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') } }
