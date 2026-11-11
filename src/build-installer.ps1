# Builds dist/SnapLayouts-Setup-<version>.exe from dist/portable (runs build-release.ps1 first unless -SkipPublish).
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File src/build-installer.ps1 [-SkipPublish]
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'installer/version.txt') -Raw).Trim()
$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) { throw "ISCC.exe not found at $iscc" }
if (-not $SkipPublish) { & (Join-Path $PSScriptRoot 'build-release.ps1') }
if (-not (Test-Path (Join-Path $root 'dist/portable/RegionControl.exe'))) { throw 'dist/portable/RegionControl.exe is missing' }
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer/RegionControl.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }
$setup = Join-Path $root "dist/SnapLayouts-Setup-$version.exe"
"{0}: {1:N1} MB" -f $setup, ((Get-Item $setup).Length / 1MB)
