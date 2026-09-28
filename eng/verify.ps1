[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$project = 'src/MirrorPulse.Adapter.Sftp.Worker/MirrorPulse.Adapter.Sftp.Worker.csproj'
& dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Worker restore failed." }
foreach ($rid in @("win-x64", "win-arm64")) {
    & dotnet build $project --configuration Release --runtime $rid --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Worker build failed for $rid." }
}
