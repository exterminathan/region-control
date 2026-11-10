# Creates "Region Control" shortcuts in the Start menu and on the Desktop, pointing at the built exe.
# Run once after src\run.cmd has built the app. Safe to run again: it replaces both shortcuts.
# Writes only the two .lnk files. No registry, no admin.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'RegionControl\bin\Release\net8.0-windows\RegionControl.exe'
if (-not (Test-Path $exe)) { Write-Error "Build first: run src\run.cmd. Not found: $exe"; exit 1 }
$shell = New-Object -ComObject WScript.Shell
$targets = @(
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Region Control.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Region Control.lnk')
)
foreach ($path in $targets) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = Split-Path $exe
    $lnk.Description = 'Region Control: custom window snap layouts (runs in the tray)'
    $lnk.IconLocation = "$exe,0"
    $lnk.Save()
    Write-Host "created $path"
}
