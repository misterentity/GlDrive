# Fail closed before packaging or publishing when the exact pushed source has not
# passed the repository's Windows and Core CI workflow. Native smoke remains local.
. (Join-Path $PSScriptRoot 'PublishManifest.ps1')

function Assert-ReleaseTrackedInputs([string]$Root, [string]$Commit) {
    # Reuse the publish guard's SDK-input inventory: git status alone cannot see
    # ignored source files or root MSBuild settings that still affect compilation.
    $context = Get-PublishContext $Root '' 'ci-source-inventory'
    if ($context.commit -ne $Commit) { throw 'Release commit changed while verifying build inputs.' }
    $tracked = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($name in (Invoke-PublishGit $Root 'ls-files -z --cached').Split([char]0)) {
        if ($name) { [void]$tracked.Add($name) }
    }
    foreach ($inputFile in $context.inputs) {
        if (-not $tracked.Contains($inputFile.path)) {
            throw "Release build input is not tracked by the CI-tested commit: $($inputFile.path). Commit the intended input or remove it before releasing."
        }
    }
}

function Assert-ReleaseCiResult([string]$Commit, [string]$RemoteCommit, $Response) {
    if ($Commit -notmatch '^[0-9a-f]{40}$' -or $RemoteCommit -ne $Commit) {
        throw 'Release HEAD must match the pushed origin/master commit. Commit and push the reviewed release first.'
    }
    if ($null -eq $Response -or $null -eq $Response.workflow_runs) {
        throw 'GitHub returned no usable CI evidence. Retry after checking authentication and connectivity.'
    }
    $run = @($Response.workflow_runs | Where-Object {
        $_.head_sha -eq $Commit -and $_.head_branch -eq 'master' -and
        $_.event -eq 'push' -and $_.path -eq '.github/workflows/ci.yml'
    } | Sort-Object -Property @{Expression = { [long]$_.id }; Descending = $true}) | Select-Object -First 1
    if ($null -eq $run) {
        throw "No push CI run for release commit $Commit. Push a CI-triggering release change and wait for CI."
    }
    if ([long]$run.id -le 0 -or $run.status -ne 'completed' -or $run.conclusion -ne 'success') {
        throw "Release CI has not passed for $Commit (run $($run.id): $($run.status)/$($run.conclusion)). Resolve failures or wait for completion before releasing."
    }
    return [pscustomobject]@{ Commit = $Commit; RunId = $run.id }
}

function Get-ReleaseCiApproval {
    $commit = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the release commit.' }
    $dirty = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Release worktree must be clean so the package matches the CI-tested commit.' }
    $remote = & git ls-remote --exit-code origin refs/heads/master
    if ($LASTEXITCODE -ne 0 -or @($remote).Count -ne 1) { throw 'Cannot verify the pushed origin/master commit.' }
    $remoteCommit = ($remote -split '\s+')[0]
    if ($remoteCommit -ne $commit) { throw 'Release HEAD must match pushed origin/master. Commit and push the reviewed release first.' }
    $json = & gh api "repos/{owner}/{repo}/actions/workflows/ci.yml/runs?head_sha=$commit&event=push&per_page=100"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot retrieve release CI evidence from GitHub. Check authentication and connectivity.' }
    try { $response = ($json -join "`n") | ConvertFrom-Json }
    catch { throw 'GitHub returned invalid release CI evidence.' }
    $approval = Assert-ReleaseCiResult $commit $remoteCommit $response
    Assert-ReleaseTrackedInputs (Get-Location).Path $commit
    return $approval
}
