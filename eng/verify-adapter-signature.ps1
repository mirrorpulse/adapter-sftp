[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$rsa = [Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem((Get-Content -LiteralPath "$PackagePath.public.pem" -Raw))
$archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $entry = $archive.GetEntry('META-INF/mirrorpulse/signature.json')
    if ($null -eq $entry) { throw 'The embedded signature is missing.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $envelope = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($envelope.algorithm -cne 'RSA-SHA256') { throw 'The signing algorithm is unsupported.' }
    $inventory = @($envelope.files | ForEach-Object { [ordered]@{ path = $_.path; length = [long]$_.length; sha256 = $_.sha256 } })
    $canonical = ConvertTo-Json -InputObject $inventory -Compress -Depth 5
    if (-not $rsa.VerifyData([Text.Encoding]::UTF8.GetBytes($canonical), [Convert]::FromBase64String($envelope.signature),
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'The package signature is invalid.' }
    if ($archive.Entries.Count -ne $inventory.Count + 1) { throw 'The package inventory is incomplete.' }
    foreach ($file in $inventory) {
        Assert-AdapterPackagePath $file.path
        $entry = $archive.GetEntry($file.path)
        if ($null -eq $entry -or $entry.Length -ne $file.length) { throw 'The payload length does not match its signed inventory.' }
        $input = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)) } finally { $input.Dispose() }
        if ($hash -cne $file.sha256) { throw 'The payload hash does not match its signed inventory.' }
    }
    foreach ($runtime in @('win-x64', 'win-arm64')) {
        if ($null -eq $archive.GetEntry("worker/$runtime/MirrorPulse.Adapter.Worker.exe")) { throw 'The dual-RID Worker payload is incomplete.' }
    }
} finally { $archive.Dispose(); $rsa.Dispose() }
Write-Host 'The embedded signature, payload inventory and dual-RID package are valid.'
