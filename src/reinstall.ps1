# Rebuild the installer from the current source, stop any running copy, install the new build silently, relaunch it.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File src/reinstall.ps1
# Build first, so a broken tree aborts before anything is stopped. Installs with default tasks only:
# no desktop icon, no Start-with-Windows task, no snap change. The tray toggle for Start with Windows is left alone.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content (Join-Path $root 'installer/version.txt') -Raw).Trim()
$setup = Join-Path $root "dist/SnapLayouts-Setup-$version.exe"

& (Join-Path $PSScriptRoot 'build-installer.ps1')
if (-not (Test-Path $setup)) { throw "Missing $setup" }

$running = Get-Process -Name RegionControl -ErrorAction SilentlyContinue
if ($running) {
  "Stopping {0} running copy(ies): PID {1}" -f @($running).Count, (($running | ForEach-Object Id) -join ', ')
  $running | Stop-Process -Force
  $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
}

$log = Join-Path $env:TEMP 'RegionControl-reinstall.log'
$p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=$log" -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Installer exit code $($p.ExitCode). Log: $log" }

# Find the installed exe (per-user default, then per-machine).
$candidates = @(
  (Join-Path $env:LOCALAPPDATA 'Programs\Snap Layouts\RegionControl.exe'),
  (Join-Path $env:LOCALAPPDATA 'Programs\Region Control\RegionControl.exe'),
  (Join-Path $env:ProgramFiles 'Snap Layouts\RegionControl.exe'),
  (Join-Path $env:ProgramFiles 'Region Control\RegionControl.exe'))
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $exe) { throw "Installed exe not found. Log: $log" }

# VS Code sets variables that confuse child apps; start the app without them.
Remove-Item Env:ELECTRON_RUN_AS_NODE -ErrorAction SilentlyContinue
Start-Process -FilePath $exe
Start-Sleep -Seconds 3
$now = Get-Process -Name RegionControl -ErrorAction SilentlyContinue
if (-not $now) { throw "Installed, but the app is not running. Log: $log" }
"Installed {0} v{1}; running as PID {2}. Setup log: {3}" -f $exe, $version, ($now | Select-Object -First 1).Id, $log
