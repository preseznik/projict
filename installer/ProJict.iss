; Inno Setup script for ProJict. Built by build.ps1, which passes /DAppVersion=x.y.z.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define PublishDir "..\artifacts\publish"

[Setup]
AppId={{F0C7FC25-C930-40BB-A60D-DB6F340B133C}
AppName=ProJict
AppVersion={#AppVersion}
AppVerName=ProJict {#AppVersion}
AppPublisher=preseznik
AppPublisherURL=https://github.com/preseznik/projict
AppSupportURL=https://github.com/preseznik/projict/issues
AppUpdatesURL=https://github.com/preseznik/projict/releases
VersionInfoVersion={#AppVersion}
; Per-user install: no admin rights needed, and updates install without prompts.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\ProJict
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=ProJict-Setup-{#AppVersion}
SetupIconFile=..\src\ProJict.App\Assets\ProJict.ico
UninstallDisplayIcon={app}\ProJict.exe
UninstallDisplayName=ProJict
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ProJict"; Filename: "{app}\ProJict.exe"
Name: "{autodesktop}\ProJict"; Filename: "{app}\ProJict.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ProJict.exe"; Description: "{cm:LaunchProgram,ProJict}"; Flags: nowait postinstall skipifsilent
; In-app updates run the installer silently; start ProJict again afterwards.
Filename: "{app}\ProJict.exe"; Flags: nowait; Check: WizardSilent
