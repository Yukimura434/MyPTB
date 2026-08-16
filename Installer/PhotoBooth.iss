#define MyAppVersion "1.0.0"
[Setup]
AppId={{7C560A96-24AE-48B0-987A-A6D299A0F310}
AppName=PhotoBooth
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\PhotoBooth
DefaultGroupName=PhotoBooth
OutputBaseFilename=PhotoBooth-Setup-{#MyAppVersion}
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes

[Files]
Source: "..\PhotoBooth.Admin.UI\bin\Release\net48\*"; DestDir: "{app}\Admin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\PhotoBooth.Customer.UI\bin\Release\net48\*"; DestDir: "{app}\Customer"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{localappdata}\PhotoBooth\Data\Sessions"
Name: "{localappdata}\PhotoBooth\Data\Captures"
Name: "{localappdata}\PhotoBooth\Data\Frames"
Name: "{localappdata}\PhotoBooth\Data\Presets"
Name: "{localappdata}\PhotoBooth\Data\Preview"
Name: "{localappdata}\PhotoBooth\Data\Print"
Name: "{localappdata}\PhotoBooth\Data\Logs"
Name: "{localappdata}\PhotoBooth\Data\Temp"

[Icons]
Name: "{group}\PhotoBooth Admin"; Filename: "{app}\Admin\PhotoBooth.Admin.UI.exe"
Name: "{group}\PhotoBooth Customer"; Filename: "{app}\Customer\PhotoBooth.Customer.UI.exe"
Name: "{autodesktop}\PhotoBooth"; Filename: "{app}\Customer\PhotoBooth.Customer.UI.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"

[Run]
Filename: "{app}\Customer\PhotoBooth.Customer.UI.exe"; Parameters: "/enroll-device"; Description: "Register this PhotoBooth device"; Flags: waituntilterminated
Filename: "{app}\Admin\PhotoBooth.Admin.UI.exe"; Description: "Configure PhotoBooth"; Flags: nowait postinstall skipifsilent

[Code]
var
  DevicePage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  DevicePage := CreateInputQueryPage(wpSelectDir, 'Đăng ký thiết bị',
    'Kết nối PhotoBooth với dịch vụ ảnh',
    'Nhập mã thiết bị dùng một lần do quản trị viên cấp và địa chỉ dịch vụ.');
  DevicePage.Add('Mã thiết bị:', True);
  DevicePage.Add('Backend URL:', False);
  DevicePage.Add('Frontend URL:', False);
  DevicePage.Values[1] := 'https://myptbooth-api.phongquoc434.workers.dev';
  DevicePage.Values[2] := 'https://myptbooth-gallery.phongquoc434.chatgpt.site/c';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = DevicePage.ID then
  begin
    if (Trim(DevicePage.Values[0]) = '') or
       (Pos('https://', Lowercase(Trim(DevicePage.Values[1]))) <> 1) or
       (Pos('https://', Lowercase(Trim(DevicePage.Values[2]))) <> 1) then
    begin
      MsgBox('Cần mã thiết bị và hai URL HTTPS hợp lệ.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    DataDir := ExpandConstant('{localappdata}\PhotoBooth\Data');
    ForceDirectories(DataDir);
    SaveStringToFile(DataDir + '\device-enrollment.code', Trim(DevicePage.Values[0]), False);
    SaveStringToFile(DataDir + '\photo-service.config',
      'PHOTO_API_BASE_URL=' + Trim(DevicePage.Values[1]) + #13#10 +
      'PHOTO_PAGE_BASE_URL=' + Trim(DevicePage.Values[2]) + #13#10 +
      'UPLOAD_MAX_RETRIES=5' + #13#10 +
      'UPLOAD_TIMEOUT_SECONDS=120' + #13#10, False);
  end;
end;

function IsDotNet48Installed: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full');
end;
function InitializeSetup: Boolean;
begin
  Result := IsDotNet48Installed;
  if not Result then MsgBox('.NET Framework 4.8 is required.', mbError, MB_OK);
end;
