#define MyAppVersion "3.0.12"
#define MyDataVersion "2.0.0-alpha"
#ifndef MyAppSource
#define MyAppSource "..\PhotoBooth.Admin.UI\bin\Release\net48"
#endif
[Setup]
AppId={{7C560A96-24AE-48B0-987A-A6D299A0F310}
AppName=MiuPhotoBooth
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\MiuPhotoBooth
DefaultGroupName=MiuPhotoBooth
OutputBaseFilename=MiuPhotoBooth-Setup-{#MyAppVersion}
UsePreviousAppDir=no
PrivilegesRequired=lowest
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\MiuPhotoBooth.exe

[InstallDelete]
Type: files; Name: "{app}\MiuManager.exe"
Type: files; Name: "{app}\MiuManager.exe.config"
Type: files; Name: "{app}\MiuManager.pdb"
Type: files; Name: "{group}\MiuManager.lnk"
Type: files; Name: "{autodesktop}\MiuManager.lnk"

[Files]
Source: "{#MyAppSource}\*"; DestDir: "{app}"; Excludes: "MiuManager.exe,MiuManager.exe.config,MiuManager.pdb,MiuPhotoBooth.LegacyHost.*,X*Check.exe,X*Check.exe.config"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\PhotoBooth.Admin.UI\Assets\photobooth.ico"; DestDir: "{app}"; DestName: "MiuPhotoBooth.ico"; Flags: ignoreversion

[Dirs]
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Sessions"
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Captures"
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Frames"
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Presets"
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Preview"
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Print"
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Logs"
Name: "{localappdata}\PhotoBooth\{#MyDataVersion}\Data\Temp"

[Icons]
Name: "{group}\MiuPhotoBooth"; Filename: "{app}\MiuPhotoBooth.exe"; IconFilename: "{app}\MiuPhotoBooth.ico"; WorkingDir: "{app}"
Name: "{autodesktop}\MiuPhotoBooth"; Filename: "{app}\MiuPhotoBooth.exe"; IconFilename: "{app}\MiuPhotoBooth.ico"; WorkingDir: "{app}"; Tasks: desktopbooth

[Tasks]
Name: desktopbooth; Description: "Tạo shortcut MiuPhotoBooth trên màn hình"; Flags: checkedonce

[Run]
Filename: "{app}\MiuPhotoBooth.exe"; Description: "Mở MiuPhotoBooth"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNet48Installed: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full');
end;
function InitializeSetup: Boolean;
begin
  Result := IsDotNet48Installed;
  if not Result then MsgBox('.NET Framework 4.8 is required.', mbError, MB_OK);
end;
