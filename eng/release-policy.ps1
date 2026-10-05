Set-StrictMode -Version Latest

function Get-AdapterReleaseVersion {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 32 -or $Value -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))?$') {
        throw 'The Adapter version must be a canonical numeric version with three or four components.'
    }
    $parsed = $null
    if (-not [Version]::TryParse($Value, [ref]$parsed)) { throw 'The Adapter version is outside the supported range.' }
    return $Value
}

function Resolve-AdapterReleaseVersion {
    if ($env:MP_RELEASE_EVENT -eq 'push') {
        $tag = $env:MP_RELEASE_REF
        if ([string]::IsNullOrEmpty($tag) -or -not $tag.StartsWith('v', [StringComparison]::Ordinal)) { throw 'The release tag is invalid.' }
        return Get-AdapterReleaseVersion $tag.Substring(1)
    }
    return Get-AdapterReleaseVersion ([string]$env:MP_RELEASE_VERSION)
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
