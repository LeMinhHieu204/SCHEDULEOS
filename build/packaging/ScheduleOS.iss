#define MyAppName "ScheduleOS"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "ScheduleOS"
#define ReleaseDir AddBackslash(SourcePath) + "..\..\artifacts\release"
#define PublishDir AddBackslash(ReleaseDir) + "win-x64"
#define InstallerDir AddBackslash(ReleaseDir) + "installer"
#define IconFile AddBackslash(SourcePath) + "..\..\assets\branding\scheduleos.ico"

[Setup]
AppId={{8A5D3B72-3C79-4A5D-BE2D-3B51F7C8C2D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\ScheduleOS
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#InstallerDir}
OutputBaseFilename=ScheduleOS-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\ScheduleOS.exe
SetupIconFile={#IconFile}

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ScheduleOS"; Filename: "{app}\ScheduleOS.exe"
Name: "{autodesktop}\ScheduleOS"; Filename: "{app}\ScheduleOS.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\ScheduleOS.exe"; Description: "Launch ScheduleOS"; Flags: nowait postinstall skipifsilent

; The application stores user data in %LOCALAPPDATA%\ScheduleOS.
; There is intentionally no [UninstallDelete] section: uninstall must preserve data and backups.
