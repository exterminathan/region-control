; Region Control installer. Build with src/build-installer.ps1 (passes /DAppVersion=<installer/version.txt>).
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Optional icon: src/assets/RegionControl.ico. The installer uses Inno's default icon when it is absent.
#define IconFile "..\src\assets\RegionControl.ico"
#define HasIcon FileExists(AddBackslash(SourcePath) + IconFile)

[Setup]
; Fixed AppId: never change it, or upgrades will install side by side.
AppId={{6F1D2B7E-3C54-4E0A-9B6A-5D2C8A41E7F3}
AppName=Region Control
AppVersion={#AppVersion}
AppVerName=Region Control {#AppVersion}
AppPublisher=inNATE Tools
AppCopyright=Copyright (c) 2026 inNATE Tools
; Per-user default is %LOCALAPPDATA%\Programs\Region Control ({autopf} under lowest privileges).
DefaultDirName={autopf}\Region Control
DefaultGroupName=Region Control
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=LICENSE.rtf
WizardImageFile=assets\wizard-164.bmp,assets\wizard-246.bmp
WizardSmallImageFile=assets\small-55.bmp,assets\small-110.bmp
WizardStyle=modern dynamic
VersionInfoCompany=inNATE Tools
VersionInfoCopyright=Copyright (c) 2026 inNATE Tools
VersionInfoProductName=Region Control
VersionInfoDescription=Region Control Setup
OutputDir=..\dist
OutputBaseFilename=RegionControl-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
UninstallDisplayName=Region Control
UninstallDisplayIcon={app}\RegionControl.exe
VersionInfoVersion={#AppVersion}
AppMutex=Local\RegionControl.Single
CloseApplications=yes
RestartApplications=no
CloseApplicationsFilter=RegionControl.exe
#if HasIcon
SetupIconFile={#IconFile}
#endif
; SIGNING: unsigned for now. Enable after defining a SignTool in ISCC (/S) and buying a certificate.
;SignTool=signtool $f
;SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &Desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "&Start Region Control with Windows"; Flags: unchecked
; Windows' own snapping must be off for Region Control. Two choices, default = B. Nothing is applied unless A is chosen.
Name: "snapauto"; Description: "Let Region Control turn off Windows' own snapping for me (keeps Win+Arrow)"; GroupDescription: "Windows' own snap-on-drag:"; Flags: exclusive unchecked
Name: "snapmanual"; Description: "I will turn it off myself in Settings (steps on the last page)"; GroupDescription: "Windows' own snap-on-drag:"; Flags: exclusive
; The exact change for A appears on the "Ready to Install" page (UpdateReadyMemo). Steps for B: finish page (CurPageChanged).

[Files]
Source: "..\dist\portable\RegionControl.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE.rtf"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Region Control"; Filename: "{app}\RegionControl.exe"
Name: "{autodesktop}\Region Control"; Filename: "{app}\RegionControl.exe"; Tasks: desktopicon

[Registry]
; Same value the app's tray toggle writes (Startup.cs): HKCU Run, name RegionControl, quoted exe path.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RegionControl"; ValueData: """{app}\RegionControl.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Fresh install: ask. Upgrade of a running copy: restart it without asking.
Filename: "{app}\RegionControl.exe"; Description: "Launch Region Control"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: not WasRunning
Filename: "{app}\RegionControl.exe"; Flags: nowait runasoriginaluser; Check: WasRunning

[UninstallRun]
; Closes the app if it still runs (CloseApplications covers only the installer).
Filename: "{sys}\taskkill.exe"; Parameters: "/IM RegionControl.exe /F"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
const
  AdvKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced';
  DeskKey = 'Control Panel\Desktop';
  SPI_SETDOCKMOVING = $0091;
  SPIF_UPDATE_AND_SEND = 3;

var
  RanBefore: Boolean;

function SystemParametersInfoW(uiAction, uiParam: Cardinal; pvParam: Cardinal; fWinIni: Cardinal): Boolean;
  external 'SystemParametersInfoW@user32.dll stdcall';

function WasRunning: Boolean;
begin
  Result := RanBefore;
end;

function UserDataDir: String;
begin
  Result := ExpandConstant('{userappdata}\RegionControl');
end;

function BackupFile: String;
begin
  Result := UserDataDir + '\native-snap-backup.json';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + MemoTasksInfo;
  if WizardIsTaskSelected('snapauto') then
    Result := Result + NewLine + NewLine + 'Windows snapping change (undone on uninstall):' + NewLine +
      Space + 'HKCU\' + DeskKey + '  DockMoving -> 0 (SPI_SETDOCKMOVING)' + NewLine +
      Space + 'HKCU\' + AdvKey + '  EnableSnapBar -> DWORD 0' + NewLine +
      Space + 'Old values saved to ' + BackupFile;
end;

{ ---- native snap off / restore ---- }

function ReadRegText(const Key, Name: String; var Text: String): Boolean;
var
  S: String;
  D: Cardinal;
begin
  Result := True;
  if RegQueryStringValue(HKCU, Key, Name, S) then Text := S
  else if RegQueryDWordValue(HKCU, Key, Name, D) then Text := IntToStr(D)
  else Result := False;
end;

procedure SnapOff;
var
  Dock, Bar, Json: String;
begin
  ForceDirectories(UserDataDir);
  if not FileExists(BackupFile) then
  begin
    Json := '{' + #13#10;
    if ReadRegText(DeskKey, 'DockMoving', Dock) then Json := Json + '  "DockMoving": "' + Dock + '",' + #13#10
    else Json := Json + '  "DockMoving": null,' + #13#10;
    if ReadRegText(AdvKey, 'EnableSnapBar', Bar) then Json := Json + '  "EnableSnapBar": ' + Bar + #13#10
    else Json := Json + '  "EnableSnapBar": null' + #13#10;
    Json := Json + '}' + #13#10;
    SaveStringToFile(BackupFile, Json, False);
  end;
  SystemParametersInfoW(SPI_SETDOCKMOVING, 0, 0, SPIF_UPDATE_AND_SEND);
  RegWriteDWordValue(HKCU, AdvKey, 'EnableSnapBar', 0);
end;

{ Reads one field of the simple backup JSON. Returns '' when the field is null or absent. }
function JsonField(const Json, Name: String): String;
var
  P, E: Integer;
  Rest: String;
begin
  Result := '';
  P := Pos('"' + Name + '":', Json);
  if P = 0 then Exit;
  Rest := Trim(Copy(Json, P + Length(Name) + 3, Length(Json)));
  E := 1;
  while (E <= Length(Rest)) and (Rest[E] <> ',') and (Rest[E] <> #13) and (Rest[E] <> #10) and (Rest[E] <> '}') do E := E + 1;
  Rest := Trim(Copy(Rest, 1, E - 1));
  if Rest = 'null' then Exit;
  StringChange(Rest, '"', '');
  Result := Rest;
end;

procedure SnapRestore;
var
  Raw: AnsiString;
  Dock, Bar: String;
begin
  if not FileExists(BackupFile) then Exit;
  if not LoadStringFromFile(BackupFile, Raw) then Exit;
  Dock := JsonField(String(Raw), 'DockMoving');
  Bar := JsonField(String(Raw), 'EnableSnapBar');
  SystemParametersInfoW(SPI_SETDOCKMOVING, StrToIntDef(Dock, 1), 0, SPIF_UPDATE_AND_SEND);
  if Bar = '' then RegDeleteValue(HKCU, AdvKey, 'EnableSnapBar')
  else RegWriteDWordValue(HKCU, AdvKey, 'EnableSnapBar', StrToIntDef(Bar, 1));
  DeleteFile(BackupFile);
end;

{ ---- install flow ---- }

function InitializeSetup: Boolean;
begin
  { Remember whether the app runs now, so an upgrade can restart it. }
  RanBefore := CheckForMutexes('Local\RegionControl.Single');
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('snapauto') then SnapOff;
end;

{ ---- uninstall flow ---- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Rc: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM RegionControl.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Rc);
    SnapRestore;
  end;
  if (CurUninstallStep = usPostUninstall) and DirExists(UserDataDir) and (not UninstallSilent) then
  begin
    if MsgBox('Also delete your Region Control layouts and logs?' + #13#10 + UserDataDir + #13#10#13#10 +
              'Choose No to keep them (default).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(UserDataDir, True, True, True);
  end;
end;

{ Finish page: steps for choice B. }
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and WizardIsTaskSelected('snapmanual') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'One step left: turn off Windows'' own snapping.' + #13#10 +
      '1. Open Settings > System > Multitasking.' + #13#10 +
      '2. Turn off "Snap windows".' + #13#10 +
      'This also turns off Win+Arrow. Region Control cannot work with the Windows snap bar on.';
end;
