$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'PublishManifest.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('gldrive-publish-manifest-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $fixture 'publish'
$manifest = Join-Path $fixture 'manifest.json'
$utf8 = New-Object System.Text.UTF8Encoding $false
$passed = New-Object 'System.Collections.Generic.List[string]'
function Write-Fixture([string]$Relative, [string]$Text) {
    $path = Join-Path $fixture $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Text, $utf8)
}
function Expect-Rejected([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Guard failed to reject: $Name" }
    $passed.Add($Name)
}
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    Write-Fixture '.gitignore' "publish/`nmanifest.json`nignored.cs`nDirectory.Build.props`n"
    Write-Fixture 'src/App/Example.cs' 'class Example {}'
    Write-Fixture 'publish/App.dll' 'complete published bytes'
    Write-Fixture 'publish/nested/data.txt' 'payload data'
    Write-Fixture ('publish/caf' + [char]0x00E9 + '.txt') 'Unicode filename payload'
    [void](Invoke-PublishGit $fixture 'init --quiet')
    [void](Invoke-PublishGit $fixture 'add .')
    [void](Invoke-PublishGit $fixture '-c user.name=Fixture -c user.email=fixture@example.invalid commit -qm initial')
    $context = Get-PublishContext $fixture '1.2.3' 'fixture-sdk'
    Expect-Rejected 'missing manifest' { Assert-PublishManifest $manifest $payload $context }
    Write-PublishManifest $manifest $payload $context
    Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'fixture-sdk')
    $passed.Add('unchanged provenance and Unicode payload filename accepted')

    Write-Fixture 'publish/App.dll' 'tampered published bytes'
    Expect-Rejected 'modified payload' { Assert-PublishManifest $manifest $payload $context }
    Write-Fixture 'publish/App.dll' 'complete published bytes'
    Write-Fixture 'publish/extra.dll' 'unexpected'
    Expect-Rejected 'extra payload' { Assert-PublishManifest $manifest $payload $context }
    Remove-Item -LiteralPath (Join-Path $payload 'extra.dll')
    Remove-Item -LiteralPath (Join-Path $payload 'nested/data.txt')
    Expect-Rejected 'missing payload' { Assert-PublishManifest $manifest $payload $context }
    Write-Fixture 'publish/nested/data.txt' 'payload data'

    Write-Fixture 'src/App/Example.cs' 'class Edited {}'
    Expect-Rejected 'dirty tracked source' { Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'fixture-sdk') }
    Write-Fixture 'src/App/Example.cs' 'class Example {}'
    Write-Fixture 'src/App/New.cs' 'class Added {}'
    Expect-Rejected 'untracked source' { Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'fixture-sdk') }
    Remove-Item -LiteralPath (Join-Path $fixture 'src/App/New.cs')
    Write-Fixture 'src/App/ignored.cs' 'class IgnoredButCompiled {}'
    Expect-Rejected 'ignored SDK glob source' { Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'fixture-sdk') }
    Remove-Item -LiteralPath (Join-Path $fixture 'src/App/ignored.cs')
    Write-Fixture 'Directory.Build.props' '<Project />'
    Expect-Rejected 'ignored root build properties' { Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'fixture-sdk') }
    Remove-Item -LiteralPath (Join-Path $fixture 'Directory.Build.props')

    Expect-Rejected 'stale version' { Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.4' 'fixture-sdk') }
    Expect-Rejected 'changed SDK' { Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'other-sdk') }
    $otherProperties = $context | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $otherProperties.properties.selfContained = $false
    Expect-Rejected 'changed build properties' { Assert-PublishManifest $manifest $payload $otherProperties }
    Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'fixture-sdk')
    $passed.Add('restored original inputs and payload accepted')
    [void](Invoke-PublishGit $fixture '-c user.name=Fixture -c user.email=fixture@example.invalid commit --allow-empty -qm new-head')
    Expect-Rejected 'stale commit' { Assert-PublishManifest $manifest $payload (Get-PublishContext $fixture '1.2.3' 'fixture-sdk') }
    # Invoke copied entry scripts from an unrelated directory. Stop at their first external
    # command, proving root selection and finally restoration without building or publishing.
    $installerSource = Split-Path -Parent $PSScriptRoot
    $copiedInstaller = Join-Path $fixture 'installer'
    [IO.Directory]::CreateDirectory($copiedInstaller) | Out-Null
    foreach ($name in @('build.ps1','release.ps1','PublishManifest.ps1')) {
        Copy-Item -LiteralPath (Join-Path $installerSource $name) -Destination (Join-Path $copiedInstaller $name)
    }
    Write-Fixture 'src/GlDrive/GlDrive.csproj' '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
    $caller = Join-Path $fixture 'unrelated-caller'
    [IO.Directory]::CreateDirectory($caller) | Out-Null
    $observed = [pscustomobject]@{ Root=$null }
    function dotnet { $observed.Root = (Get-Location).Path; throw 'fixture stopped dotnet' }
    function gh { $observed.Root = (Get-Location).Path; throw 'fixture stopped gh' }
    Push-Location $caller
    try {
        foreach ($name in @('build.ps1','release.ps1')) {
            $observed.Root = $null
            $stopped = $false
            try { & (Join-Path $copiedInstaller $name) | Out-Null }
            catch {
                if ($_.Exception.Message -notlike 'fixture stopped *') { throw }
                $stopped = $true
            }
            if (-not $stopped -or $observed.Root -ne $fixture -or (Get-Location).Path -ne $caller) {
                throw "Working directory was not rooted and restored: $name"
            }
            $passed.Add("$name roots commands and restores caller on failure")
        }
    } finally { Pop-Location }
    [pscustomobject]@{ passed=$passed.Count; checks=@($passed); applicationBuilt=$false; productionTouched=$false } | ConvertTo-Json
} finally {
    if (Test-Path -LiteralPath $fixture) {
        $resolved = (Resolve-Path -LiteralPath $fixture).Path
        $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if (-not $resolved.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolved) -notlike 'gldrive-publish-manifest-*') { throw 'Fixture cleanup path rejected' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
