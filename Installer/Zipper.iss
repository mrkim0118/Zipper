; Zipper installer (Inno Setup 6). Build with build.ps1 in the repo root.
#define AppVersion "1.0.0"
#define Bin "..\src\Zipper\bin\Release\net48"

[Setup]
AppId={{6B0C8F43-2E7A-4C1B-9D55-8A3E1F2B7C90}
AppName=Zipper
AppVersion={#AppVersion}
AppPublisher=Manggdy
AppVerName=Zipper {#AppVersion}
DefaultDirName={autopf}\Zipper
DefaultGroupName=Zipper
DisableProgramGroupPage=yes
; per-user install by default (no UAC); Zipper's file associations are per-user too
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=ZipperSetup-{#AppVersion}
SetupIconFile=..\Assets\icon.ico
UninstallDisplayIcon={app}\Zipper.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ChangesAssociations=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.AssocTask=압축 파일을 Zipper로 열기 (zip, 7z, rar, egg, alz 등 29가지)
english.AssocTask=Open archives with Zipper (zip, 7z, rar, egg, alz and 24 more)
korean.MenuTask=탐색기 오른쪽 클릭 메뉴에 Zipper 추가
english.MenuTask=Add Zipper to the Explorer context menu
korean.Integration=Windows 연동:
english.Integration=Windows integration:
korean.LaunchApp=Zipper 실행
english.LaunchApp=Launch Zipper
korean.Registering=Windows 연동 설정 중...
english.Registering=Setting up Windows integration...

[Tasks]
Name: "assoc"; Description: "{cm:AssocTask}"; GroupDescription: "{cm:Integration}"
Name: "menu"; Description: "{cm:MenuTask}"; GroupDescription: "{cm:Integration}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Bin}\Zipper.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\Zipper.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\7z.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\7z.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\EggDotNet.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\ThirdParty\EggDotNet-License.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\ThirdParty\7-Zip\License.txt"; DestDir: "{app}"; DestName: "7-Zip License.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Zipper"; Filename: "{app}\Zipper.exe"
Name: "{autodesktop}\Zipper"; Filename: "{app}\Zipper.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Zipper.exe"; Parameters: "/register{code:RegisterArgs}"; StatusMsg: "{cm:Registering}"; Flags: runhidden waituntilterminated
Filename: "{app}\Zipper.exe"; Description: "{cm:LaunchApp}"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\Zipper.exe"; Parameters: "/unregister"; RunOnceId: "ZipperUnregister"; Flags: runhidden waituntilterminated

[Code]
function RegisterArgs(Param: string): string;
begin
  Result := '';
  if WizardIsTaskSelected('assoc') then Result := Result + ' assoc';
  if WizardIsTaskSelected('menu') then Result := Result + ' menu';
  if Result = '' then Result := ' none';
end;
