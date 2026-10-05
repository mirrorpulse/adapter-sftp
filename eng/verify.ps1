[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restore-adapter-sdk.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Fixed SDK verification failed.' }
$projects = @('src/MirrorPulse.Adapter.Sftp.Worker/MirrorPulse.Adapter.Sftp.Worker.csproj',
    'tests/MirrorPulse.Adapter.Sftp.Worker.Tests/MirrorPulse.Adapter.Sftp.Worker.Tests.csproj',
    'tools/MirrorPulse.Adapter.Sftp.Conformance/MirrorPulse.Adapter.Sftp.Conformance.csproj')
foreach ($project in $projects) {
    & dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet build $project -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & dotnet format $project --no-restore --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'Formatting failed.' }
}
& dotnet test $projects[1] -c Release --no-build --no-restore --logger 'trx;LogFileName=sftp-v2.trx' --results-directory artifacts/test-results
if ($LASTEXITCODE -ne 0) { throw 'Actual SFTP conformance failed.' }
[xml]$trx = Get-Content -LiteralPath artifacts/test-results/sftp-v2.trx -Raw
$counts = $trx.TestRun.ResultSummary.Counters
if ($counts.total -ne 9 -or $counts.executed -ne 9 -or $counts.passed -ne 9 -or $counts.notExecuted -ne 0) {
    throw 'All SFTP source cases must execute without skips.'
}
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-adapter-version.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Provider version policy verification failed.' }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-adapter-publishing.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Provider publication policy verification failed.' }
