; Gói rescue offline cho DeskBox x64. Không đóng binary vendor vào Git:
; scripts\prepare-offline-prerequisites.ps1 phải tạo artifacts\prerequisites\x64 trước.

#define MyAppName "DeskBox prerequisites"
#define MyAppVersion "1.4.2.1"
#define MyPrerequisiteDir "..\artifacts\prerequisites\x64"
#define DotNetInstallerName "dotnet-runtime-10-win-x64.exe"
#define WindowsAppRuntimeInstallerName "WindowsAppRuntimeInstall-x64.exe"
#define VcRedistInstallerName "vc_redist.x64.exe"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=DeskBox fork
AppComments=Cài sẵn runtime cần cho DeskBox trên máy Windows 10/11 x64.
CreateUninstallRegKey=no
Uninstallable=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={tmp}\DeskBoxPrerequisites
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\Output
OutputBaseFilename=DeskBox_Prerequisites_{#MyAppVersion}_x64
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes

[Files]
Source: "{#MyPrerequisiteDir}\{#DotNetInstallerName}"; Flags: dontcopy
Source: "{#MyPrerequisiteDir}\{#WindowsAppRuntimeInstallerName}"; Flags: dontcopy
Source: "{#MyPrerequisiteDir}\{#VcRedistInstallerName}"; Flags: dontcopy

[Code]
function RunPrerequisite(
  const DisplayName: string;
  const FileName: string;
  const Parameters: string;
  var NeedsRestart: Boolean): Boolean;
var
  ResultCode: Integer;
  InstallerPath: string;
begin
  Result := False;
  ExtractTemporaryFile(FileName);
  InstallerPath := ExpandConstant('{tmp}\' + FileName);
  Log('DeskBox prerequisites: launching ' + DisplayName + ' from ' + InstallerPath);

  if not ShellExec('runas', InstallerPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox(
      'Không thể chạy ' + DisplayName + ' với quyền quản trị. Hãy chấp nhận hộp thoại UAC rồi thử lại.',
      mbCriticalError,
      MB_OK,
      IDOK);
    Exit;
  end;

  Log('DeskBox prerequisites: ' + DisplayName + ' exit code ' + IntToStr(ResultCode));
  if (ResultCode = 3010) or (ResultCode = 1641) then
  begin
    NeedsRestart := True;
    Result := True;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    SuppressibleMsgBox(
      DisplayName + ' cài đặt thất bại (mã ' + IntToStr(ResultCode) + '). Xem Setup Log trong thư mục Temp để chẩn đoán.',
      mbCriticalError,
      MB_OK,
      IDOK);
    Exit;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  if not RunPrerequisite('Visual C++ 2015-2022 Redistributable x64', '{#VcRedistInstallerName}', '/install /quiet /norestart', NeedsRestart) then
  begin
    Result := 'Visual C++ Redistributable chưa cài được.';
    Exit;
  end;

  if not RunPrerequisite('.NET 10 Runtime x64', '{#DotNetInstallerName}', '/install /quiet /norestart', NeedsRestart) then
  begin
    Result := '.NET 10 Runtime chưa cài được.';
    Exit;
  end;

  if not RunPrerequisite('Windows App Runtime 2.2 x64', '{#WindowsAppRuntimeInstallerName}', '--quiet', NeedsRestart) then
  begin
    Result := 'Windows App Runtime chưa cài được.';
    Exit;
  end;

  if NeedsRestart then
    Result := 'Runtime đã được cài nhưng Windows yêu cầu khởi động lại. Hãy restart rồi chạy DeskBox setup.';
end;
