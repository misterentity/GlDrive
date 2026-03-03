# GlDrive Release Script
# Builds everything then creates a GitHub release with both artifacts.
# Prerequisites: .NET 10 SDK, Inno Setup 6, gh CLI (authenticated)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$InstallerDir = $PSScriptRoot
$OutputDir = Join-Path $InstallerDir 'output'
$CsprojPath = Join-Path $Root 'src\GlDrive\GlDrive.csproj'

# --- Read version ---
[xml]$Csproj = Get-Content $CsprojPath
$Version = $Csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $Version) {
    Write-Error "Could not read <Version> from $CsprojPath"
    exit 1
}

$Tag = "v$Version"
$InstallerExe = Join-Path $OutputDir "GlDriveSetup-v$Version.exe"
$ZipFile = Join-Path $OutputDir "GlDrive-v$Version-win-x64.zip"

Write-Host "=== GlDrive Release $Tag ===" -ForegroundColor Cyan

# --- Check gh CLI ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "gh CLI not found. Install from https://cli.github.com/"
    exit 1
}

# --- Check tag doesn't already exist ---
$existing = gh release view $Tag 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Error "Release $Tag already exists. Bump the version in $CsprojPath first."
    exit 1
}

# --- Build ---
Write-Host "`n=== Running build.ps1 ===" -ForegroundColor Cyan
& "$InstallerDir\build.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }

# --- Verify artifacts ---
$assets = @()

if (Test-Path $InstallerExe) {
    $assets += $InstallerExe
    Write-Host "Installer: $InstallerExe" -ForegroundColor Green
} else {
    Write-Warning "Installer not found at $InstallerExe — releasing zip only"
}

if (Test-Path $ZipFile) {
    $assets += $ZipFile
    Write-Host "Zip: $ZipFile" -ForegroundColor Green
} else {
    Write-Error "Zip not found at $ZipFile"
    exit 1
}

# --- Create GitHub release ---
Write-Host "`n=== Creating GitHub release $Tag ===" -ForegroundColor Cyan

Push-Location $Root
try {
    gh release create $Tag @assets --title $Tag --generate-notes
    if ($LASTEXITCODE -ne 0) {
        Write-Error "gh release create failed"
        exit 1
    }
} finally {
    Pop-Location
}

Write-Host "`n=== Release $Tag published ===" -ForegroundColor Green
