# Builds the portable single-file exe into dist/portable/. Does not edit the .csproj.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File src/build-release.ps1 [-Trim]
param([switch]$Trim)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'installer/version.txt') -Raw).Trim()
$out = Join-Path $root 'dist/portable'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$args = @('publish', (Join-Path $PSScriptRoot 'RegionControl/RegionControl.csproj'),
  '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
  '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-p:DebugSymbols=false',
  "-p:Version=$version", '-p:Company=inNATE Tools', '-p:Product=Region Control', '-p:Copyright=Copyright (c) 2026 inNATE Tools', '-o', $out)
# Trim tried: NETSDK1175 refuses WinForms with trimming (SDK 10.0.302). -Trim stays only to re-test later.
if ($Trim) { $args += @('-p:PublishTrimmed=true', '-p:TrimMode=partial') }
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
$exe = Join-Path $out 'RegionControl.exe'
"{0}: {1:N1} MB, version {2}" -f $exe, ((Get-Item $exe).Length / 1MB), $version
