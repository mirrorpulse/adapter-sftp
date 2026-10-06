function Get-MergedReleaseClassification {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$SourceSha,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$PullRequests
    )

    if ($Repository -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z' -or
        $SourceSha -cnotmatch '\A[0-9a-f]{40}\z') {
        throw 'Invalid stable release repository or source identity.'
    }
    $matches = @($PullRequests | Where-Object {
        $_.state -ceq 'closed' -and $_.merged_at -and
        $_.merge_commit_sha -ceq $SourceSha -and
        $_.base.ref -ceq 'main' -and $_.head.ref -ceq 'develop' -and
        [string]::Equals($_.base.repo.full_name, $Repository, [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($_.head.repo.full_name, $Repository, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($matches.Count -ne 1) {
        throw 'Stable release publication requires exactly one merged same-repository develop PR for this main commit.'
    }
    $labels = @($matches[0].labels | ForEach-Object { $_.name } | Where-Object {
        $_ -cin @('breaking', 'feature', 'fix')
    })
    if ($labels.Count -ne 1) {
        throw 'Stable release publication requires exactly one release classification.'
    }
    [pscustomobject]@{pullRequest = $matches[0].number; bump = $labels[0]; sourceSha = $SourceSha}
}
