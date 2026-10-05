[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath, [switch]$DryRun)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-policy.ps1')
$stream = [IO.File]::Open($PackagePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Update)
$certificate = $null
$rsa = $null
$pfx = $null
try {
    if ($archive.GetEntry('META-INF/mirrorpulse/signature.json')) { throw 'The package is already signed.' }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $inventory = @(
        foreach ($entry in $archive.Entries) {
            Assert-AdapterPackagePath $entry.FullName
            if (-not $names.Add($entry.FullName)) { throw 'The package has aliased entries.' }
            $length = [long]$entry.Length
            $input = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input)) } finally { $input.Dispose() }
            [ordered]@{ path = $entry.FullName; length = $length; sha256 = $hash }
        }
    ) | Sort-Object { $_['path'] } -CaseSensitive
    $canonical = ConvertTo-Json -InputObject @($inventory) -Compress -Depth 5
    if ($DryRun) {
        $key = [Security.Cryptography.RSA]::Create(3072)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=MirrorPulse Dry Run', $key,
                [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
        } finally { $key.Dispose() }
    } else {
        if ([string]::IsNullOrWhiteSpace($env:MIRRORPULSE_ADAPTER_SIGNING_PFX_BASE64) -or
            [string]::IsNullOrWhiteSpace($env:MIRRORPULSE_ADAPTER_SIGNING_PFX_PASSWORD)) { throw 'Adapter signing secrets are not configured.' }
        try {
            $pfx = [Convert]::FromBase64String($env:MIRRORPULSE_ADAPTER_SIGNING_PFX_BASE64)
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx,
                $env:MIRRORPULSE_ADAPTER_SIGNING_PFX_PASSWORD, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        } catch { throw 'The configured signing certificate could not be loaded.' }
    }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    if ($null -eq $rsa) { throw 'The signing certificate has no RSA key.' }
    $signature = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($canonical),
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
    $signer = if ($DryRun) { 'MirrorPulse Dry Run' } else { 'MirrorPulse Team' }
    $envelope = [ordered]@{ algorithm = 'RSA-SHA256'; signer = $signer; signature = $signature; files = @($inventory) } | ConvertTo-Json -Depth 8
    $entry = $archive.CreateEntry('META-INF/mirrorpulse/signature.json', [IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($envelope) } finally { $writer.Dispose() }
    $envelope | Set-Content -LiteralPath "$PackagePath.signature.json" -Encoding utf8
    $rsa.ExportSubjectPublicKeyInfoPem() | Set-Content -LiteralPath "$PackagePath.public.pem" -Encoding utf8
} finally {
    if ($rsa) { $rsa.Dispose() }
    if ($certificate) { $certificate.Dispose() }
    if ($pfx) { [Array]::Clear($pfx) }
    $archive.Dispose()
    $stream.Dispose()
}
& (Join-Path $PSScriptRoot 'verify-adapter-signature.ps1') -PackagePath $PackagePath
if ($env:GITHUB_OUTPUT) { "package=$PackagePath" | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8 }
