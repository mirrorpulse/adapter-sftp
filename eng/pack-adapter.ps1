[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$version = Resolve-AdapterReleaseVersion
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-settings.json') -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$manifest.version = $version
if ($manifest.adapterId -cnotmatch '^[a-z0-9]+(\.[a-z0-9-]+)+$') { throw 'The Adapter ID is invalid.' }
$root = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $root 'payload'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
& dotnet restore $settings.workerProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Worker locked restore failed.' }
foreach ($runtime in @('win-x64', 'win-arm64')) {
    $output = Join-Path $root "publish/$runtime"
    & dotnet publish $settings.workerProject --configuration Release --runtime $runtime --self-contained false --output $output --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
    $destination = Join-Path $packageRoot "worker/$runtime"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $output | Copy-Item -Destination $destination -Recurse -Force
    $workers = @(Get-ChildItem -LiteralPath $destination -Filter '*.exe' -File)
    if ($workers.Count -ne 1) { throw 'Exactly one Worker apphost must be produced.' }
    Rename-Item -LiteralPath $workers[0].FullName -NewName 'MirrorPulse.Adapter.Worker.exe'
}
New-Item -ItemType Directory -Path (Join-Path $packageRoot 'locales') -Force | Out-Null
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Encoding utf8
@{ displayName = $settings.label } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'locales/en-US.json') -Encoding utf8
$packagePath = Join-Path $root "$($manifest.adapterId)-$version.mpadapter"
$stream = [IO.File]::Open($packagePath, [IO.FileMode]::CreateNew)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName).Replace('\', '/')
        Assert-AdapterPackagePath $relative
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $input = [IO.File]::OpenRead($file.FullName)
        $output = $entry.Open()
        try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
    }
} finally { $archive.Dispose(); $stream.Dispose() }
if ($env:GITHUB_OUTPUT) {
    "version=$version" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "package=$packagePath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
return $packagePath
