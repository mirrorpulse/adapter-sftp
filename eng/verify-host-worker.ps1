[CmdletBinding()]
param([Parameter(Mandatory)][string]$MirrorPulseRoot, [Parameter(Mandatory)][string]$CandidateDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'The signed candidate protocol gate requires a disposable GitHub Actions runner.' }
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$mp = (Resolve-Path -LiteralPath $MirrorPulseRoot).Path
$candidates = @(Get-ChildItem -LiteralPath $CandidateDirectory -File -Filter '*.mpadapter')
if ($candidates.Count -ne 1) { throw 'Exactly one signed candidate is required.' }
$candidate = $candidates[0]
$archive = [IO.Compression.ZipFile]::OpenRead($candidate.FullName)
try {
    $entry = $archive.GetEntry('manifest.json')
    if ($null -eq $entry -or $entry.Length -gt 1024 * 1024) { throw 'The candidate manifest is invalid.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
} finally { $archive.Dispose() }
$id = $manifest.adapterId
if ($id -cnotmatch '^com\.mirrorpulse\.adapter\.(local|webdav|smb|ftp|sftp)$') { throw 'The candidate is not an official Adapter.' }
$version = Get-AdapterReleaseVersion $manifest.version
$aggregate = [IO.Path]::GetFullPath((Join-Path $mp 'artifacts/candidate-adapters'))
if (-not $aggregate.StartsWith($mp.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The candidate gate output is outside its checkout.' }
$share = 'MirrorPulseCandidate' + [Guid]::NewGuid().ToString('N')
$createdShare = $false
Push-Location $mp
try {
    & dotnet restore tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'The product verifier locked restore failed.' }
    & ./eng/aggregate-official-adapters.ps1 -OutputDirectory $aggregate
    $packageRoot = Join-Path $aggregate $id
    Copy-Item -LiteralPath $candidate.FullName -Destination $packageRoot
    Copy-Item -LiteralPath ($candidate.FullName + '.signature.json') -Destination $packageRoot
    $aggregateManifest = Join-Path $aggregate 'official-adapters.manifest.json'
    $records = @(Get-Content -LiteralPath $aggregateManifest -Raw | ConvertFrom-Json)
    $record = @($records | Where-Object adapterId -eq $id)
    if ($record.Count -ne 1) { throw 'The candidate has no aggregation entry.' }
    $record[0].version = $version
    $record[0].tag = "candidate-$version"
    $record[0].releaseUrl = "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
    $record[0].package = $candidate.Name
    $record[0].signature = $candidate.Name + '.signature.json'
    $record[0].packageLength = $candidate.Length
    $record[0].packageSha256 = (Get-FileHash -LiteralPath $candidate.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    ConvertTo-Json -InputObject $records -Depth 10 | Set-Content -LiteralPath $aggregateManifest -Encoding utf8
    $previous = Join-Path $mp 'artifacts/local-previous'
    New-Item -ItemType Directory -Path $previous -Force | Out-Null
    & gh release download v0.1.3 --repo mirrorpulse/adapter-local --dir $previous --pattern 'com.mirrorpulse.adapter.local-0.1.3.mpadapter*'
    if ($LASTEXITCODE -ne 0) { throw 'The signed previous Local release could not be downloaded.' }
    & ./eng/setup-test-environment.ps1
    $backing = Join-Path $mp 'artifacts/smb-candidate-backing'
    New-Item -ItemType Directory -Path $backing -Force | Out-Null
    New-SmbShare -Name $share -Path $backing -FullAccess $env:USERNAME -ErrorAction Stop | Out-Null
    $createdShare = $true
    $env:MIRRORPULSE_SMB_TEST_SHARE = "\\localhost\$share"
    $env:MIRRORPULSE_SMB_TEST_BACKING = $backing
    $env:MIRRORPULSE_OFFICIAL_AGGREGATE = $aggregate
    $env:MIRRORPULSE_LOCAL_PREVIOUS_RELEASE = $previous
    & dotnet test tests/MirrorPulse.Core.Tests/MirrorPulse.Core.Tests.csproj --configuration Release --no-restore --filter TestCategory=OfficialPackages --logger trx --results-directory artifacts/test-results/candidate
    if ($LASTEXITCODE -ne 0) { throw 'The signed candidate Host/Worker protocol gate failed.' }
    & ./eng/verify-test-results.ps1 -Suite official -ResultsDirectory artifacts/test-results/candidate
    $mpSha = (& git rev-parse HEAD | Out-String).Trim()
    [ordered]@{
        schemaVersion = 1; adapterId = $id; version = $version; adapterSourceSha = $env:GITHUB_SHA;
        mirrorPulseSourceSha = $mpSha; packageSha256 = $record[0].packageSha256; packageLength = $candidate.Length;
        runId = $env:GITHUB_RUN_ID; protocolGate = 'official'; published = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $mp 'artifacts/candidate-evidence.json') -Encoding utf8
} finally {
    if ($createdShare) { Remove-SmbShare -Name $share -Force }
    Pop-Location
}
