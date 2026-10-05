$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'adapter-publication-policy.ps1')
$source = & git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the checked-out Provider source.' }
$request = Get-AdapterPublicationRequest -Event $env:MP_RELEASE_EVENT -Ref $env:GITHUB_REF -SourceSha $source `
    -EventSha $env:GITHUB_SHA -Repository $env:GITHUB_REPOSITORY -Channel $env:MP_RELEASE_CHANNEL `
    -Publish ($env:MP_RELEASE_PUBLISH -ceq 'true') -Confirm $env:MP_RELEASE_CONFIRM
$bump = 'Fix'
$pullRequest = $null
if ($env:MP_RELEASE_EVENT -ceq 'push') {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { throw 'Stable source classification requires a read-only GitHub token.' }
    $headers = @{ Authorization = 'Bearer ' + $env:GITHUB_TOKEN; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    try {
        $response = Invoke-RestMethod -Uri "https://api.github.com/repos/$env:GITHUB_REPOSITORY/commits/$source/pulls?per_page=100" -Headers $headers
        $requests = @($response | ForEach-Object { $_ })
        if ($requests.Count -ge 100) { throw 'Commit PR history exceeds the bounded query.' }
        $classification = Get-MergedReleaseClassification -Repository $env:GITHUB_REPOSITORY -SourceSha $source -PullRequests $requests
        $bump = $classification.bump
        $pullRequest = $classification.pullRequest
    } finally { $headers.Clear() }
}
$version = & (Join-Path $PSScriptRoot 'resolve-adapter-version.ps1') -Channel $request.Channel -Bump $bump | ConvertFrom-Json
$result = [ordered]@{
    schemaVersion = 1; repository = $env:GITHUB_REPOSITORY; sourceSha = $source
    version = $version.version; tag = $version.tag; channel = $request.Channel.ToLowerInvariant()
    publish = $request.Publish; bump = $bump.ToLowerInvariant(); pullRequest = $pullRequest
}
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath artifacts/provider-version.json -Encoding utf8
foreach ($entry in @("version=$($result.version)", "source_sha=$source", "channel=$($result.channel)", "publish=$($request.Publish)".ToLowerInvariant())) {
    $entry | Out-File -LiteralPath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
