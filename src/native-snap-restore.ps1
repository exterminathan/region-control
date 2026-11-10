# Undoes native-snap-off.ps1. Restores the values saved in native-snap-backup.json,
# or the Windows defaults if there is no backup: DockMoving=1, EnableSnapBar removed (absent = on).
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File native-snap-restore.ps1 [-DryRun]
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'

Add-Type -Name Spi -Namespace RC -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint ui, ref int pv, uint f);
[DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint a, uint ui, IntPtr pv, uint f);
'@
$desk = 'HKCU:\Control Panel\Desktop'
$adv  = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$backup = Join-Path $PSScriptRoot 'native-snap-backup.json'

function Get-Reg($path, $name) { $p = Get-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue; if ($p) { $p.$name } else { $null } }
function Show($label) {
    $v = 0; [RC.Spi]::SystemParametersInfo(0x0090, 0, [ref]$v, 0) | Out-Null   # SPI_GETDOCKMOVING
    $bar = Get-Reg $adv 'EnableSnapBar'
    "{0}: DockMoving reg={1} live={2} | EnableSnapBar={3}" -f $label, (Get-Reg $desk 'DockMoving'), $v, $(if ($null -eq $bar) { '(absent = on)' } else { $bar })
}

$dock = 1; $bar = $null; $source = 'Windows defaults (no backup file)'
if (Test-Path $backup) {
    $b = Get-Content $backup -Raw | ConvertFrom-Json
    if ($null -ne $b.DockMoving) { $dock = [int]$b.DockMoving }
    $bar = $b.EnableSnapBar
    $source = $backup
}

Show 'Before'
if ($DryRun) { "Dry run: would set DockMoving=$dock and EnableSnapBar=$(if ($null -eq $bar) { 'removed' } else { $bar }) from $source. Nothing changed."; exit 0 }

# SPI_SETDOCKMOVING = 0x0091, uiParam TRUE/FALSE, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE = 3
if (-not [RC.Spi]::SystemParametersInfo(0x0091, [uint32]$dock, [IntPtr]::Zero, 3)) { throw "SPI_SETDOCKMOVING failed, error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
if ($null -eq $bar) { Remove-ItemProperty -Path $adv -Name 'EnableSnapBar' -ErrorAction SilentlyContinue }
else { Set-ItemProperty -Path $adv -Name 'EnableSnapBar' -Type DWord -Value ([int]$bar) }
if (Test-Path $backup) { Remove-Item $backup }

Show 'After'
"Restored from $source."
