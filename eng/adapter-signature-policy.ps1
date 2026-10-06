Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'release-policy.ps1')

function Get-AdapterSignatureCanonical {
    param([Parameter(Mandatory)][object[]]$Inventory)
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $Inventory) {
        Assert-AdapterPackagePath $file.path
        if ($file.length -lt 0 -or $file.sha256 -cnotmatch '\A[0-9A-F]{64}\z' -or $entries.ContainsKey($file.path)) {
            throw 'The signature inventory is invalid.'
        }
        $entries.Add($file.path, $file)
    }
    $paths = [string[]]@($entries.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $builder = [Text.StringBuilder]::new('[')
    $separator = ''
    foreach ($path in $paths) {
        $file = $entries[$path]
        # Match the product UTF-8 JSON encoder, ordinal order and exact field order.
        $encoded = [Text.Json.JsonSerializer]::Serialize($path, [string], [Text.Json.JsonSerializerOptions]::Default)
        $null = $builder.Append($separator).Append('{"path":').Append($encoded).Append(',"length":')
        $null = $builder.Append(([long]$file.length).ToString([Globalization.CultureInfo]::InvariantCulture))
        $null = $builder.Append(',"sha256":"').Append($file.sha256).Append('"}')
        $separator = ','
    }
    return $builder.Append(']').ToString()
}
