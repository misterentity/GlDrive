# Local publish-cache provenance. Release assets still require the separate RSA signature.
# Kept outside publish/ so the receipt is never shipped or included in its own payload hashes.

function Invoke-PublishGit([string]$Root, [string]$Arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = 'git'
    $start.Arguments = '-C "' + $Root + '" ' + $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object System.Text.UTF8Encoding $false
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Cannot fingerprint publish inputs: $errorText" }
        return $output
    } finally { $process.Dispose() }
}

function Get-PublishFiles([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Missing publish directory: $root" }
    $found = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
    $stack = New-Object 'System.Collections.Generic.Stack[string]'
    $stack.Push($root)
    while ($stack.Count) {
        $directory = $stack.Pop()
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked publish directory is not reusable' }
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked publish payload is not reusable' }
            if ($item.PSIsContainer) { $stack.Push($item.FullName) } else { $found.Add($item) }
        }
    }
    $files = @($found | Sort-Object FullName)
    if ($files.Count -eq 0) { throw 'Publish payload is empty' }
    foreach ($file in $files) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked publish payload is not reusable' }
        [pscustomobject][ordered]@{
            path = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            bytes = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
}

function Get-PublishContext([string]$Root, [string]$Version, [string]$SdkVersion = '') {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not $SdkVersion) {
        Push-Location $rootPath
        try {
            $SdkVersion = (& dotnet --version | Out-String).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot determine publish SDK version' }
        } finally { Pop-Location }
    }
    $commit = (Invoke-PublishGit $rootPath 'rev-parse HEAD').Trim()
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($name in (Invoke-PublishGit $rootPath 'ls-files -z --cached --others --exclude-standard').Split([char]0)) {
        if ($name) { [void]$names.Add($name) }
    }
    foreach ($name in @('Directory.Build.props','Directory.Build.targets','Directory.Build.rsp','Directory.Packages.props','global.json','NuGet.Config','nuget.config','.editorconfig')) {
        if (Test-Path -LiteralPath (Join-Path $rootPath $name) -PathType Leaf) { [void]$names.Add($name) }
    }
    # Git-ignored source additions can still enter SDK globs. Include them, but not
    # generated bin/obj or WPF temporary projects. Normal untracked inputs above also
    # cover build scripts, root props/targets, resources, and documentation copied at publish.
    $stack = New-Object 'System.Collections.Generic.Stack[string]'
    $stack.Push((Join-Path $rootPath 'src'))
    while ($stack.Count) {
        $directory = $stack.Pop()
        if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked source directory requires a fresh reviewed build' }
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked source input requires a fresh reviewed build' }
            if ($item.PSIsContainer) {
                if ($item.Name -notin @('bin','obj','.git')) { $stack.Push($item.FullName) }
            } elseif ($item.Name -notlike '*_wpftmp.csproj') {
                [void]$names.Add($item.FullName.Substring($rootPath.Length + 1).Replace('\', '/'))
            }
        }
    }
    $inputs = @(
        foreach ($name in @($names | Sort-Object -CaseSensitive)) {
            $path = [IO.Path]::GetFullPath((Join-Path $rootPath $name))
            if (-not $path.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish input escaped the checkout' }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Tracked publish input is missing: $name" }
            if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked publish input requires review' }
            [pscustomobject][ordered]@{ path=$name; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    )
    [pscustomobject][ordered]@{
        commit=$commit; version=$Version; sdk=$SdkVersion
        properties=[pscustomobject][ordered]@{ configuration='Release'; runtimeIdentifier='win-x64'; selfContained=$true }
        inputs=$inputs
    }
}

function Assert-PublishContext($Expected, $Actual) {
    if (($Expected | ConvertTo-Json -Depth 10 -Compress) -cne ($Actual | ConvertTo-Json -Depth 10 -Compress)) {
        throw 'Publish provenance changed (commit, version, SDK, build properties, or source inputs); publish again'
    }
}

function Write-PublishManifest([string]$ManifestPath, [string]$PublishDirectory, $Context) {
    $manifest = [pscustomobject][ordered]@{
        schema=1; createdUtc=[DateTime]::UtcNow.ToString('O'); context=$Context
        files=@(Get-PublishFiles $PublishDirectory)
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ManifestPath))) | Out-Null
    [IO.File]::WriteAllText($ManifestPath, ($manifest | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding $false))
}

function Assert-PublishManifest([string]$ManifestPath, [string]$PublishDirectory, $Context) {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { throw 'Publish provenance manifest is missing; run without -SkipPublish' }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.schema -ne 1) { throw 'Unsupported publish manifest schema' }
    Assert-PublishContext $manifest.context $Context
    $files = @(Get-PublishFiles $PublishDirectory)
    if (($manifest.files | ConvertTo-Json -Depth 5 -Compress) -cne ($files | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Published payload differs from its manifest; publish again'
    }
}
