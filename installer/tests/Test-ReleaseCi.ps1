$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'ReleaseCi.ps1')
$commit = '1234567890123456789012345678901234567890'
$other = 'abcdef1234567890123456789012345678901234'
$passed = New-Object 'System.Collections.Generic.List[string]'
function New-Run([long]$Id = 100, [string]$Status = 'completed', [string]$Conclusion = 'success') {
    return [pscustomobject]@{ id=$Id; head_sha=$commit; head_branch='master'; event='push'; path='.github/workflows/ci.yml'; status=$Status; conclusion=$Conclusion }
}
function Expect-Rejected([string]$Name, [scriptblock]$Action, [string]$Message) {
    $rejected = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike $Message) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "CI guard accepted: $Name" }
    $passed.Add($Name)
}
$success = [pscustomobject]@{ workflow_runs=@((New-Run)) }
$approval = Assert-ReleaseCiResult $commit $commit $success
if ($approval.Commit -ne $commit -or $approval.RunId -ne 100) { throw 'Wrong approved source/run' }
$passed.Add('completed successful exact-commit push accepted')
Expect-Rejected 'unpushed source' { Assert-ReleaseCiResult $commit $other $success } '*match*origin/master*'
Expect-Rejected 'malformed source' { Assert-ReleaseCiResult 'short' 'short' $success } '*match*origin/master*'
Expect-Rejected 'missing response' { Assert-ReleaseCiResult $commit $commit $null } '*no usable CI evidence*'
Expect-Rejected 'missing runs property' { Assert-ReleaseCiResult $commit $commit ([pscustomobject]@{}) } '*no usable CI evidence*'
Expect-Rejected 'no exact CI run' { Assert-ReleaseCiResult $commit $commit ([pscustomobject]@{workflow_runs=@()}) } '*No push CI run*'
foreach ($field in @('head_sha','head_branch','event','path')) {
    $wrong = New-Run
    $wrong.$field = 'unrelated'
    Expect-Rejected "wrong $field cannot authorize release" { Assert-ReleaseCiResult $commit $commit ([pscustomobject]@{workflow_runs=@($wrong)}) } '*No push CI run*'
}
foreach ($conclusion in @('failure','cancelled','timed_out','skipped','neutral','')) {
    $unsuccessful = New-Run -Conclusion $conclusion
    Expect-Rejected "completed $conclusion rejected" { Assert-ReleaseCiResult $commit $commit ([pscustomobject]@{workflow_runs=@($unsuccessful)}) } '*CI has not passed*'
}
Expect-Rejected 'in-progress CI rejected even with stale success' {
    Assert-ReleaseCiResult $commit $commit ([pscustomobject]@{workflow_runs=@((New-Run -Status 'in_progress'))})
} '*CI has not passed*'
Expect-Rejected 'newest failed run takes precedence over older success' {
    Assert-ReleaseCiResult $commit $commit ([pscustomobject]@{workflow_runs=@((New-Run -Id 100), (New-Run -Id 101 -Conclusion 'failure'))})
} '*CI has not passed*'
Expect-Rejected 'missing run identifier rejected' {
    Assert-ReleaseCiResult $commit $commit ([pscustomobject]@{workflow_runs=@((New-Run -Id 0))})
} '*CI has not passed*'

# Real disposable git fixture: ignored SDK inputs must not inherit CI approval,
# while generated outputs and ignored packaging credentials/dependencies are allowed.
$inputFixture = Join-Path ([IO.Path]::GetTempPath()) ('gldrive-release-ci-' + [Guid]::NewGuid().ToString('N'))
function Write-InputFixture([string]$Name, [string]$Text) {
    $path = Join-Path $inputFixture $Name
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text, (New-Object System.Text.UTF8Encoding $false))
}
try {
    Write-InputFixture '.gitignore' "ignored.cs`nDirectory.Build.props`nbin/`nobj/`n*_wpftmp.csproj`ninstaller/keys/checksum-private.pem`ninstaller/deps/winfsp.msi`n"
    Write-InputFixture 'src/App/Example.cs' 'class Example {}'
    [void](Invoke-PublishGit $inputFixture 'init --quiet')
    [void](Invoke-PublishGit $inputFixture 'add .')
    [void](Invoke-PublishGit $inputFixture '-c user.name=Fixture -c user.email=fixture@example.invalid commit -qm initial')
    $inputCommit = (Invoke-PublishGit $inputFixture 'rev-parse HEAD').Trim()
    Assert-ReleaseTrackedInputs $inputFixture $inputCommit
    $passed.Add('tracked build inputs accepted')
    Write-InputFixture 'src/App/ignored.cs' 'class IgnoredButCompiled {}'
    if ((Invoke-PublishGit $inputFixture 'status --porcelain').Trim()) { throw 'Fixture source must be genuinely ignored' }
    Expect-Rejected 'ignored SDK source rejected despite clean git status' { Assert-ReleaseTrackedInputs $inputFixture $inputCommit } '*build input is not tracked*ignored.cs*'
    Remove-Item -LiteralPath (Join-Path $inputFixture 'src/App/ignored.cs')
    Write-InputFixture 'Directory.Build.props' '<Project />'
    if ((Invoke-PublishGit $inputFixture 'status --porcelain').Trim()) { throw 'Fixture props must be genuinely ignored' }
    Expect-Rejected 'ignored root MSBuild props rejected' { Assert-ReleaseTrackedInputs $inputFixture $inputCommit } '*build input is not tracked*Directory.Build.props*'
    Remove-Item -LiteralPath (Join-Path $inputFixture 'Directory.Build.props')
    Write-InputFixture 'src/App/bin/Release/generated.cs' 'build output'
    Write-InputFixture 'src/App/obj/Generated.cs' 'build output'
    Write-InputFixture 'src/App/App_wpftmp.csproj' '<Project />'
    Write-InputFixture 'installer/keys/checksum-private.pem' 'synthetic fixture, not a private key'
    Write-InputFixture 'installer/deps/winfsp.msi' 'synthetic dependency bytes'
    Assert-ReleaseTrackedInputs $inputFixture $inputCommit
    $passed.Add('ignored bin obj WPF output signing key and bundled dependency allowed')
    Expect-Rejected 'changed source commit during inventory rejected' { Assert-ReleaseTrackedInputs $inputFixture $other } '*commit changed*'
} finally {
    if (Test-Path -LiteralPath $inputFixture) {
        $resolved = [IO.Path]::GetFullPath($inputFixture)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $resolved) -notlike 'gldrive-release-ci-*') { throw 'CI input fixture cleanup path rejected' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

# Exercise command failures without accessing GitHub or invoking packaging/publish.
$fixture = @{ Dirty=$false; GitFailure=$false; RemoteFailure=$false; ApiFailure=$false; InvalidJson=$false; WorkflowFailure=$false; ApiCalls=0; PackagingAttempted=$false }
function git {
    $global:LASTEXITCODE = 0
    switch ($args[0]) {
        'rev-parse' { if ($fixture.GitFailure) { $global:LASTEXITCODE=1; return }; return $commit }
        'status' { if ($fixture.Dirty) { return ' M src/GlDrive/Example.cs' }; return }
        'ls-remote' { if ($fixture.RemoteFailure) { $global:LASTEXITCODE=1; return }; return "$commit`trefs/heads/master" }
        default { throw 'Unexpected git command' }
    }
}
function gh {
    if ($args[0] -eq 'release' -and $args[1] -eq 'view') { $global:LASTEXITCODE=1; return }
    if ($args[0] -ne 'api' -or $args[1] -ne "repos/{owner}/{repo}/actions/workflows/ci.yml/runs?head_sha=$commit&event=push&per_page=100") {
        throw 'Wrong CI evidence endpoint'
    }
    $fixture.ApiCalls++
    $global:LASTEXITCODE = 0
    if ($fixture.ApiFailure) { $global:LASTEXITCODE=1; return }
    if ($fixture.InvalidJson) { return 'not-json' }
    if ($fixture.WorkflowFailure) { return ([pscustomobject]@{workflow_runs=@((New-Run -Conclusion 'failure'))} | ConvertTo-Json -Depth 5) }
    return ($success | ConvertTo-Json -Depth 5)
}
function Assert-ReleaseTrackedInputs([string]$Root, [string]$Commit) {
    if ($Commit -ne $script:commit -or $Root -ne (Get-Location).Path) { throw 'Wrong build input inventory source' }
}
$approval = Get-ReleaseCiApproval
if ($approval.Commit -ne $commit -or $fixture.ApiCalls -ne 1) { throw 'Command path did not validate exact source' }
$passed.Add('read-only command path requests exact CI workflow and commit')
$fixture.Dirty = $true
Expect-Rejected 'dirty worktree rejected before GitHub request' { Get-ReleaseCiApproval } '*worktree must be clean*'
if ($fixture.ApiCalls -ne 1) { throw 'Dirty source should stop before GitHub request' }
$fixture.Dirty = $false
foreach ($case in @(
    @{Flag='GitFailure'; Message='*Cannot resolve*'},
    @{Flag='RemoteFailure'; Message='*Cannot verify*pushed*'},
    @{Flag='ApiFailure'; Message='*Cannot retrieve*CI evidence*'},
    @{Flag='InvalidJson'; Message='*invalid*CI evidence*'}
)) {
    $fixture[$case.Flag] = $true
    Expect-Rejected $case.Flag { Get-ReleaseCiApproval } $case.Message
    $fixture[$case.Flag] = $false
}
function dotnet { $fixture.PackagingAttempted=$true; throw 'Release crossed the failed CI gate' }
$fixture.WorkflowFailure = $true
Expect-Rejected 'release entry point blocks failed CI before build or publication' {
    & (Join-Path (Split-Path -Parent $PSScriptRoot) 'release.ps1')
} '*CI has not passed*'
if ($fixture.PackagingAttempted) { throw 'Failed CI reached packaging' }
[pscustomobject]@{ passed=$passed.Count; checks=@($passed); networkAccess=$false; applicationBuilt=$false; published=$false } | ConvertTo-Json
