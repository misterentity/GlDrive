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
$ErrorActionPreference = 'SilentlyContinue'
gh release view $Tag *> $null
$tagExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if ($tagExists) {
    Write-Error "Release $Tag already exists. Bump the version in $CsprojPath first."
    exit 1
}

# --- Pin winfsp.msi SHA-256 ---
# Update this constant when upgrading WinFsp.
$WinFspMsiPin = '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A'
$WinFspMsiPath = Join-Path $InstallerDir 'deps\winfsp.msi'
if (Test-Path $WinFspMsiPath) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($WinFspMsiPath)
        $actual = [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToUpper()
    } finally { $sha.Dispose() }
    if ($actual -ne $WinFspMsiPin) {
        Write-Error "winfsp.msi SHA-256 mismatch!`n  Expected: $WinFspMsiPin`n  Actual:   $actual`nUpdate the pin in release.ps1 when intentionally upgrading WinFsp."
        exit 1
    }
    Write-Host "winfsp.msi SHA-256 verified" -ForegroundColor Green
}

# --- Test and build ---
Write-Host "`n=== Running tests ===" -ForegroundColor Cyan
& dotnet test (Join-Path $Root 'src\GlDrive.Tests\GlDrive.Tests.csproj') -c Release '-warnaserror:NU1901,NU1902,NU1903,NU1904'
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "`n=== Running build.ps1 ===" -ForegroundColor Cyan
& "$InstallerDir\build.ps1"
if ($LASTEXITCODE -ne 0) { exit 1 }

# --- Verify artifacts ---
$assets = @()

if (Test-Path $InstallerExe) {
    $assets += $InstallerExe
    Write-Host "Installer: $InstallerExe" -ForegroundColor Green
} else {
    Write-Warning "Installer not found at $InstallerExe - releasing zip only"
}

if (Test-Path $ZipFile) {
    $assets += $ZipFile
    Write-Host "Zip: $ZipFile" -ForegroundColor Green
} else {
    Write-Error "Zip not found at $ZipFile"
    exit 1
}

# --- Generate SHA-256 checksums ---
$ChecksumFile = Join-Path $OutputDir "checksums.sha256"
Write-Host "`n=== Generating checksums ===" -ForegroundColor Cyan
$checksumLines = @()
foreach ($a in $assets) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($a)
    $hashBytes = $sha256.ComputeHash($stream)
    $stream.Close()
    $sha256.Dispose()
    $hash = [BitConverter]::ToString($hashBytes).Replace('-','').ToLower()
    $name = Split-Path -Leaf $a
    $checksumLines += "$hash *$name"
    Write-Host "  $hash  $name"
}
# Write WITHOUT BOM. PS 5.1's `Set-Content -Encoding UTF8` adds a BOM,
# but the C# verifier downloads via HttpClient.GetStringAsync which strips
# the BOM during decode → signed bytes (BOM-included) wouldn't match
# verified bytes (BOM-stripped). Use UTF8Encoding(false) for portability.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($ChecksumFile, ($checksumLines -join "`n") + "`n", $utf8NoBom)
$assets += $ChecksumFile
Write-Host "Checksums: $ChecksumFile" -ForegroundColor Green

# --- Sign checksums.sha256 with RSA private key (gitignored) ---
$SigFile = "$ChecksumFile.sig"
$PrivKeyPath = Join-Path $InstallerDir 'keys\checksum-private.pem'
if (-not (Test-Path $PrivKeyPath)) {
    Write-Error "Signing key not found at $PrivKeyPath. Generate one (see installer/keys/README) and ensure it stays gitignored."
    exit 1
}
Write-Host "`n=== Signing checksums.sha256 ===" -ForegroundColor Cyan
$signerScript = @'
using System;
using System.IO;
using System.Security.Cryptography;
class S {
    static int Main(string[] a) {
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(a[0]));
        var data = File.ReadAllBytes(a[1]);
        var sig = rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        File.WriteAllText(a[2], Convert.ToBase64String(sig));
        return 0;
    }
}
'@
$SignerProj = Join-Path $env:TEMP "gldrive-checksum-signer"
if (Test-Path $SignerProj) { Remove-Item -Recurse -Force $SignerProj }
New-Item -ItemType Directory -Path $SignerProj | Out-Null
$signerScript | Set-Content -Path (Join-Path $SignerProj 'Program.cs')
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>' |
    Set-Content -Path (Join-Path $SignerProj 'sign.csproj')
& dotnet run --project $SignerProj -- $PrivKeyPath $ChecksumFile $SigFile
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to sign checksums.sha256"
    exit 1
}
$assets += $SigFile
Write-Host "Signature: $SigFile" -ForegroundColor Green

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
