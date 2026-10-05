. (Join-Path $PSScriptRoot 'release-source-policy.ps1')

function Get-AdapterPublicationRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Event,
        [Parameter(Mandatory)][string]$Ref,
        [Parameter(Mandatory)][string]$SourceSha,
        [Parameter(Mandatory)][string]$EventSha,
        [Parameter(Mandatory)][string]$Repository,
        [string]$Channel,
        [bool]$Publish,
        [string]$Confirm
    )
    if ($SourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or $SourceSha -cne $EventSha -or
        $Repository -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') { throw 'Publication requires the exact checked-out event source.' }
    if ($Event -ceq 'push') {
        if ($Ref -cne 'refs/heads/main') { throw 'Only classified main pushes prepare stable publication.' }
        return [pscustomobject]@{ Channel = 'Stable'; Publish = $true }
    }
    if ($Event -cne 'workflow_dispatch' -or $Channel -cnotin @('Stable', 'Preview') -or
        ($Channel -ceq 'Preview' -and $Ref -cne 'refs/heads/develop') -or
        ($Channel -ceq 'Stable' -and $Ref -cne 'refs/heads/main')) { throw 'Publication channel does not match a trusted event and branch.' }
    if ($Publish -and $Confirm -cne 'PUBLISH') { throw 'Publication requires the exact PUBLISH confirmation.' }
    if ($Publish -and $Channel -ceq 'Stable') { throw 'New stable versions require a classified develop PR merged into main.' }
    [pscustomobject]@{ Channel = $Channel; Publish = $Publish }
}

function Get-AdapterReleaseCreateArguments {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][ValidateSet('stable', 'preview')][string]$Channel,
        [Parameter(Mandatory)][string[]]$Assets)
    $arguments = @('release', 'create', $Tag) + $Assets + @('--repo', $Repository, '--verify-tag',
        '--title', "Adapter $Version", '--notes',
        'Signed process Adapter with native x64 and ARM64 conformance, production installation verification, and a fixed SHA256 inventory.')
    if ($Channel -ceq 'preview') { $arguments += @('--prerelease', '--latest=false') }
    else { $arguments += @('--latest=true') }
    $arguments
}
