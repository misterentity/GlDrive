# GlDrive Installer Build Script
# Prerequisites: .NET 10 SDK, Inno Setup 6

param(
    [switch]$SkipPublish,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$InstallerDir = $PSScriptRoot
$PublishDir = Join-Path $InstallerDir 'publish'
$CsprojPath = Join-Path $Root 'src\GlDrive\GlDrive.csproj'
$IssPath = Join-Path $InstallerDir 'GlDrive.iss'
$OutputDir = Join-Path $InstallerDir 'output'

# Find ISCC.exe
$IsccPaths = @(
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe',
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    $Iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
}

# --- Step 1: Publish ---
if (-not $SkipPublish) {
    Write-Host "`n=== Publishing GlDrive (self-contained) ===" -ForegroundColor Cyan

    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    dotnet publish $CsprojPath `
        -c Release `
        --self-contained `
        -o $PublishDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit 1
    }

    $fileCount = (Get-ChildItem $PublishDir -Recurse -File).Count
    $sizeMB = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Host "Published $fileCount files ($sizeMB MB) to $PublishDir" -ForegroundColor Green
}
else {
    Write-Host "`n=== Skipping publish (using existing output) ===" -ForegroundColor Yellow
    if (-not (Test-Path $PublishDir)) {
        Write-Error "Publish directory not found: $PublishDir. Run without -SkipPublish first."
        exit 1
    }
}

# --- Step 2: Check WinFsp MSI ---
$WinFspMsi = Join-Path $InstallerDir 'deps\winfsp.msi'
if (-not (Test-Path $WinFspMsi)) {
    Write-Warning @"
WinFsp MSI not found at: $WinFspMsi
Download the latest .msi from https://github.com/winfsp/winfsp/releases
and save it as: $WinFspMsi
The installer will still build but WinFsp won't be bundled.
"@
}

# --- Step 3: Build installer ---
if (-not $SkipInstaller) {
    if (-not $Iscc) {
        Write-Error @"
Inno Setup compiler (ISCC.exe) not found.
Install Inno Setup 6 from https://jrsoftware.org/isinfo.php
or add ISCC.exe to your PATH.
"@
        exit 1
    }

    Write-Host "`n=== Building installer with Inno Setup ===" -ForegroundColor Cyan
    & $Iscc $IssPath

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
        exit 1
    }

    $installer = Join-Path $OutputDir 'GlDriveSetup.exe'
    if (Test-Path $installer) {
        $sizeMB = [math]::Round((Get-Item $installer).Length / 1MB, 1)
        Write-Host "`nInstaller created: $installer ($sizeMB MB)" -ForegroundColor Green
    }
}
else {
    Write-Host "`n=== Skipping installer build ===" -ForegroundColor Yellow
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
