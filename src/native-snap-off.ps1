# Switches native snap-on-drag off (Option B: Win+Arrow keyboard snap stays on). No admin needed.
#   DockMoving    -> 0 via SystemParametersInfo(SPI_SETDOCKMOVING), saved to HKCU\Control Panel\Desktop
#   EnableSnapBar -> DWORD 0 in HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced
# Saves the prior values to native-snap-backup.json next to this script, for native-snap-restore.ps1.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File native-snap-off.ps1 [-DryRun]
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

Show 'Before'
if ($DryRun) { 'Dry run: would set DockMoving=0 (SPI_SETDOCKMOVING) and EnableSnapBar=DWORD 0. Nothing changed.'; exit 0 }

if (-not (Test-Path $backup)) {
    @{ DockMoving = Get-Reg $desk 'DockMoving'; EnableSnapBar = Get-Reg $adv 'EnableSnapBar' } | ConvertTo-Json | Set-Content -Path $backup -Encoding utf8
    "Saved prior values to $backup"
}

# SPI_SETDOCKMOVING = 0x0091, uiParam FALSE, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE = 3
if (-not [RC.Spi]::SystemParametersInfo(0x0091, 0, [IntPtr]::Zero, 3)) { throw "SPI_SETDOCKMOVING failed, error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
Set-ItemProperty -Path $adv -Name 'EnableSnapBar' -Type DWord -Value 0

Show 'After'
'If the snap bar still appears when you drag to the top, sign out and in (or restart Explorer) once.'
