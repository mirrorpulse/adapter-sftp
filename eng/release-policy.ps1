Set-StrictMode -Version Latest

function Get-AdapterReleaseVersion {
    param([AllowNull()][AllowEmptyString()][string]$Value)
    if (-not $Value -or $Value.Length -gt 64 -or
        $Value -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)|-preview\.([1-9][0-9]*))?\z') {
        throw 'The Adapter version must be canonical X.Y.Z, X.Y.Z-preview.N, or a preserved legacy four-part identity.'
    }
    $preview = $Matches[5]
    $parsed = $null
    if (-not [Version]::TryParse(($Value -split '-preview\.')[0], [ref]$parsed)) { throw 'The Adapter version is outside the supported range.' }
    if ($preview) {
        $ordinal = 0
        if (-not [int]::TryParse($preview, [ref]$ordinal)) { throw 'The Adapter preview counter is outside the supported range.' }
    }
    return $Value
}

function Resolve-AdapterReleaseVersion {
    return Get-AdapterReleaseVersion ([string]$env:MP_RELEASE_VERSION)
}

function Assert-AdapterPackageIdentity {
    param([Parameter(Mandatory)][string]$PackagePath, [string]$ExpectedVersion)
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.GetEntry('manifest.json')
        if ($null -eq $entry -or $entry.Length -gt 1MB) { throw 'The Adapter manifest is missing or too large.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        $version = Get-AdapterReleaseVersion $manifest.version
        if ($manifest.adapterId -cnotmatch '\A[a-z0-9]+(\.[a-z0-9-]+)+\z' -or
            [IO.Path]::GetFileName($PackagePath) -cne "$($manifest.adapterId)-$version.mpadapter" -or
            ($ExpectedVersion -and $version -cne (Get-AdapterReleaseVersion $ExpectedVersion))) {
            throw 'The package filename, manifest, and release version must have the same canonical identity.'
        }
    } finally { $archive.Dispose() }
}

function Assert-AdapterPackagePath {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path.Length -gt 1024 -or $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.Contains('~')) { throw 'The package path is not canonical.' }
    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment.Length -gt 255 -or $segment.Trim() -cne $segment -or
            $segment.EndsWith('.') -or $segment -in @('.', '..') -or $segment -match '[\x00-\x1f\x7f<>:"|?*]' -or
            $segment.Split('.')[0] -match '^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³])$' -or
            $segment.Normalize() -cne $segment) { throw 'The package path is not canonical.' }
    }
}
