; Build with scripts/build-installer.ps1. Only the explicit distribution files below
; are included. User settings, caches, backups and OneDrive content are never inputs.
#ifndef AppVersion
  #error AppVersion must be provided by build-installer.ps1.
#endif
#ifndef PublishDir
  #error PublishDir must be provided by build-installer.ps1.
#endif
#ifndef ProjectRoot
  #error ProjectRoot must be provided by build-installer.ps1.
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be provided by build-installer.ps1.
#endif

[Setup]
; Fixed identity for all installer upgrades. Earlier releases were portable only.
AppId={{D83DC639-8D06-4BBD-A4FE-029E57EE25A8}
AppName=ClipBridge
AppVersion={#AppVersion}
AppVerName=ClipBridge {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoProductTextVersion={#AppVersion}
AppPublisher=gomamonaka
AppPublisherURL=https://github.com/gomamonaka/ClipBridge
AppSupportURL=https://github.com/gomamonaka/ClipBridge/issues
AppUpdatesURL=https://github.com/gomamonaka/ClipBridge/releases
AppCopyright=Copyright (c) 2026 gomamonaka
DefaultDirName={localappdata}\Programs\ClipBridge
DefaultGroupName=ClipBridge
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
AppMutex=Local\ClipBridge.App.v1
CloseApplications=no
RestartApplications=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableReadyPage=no
UsePreviousTasks=yes
LicenseFile={#ProjectRoot}\LICENSE.txt
SetupIconFile={#ProjectRoot}\src\ClipBridge.App\Assets\ClipBridge.ico
UninstallDisplayIcon={app}\ClipBridge.exe
UninstallDisplayName=ClipBridge
Uninstallable=yes
WizardStyle=modern
LanguageDetectionMethod=none
ShowLanguageDialog=no
Compression=lzma2
SolidCompression=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=ClipBridge-{#AppVersion}-Setup

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; Flags: unchecked
Name: "autostart"; Description: "Windows と一緒に ClipBridge を起動する"; Flags: unchecked

[Files]
Source: "{#PublishDir}\ClipBridge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\使い方.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\TESTING.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ProjectRoot}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\Notices\NET-Runtime-LICENSE.txt"; DestDir: "{app}\Notices"; Flags: ignoreversion
Source: "{#PublishDir}\Notices\NET-Third-Party-Notices.txt"; DestDir: "{app}\Notices"; Flags: ignoreversion
Source: "{#PublishDir}\Notices\WindowsDesktop-LICENSE.txt"; DestDir: "{app}\Notices"; Flags: ignoreversion

[Icons]
Name: "{group}\ClipBridge"; Filename: "{app}\ClipBridge.exe"; WorkingDir: "{app}"
Name: "{group}\使い方"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\使い方.md"""; Comment: "ClipBridge の操作と接続設定を見る"
Name: "{group}\ClipBridge のアンインストール"; Filename: "{uninstallexe}"
Name: "{userdesktop}\ClipBridge"; Filename: "{app}\ClipBridge.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ClipBridge.exe"; Description: "ClipBridge を起動する"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  AutoStartKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  AutoStartValue = 'ClipBridge';

function InstalledAutoStartCommand: String;
begin
  { Keep identical to App.xaml.cs, so the application's own checkbox can update it. }
  Result := '"' + ExpandConstant('{app}\ClipBridge.exe') + '" --background';
end;

function HasExplicitTaskSelection: Boolean;
var
  I: Integer;
  Parameter: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Parameter := Uppercase(ParamStr(I));
    if (Pos('/TASKS=', Parameter) = 1) or
       (Pos('/MERGETASKS=', Parameter) = 1) or
       (Pos('/LOADINF=', Parameter) = 1) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure InitializeWizard;
var
  ExistingCommand: String;
begin
  { Preserve the actual application setting, including an earlier portable path.
    Explicit command-line task selections remain authoritative. Other tasks are
    left unchanged, including a previous desktop-shortcut selection. }
  if not HasExplicitTaskSelection then
  begin
    if RegQueryStringValue(HKCU, AutoStartKey, AutoStartValue, ExistingCommand) then
      WizardSelectTasks('autostart')
    else
      WizardSelectTasks('!autostart');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('autostart') then
    begin
      if not RegWriteStringValue(HKCU, AutoStartKey, AutoStartValue, InstalledAutoStartCommand) then
        MsgBox('自動起動の設定を保存できませんでした。ClipBridge の通知領域メニューから設定してください。', mbError, MB_OK);
    end
    else
    begin
      { Explicitly unchecking the task also removes a stale portable registration. }
      if RegValueExists(HKCU, AutoStartKey, AutoStartValue) and
         not RegDeleteValue(HKCU, AutoStartKey, AutoStartValue) then
        MsgBox('以前の自動起動設定を解除できませんでした。ClipBridge の通知領域メニューから設定してください。', mbError, MB_OK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExistingCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { A user may have switched back to a portable copy after installing. Remove
      only a registration still owned by this installation. No user data is deleted. }
    if RegQueryStringValue(HKCU, AutoStartKey, AutoStartValue, ExistingCommand) and
       (CompareText(Trim(ExistingCommand), InstalledAutoStartCommand) = 0) then
      RegDeleteValue(HKCU, AutoStartKey, AutoStartValue);
  end;
end;
