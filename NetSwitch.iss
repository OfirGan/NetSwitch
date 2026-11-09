#define AppVersion "2026.11.09-001"

[Setup]
AppId={{930C82B3-A874-45D8-BB4F-14EFEB8A0D4C}
AppName=NetSwitch
AppVersion={#AppVersion}
DefaultDirName={autopf}\NetSwitch
DefaultGroupName=NetSwitch
OutputDir=installer
OutputBaseFilename=NetSwitch-Setup
SetupIconFile=NetSwitch-icon01.ico
UninstallDisplayIcon={app}\NetSwitch.exe
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NetSwitch"; Filename: "{app}\NetSwitch.exe"; WorkingDir: "{app}"