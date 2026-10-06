[CmdletBinding()]
param([string]$OutputDirectory = 'artifacts/release')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
. (Join-Path $PSScriptRoot 'dependency-notices.ps1')
$version = Resolve-AdapterReleaseVersion
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-settings.json') -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$manifest.version = $version
if ($manifest.adapterId -cnotmatch '^[a-z0-9]+(\.[a-z0-9-]+)+$') { throw 'The Adapter ID is invalid.' }
$root = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $root 'payload'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../LICENSE') -Destination (Join-Path $packageRoot 'LICENSE')
& dotnet restore $settings.workerProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Worker locked restore failed.' }
foreach ($runtime in @('win-x64', 'win-arm64')) {
    $output = Join-Path $root "publish/$runtime"
    & dotnet publish $settings.workerProject --configuration Release --runtime $runtime --self-contained true --output $output --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
    $destination = Join-Path $packageRoot "worker/$runtime"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $output | Copy-Item -Destination $destination -Recurse -Force
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($settings.workerProject)
    $apphost = Join-Path $destination ($assemblyName + '.exe')
    if (-not (Test-Path -LiteralPath $apphost)) { throw 'The configured Worker apphost was not produced.' }
    Rename-Item -LiteralPath $apphost -NewName 'MirrorPulse.Adapter.Worker.exe'
    foreach ($dependency in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $dependency))) { throw 'The Worker runtime payload is incomplete.' }
    }
    $runtimeConfiguration = Get-Content -LiteralPath (Join-Path $destination ($assemblyName + '.runtimeconfig.json')) -Raw | ConvertFrom-Json
    $framework = @($runtimeConfiguration.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App')
    if ($framework.Count -ne 1 -or $framework[0].version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z' -or
        $null -ne $runtimeConfiguration.runtimeOptions.PSObject.Properties['framework'] -or
        $null -ne $runtimeConfiguration.runtimeOptions.PSObject.Properties['frameworks']) {
        throw 'The Worker must use its private runtime without a shared framework requirement.'
    }
    $assetsPath = Join-Path (Split-Path $settings.workerProject) 'obj/project.assets.json'
    $restored = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $runtimePacks = @($restored.packageFolders.PSObject.Properties.Name | ForEach-Object {
        $path = Join-Path $_ "microsoft.netcore.app.runtime.$runtime/$($framework[0].version)"
        if (Test-Path -LiteralPath $path) { $path }
    })
    if ($runtimePacks.Count -ne 1) { throw 'Exactly one restored runtime license source is required.' }
    $licenses = Join-Path $packageRoot 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        Copy-Item -LiteralPath (Join-Path $runtimePacks[0] $notice) -Destination (Join-Path $licenses "dotnet-$runtime-$($framework[0].version)-$notice")
    }
}
Copy-AdapterDependencyNotices -AssetsPath $assetsPath -PackageRoot $packageRoot
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
