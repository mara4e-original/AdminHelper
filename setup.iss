; Скрипт установщика для Inno Setup 6 (jrsoftware.org/isdl.php)
#define MyAppName "Admin Helper"
#define MyAppVersion "1.2.0"
#define MyAppExeName "AdminHelper.exe"

[Setup]
AppId={{B7C3A9E2-5D41-4F8A-9C6E-2A1D7E5F0B34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=joker
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=installer
OutputBaseFilename=AdminHelper-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "publish-full\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
; после тихого обновления (из программы) сразу запускаем новую версию
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WizardSilent
