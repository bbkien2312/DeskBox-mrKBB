[Code]
const
  DotNetRuntimeUrl = 'https://aka.ms/dotnet/10.0/dotnet-runtime-win-x64.exe';
  DotNetRuntimeFallbackUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.11/dotnet-runtime-10.0.11-win-x64.exe';
  DotNetRuntimeInstallerName = 'dotnet-runtime-10-win-x64.exe';
  WindowsAppRuntimeUrl = 'https://download.microsoft.com/download/5e0f2e92-f3ef-4023-97f0-bd57018a478c/WindowsAppRuntimeInstall-x64.exe';
  WindowsAppRuntimeFallbackUrl = 'https://aka.ms/windowsappsdk/2.2/2.2.0/windowsappruntimeinstall-x64.exe';
  WindowsAppRuntimeInstallerName = 'WindowsAppRuntimeInstall-x64.exe';
  VcRedistUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe';
  VcRedistFallbackUrl = 'https://aka.ms/vs/17/release/vc_redist.x64.exe';
  VcRedistInstallerName = 'vc_redist.x64.exe';
  MinimumDeskBoxWindowsBuild = 19044;

var
  DependencyDownloadPage: TDownloadWizardPage;
  DependencyInstallPage: TOutputProgressWizardPage;
  ShouldInstallDotNetRuntime: Boolean;
  ShouldInstallWindowsAppRuntime: Boolean;
  ShouldInstallVcRedist: Boolean;
  DependenciesPrepared: Boolean;

function IsMajorVersion(Value: string; ExpectedMajor: Integer): Boolean;
var
  DotPosition: Integer;
  MajorText: string;
begin
  DotPosition := Pos('.', Value);
  if DotPosition > 0 then
    MajorText := Copy(Value, 1, DotPosition - 1)
  else
    MajorText := Value;

  Result := StrToIntDef(MajorText, 0) = ExpectedMajor;
end;

function IsCompatibleDotNetRuntimeVersion(Value: string): Boolean;
begin
  // A preview/RC folder such as 10.0.0-preview.7 cannot satisfy an app that
  // targets the stable Microsoft.NETCore.App 10.0.0 framework.
  Result := (Pos('-', Value) = 0) and IsMajorVersion(Value, 10);
end;

var
  DotNet10RuntimeDetected: Boolean;

procedure DetectDotNet10RuntimeFromOutput(
  const S: String;
  const Error, FirstLine: Boolean);
var
  LineText: string;
  VersionText: string;
  VersionEnd: Integer;
begin
  if Error then
  begin
    Log('dotnet --list-runtimes error: ' + S);
    Exit;
  end;

  LineText := Trim(S);
  if Pos('Microsoft.NETCore.App ', LineText) <> 1 then
    Exit;

  VersionText := Copy(LineText, Length('Microsoft.NETCore.App ') + 1, MaxInt);
  VersionEnd := Pos(' ', VersionText);
  if VersionEnd > 0 then
    VersionText := Copy(VersionText, 1, VersionEnd - 1);

  if IsCompatibleDotNetRuntimeVersion(VersionText) then
    DotNet10RuntimeDetected := True;
end;

function IsDotNet10RuntimeInstalledAt(BasePath: string): Boolean;
var
  DotNetPath: string;
  ResultCode: Integer;
begin
  Result := False;
  DotNetPath := AddBackslash(BasePath) + 'dotnet\dotnet.exe';
  if not FileExists(DotNetPath) then
    Exit;

  DotNet10RuntimeDetected := False;
  try
    if not ExecAndLogOutput(
      DotNetPath,
      '--list-runtimes',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode,
      @DetectDotNet10RuntimeFromOutput) then
    begin
      Log('DeskBox dependency check could not run: ' + DotNetPath);
      Exit;
    end;
  except
    Log('DeskBox dependency check failed: ' + GetExceptionMessage);
    Exit;
  end;

  Result := (ResultCode = 0) and DotNet10RuntimeDetected;
end;

function IsDotNet10RuntimeInstalled: Boolean;
begin
  // {autopf} follows the installer architecture (Program Files on x64 and
  // native ARM64 Program Files on Windows ARM). Keep {pf} as a compatibility
  // fallback for older Inno Setup installations and custom layouts.
  Result :=
    IsDotNet10RuntimeInstalledAt(ExpandConstant('{autopf}')) or
    IsDotNet10RuntimeInstalledAt(ExpandConstant('{pf}'));
end;

function IsWindowsAppRuntime22Installed: Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -Command "$pkg = Get-AppxPackage -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''X64'' -and [version]$_.Version -ge [version]''2.2.0.0'' } | Select-Object -First 1; if (-not $pkg) { $pkg = Get-AppxPackage -AllUsers -Name Microsoft.WindowsAppRuntime.2 -ErrorAction SilentlyContinue | Where-Object { $_.Architecture -eq ''X64'' -and [version]$_.Version -ge [version]''2.2.0.0'' } | Select-Object -First 1 }; if ($pkg) { exit 0 } exit 1"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and
    (ResultCode = 0);
end;

function IsVcRedistInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result :=
    RegQueryDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
      'Installed',
      Installed) and
    (Installed = 1);

  if not Result then
    Log('DeskBox dependency check: Visual C++ 2015-2022 x64 runtime is missing.');
end;

procedure DetectDeskBoxDependencies;
begin
  ShouldInstallDotNetRuntime := not IsDotNet10RuntimeInstalled;
  ShouldInstallWindowsAppRuntime := not IsWindowsAppRuntime22Installed;
  ShouldInstallVcRedist := not IsVcRedistInstalled;

  Log('DeskBox dependency check: dotnet10Missing=' + IntToStr(Integer(ShouldInstallDotNetRuntime)));
  Log('DeskBox dependency check: windowsAppRuntimeMissing=' + IntToStr(Integer(ShouldInstallWindowsAppRuntime)));
  Log('DeskBox dependency check: vcRedistMissing=' + IntToStr(Integer(ShouldInstallVcRedist)));
end;

procedure WaitForDeskBoxDependencies;
var
  Attempt: Integer;
begin
  // Runtime installers can return just before Windows finishes publishing the
  // machine-wide registration. Recheck for a few seconds before continuing.
  for Attempt := 1 to 30 do
  begin
    DetectDeskBoxDependencies;
    if not (ShouldInstallDotNetRuntime or ShouldInstallWindowsAppRuntime or ShouldInstallVcRedist) then
      Exit;

    Sleep(1000);
  end;

  DetectDeskBoxDependencies;
end;

function DownloadDependencyWithProgress(
  DisplayName: string;
  Url: string;
  FallbackUrl: string;
  FileName: string;
  var ErrorMessage: string): Boolean;
begin
  Result := False;
  ErrorMessage := '';

  DependencyDownloadPage.Clear;
  DependencyDownloadPage.Add(Url, FileName, '');

  try
    DependencyDownloadPage.Download;
    Result := True;
    Exit;
  except
    if DependencyDownloadPage.AbortedByUser then
    begin
      ErrorMessage := 'Download was cancelled.';
      Exit;
    end;

    ErrorMessage := GetExceptionMessage;
    Log(DisplayName + ' primary download failed: ' + ErrorMessage);
  end;

  DependencyDownloadPage.Clear;
  DependencyDownloadPage.Add(FallbackUrl, FileName, '');

  try
    DependencyDownloadPage.Download;
    Result := True;
  except
    if DependencyDownloadPage.AbortedByUser then
      ErrorMessage := 'Download was cancelled.'
    else
      ErrorMessage :=
        DisplayName + ' download failed.' + #13#10 +
        'Primary URL: ' + Url + #13#10 +
        'Fallback URL: ' + FallbackUrl + #13#10 +
        'Error: ' + GetExceptionMessage;

    Log(DisplayName + ' fallback download failed: ' + ErrorMessage);
  end;
end;

function DownloadDeskBoxDependencies: Boolean;
var
  ErrorMessage: string;
begin
  Result := True;

  if not (ShouldInstallDotNetRuntime or ShouldInstallWindowsAppRuntime or ShouldInstallVcRedist) then
    Exit;

  DependencyDownloadPage.Show;
  try
    if ShouldInstallDotNetRuntime then
    begin
      DependencyDownloadPage.Msg1Label.Caption := ExpandConstant('{cm:DownloadingDotNet}');
      if not DownloadDependencyWithProgress(
        '.NET 10 Runtime x64',
        DotNetRuntimeUrl,
        DotNetRuntimeFallbackUrl,
        DotNetRuntimeInstallerName,
        ErrorMessage) then
      begin
        SuppressibleMsgBox(ErrorMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallWindowsAppRuntime then
    begin
      DependencyDownloadPage.Msg1Label.Caption := ExpandConstant('{cm:DownloadingWinAppRuntime}');
      if not DownloadDependencyWithProgress(
        'Windows App Runtime 2.2 x64',
        WindowsAppRuntimeUrl,
        WindowsAppRuntimeFallbackUrl,
        WindowsAppRuntimeInstallerName,
        ErrorMessage) then
      begin
        SuppressibleMsgBox(ErrorMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallVcRedist then
    begin
      DependencyDownloadPage.Msg1Label.Caption := 'Downloading Visual C++ 2015-2022 Redistributable x64...';
      if not DownloadDependencyWithProgress(
        'Visual C++ 2015-2022 Redistributable x64',
        VcRedistUrl,
        VcRedistFallbackUrl,
        VcRedistInstallerName,
        ErrorMessage) then
      begin
        SuppressibleMsgBox(ErrorMessage, mbCriticalError, MB_OK, IDOK);
        Result := False;
        Exit;
      end;
    end;
  finally
    DependencyDownloadPage.Hide;
  end;
end;

function InstallDownloadedDependency(
  DisplayName: string;
  FileName: string;
  Parameters: string;
  Step: Integer;
  StepCount: Integer;
  var NeedsRestart: Boolean): Boolean;
var
  InstallerPath: string;
  ResultCode: Integer;
begin
  Result := False;
  InstallerPath := ExpandConstant('{tmp}\' + FileName);

  DependencyInstallPage.SetProgress(Step - 1, StepCount);
  DependencyInstallPage.SetText(
    FmtMessage(ExpandConstant('{cm:InstallingDependency}'), [DisplayName]),
    '');

  if not ShellExec('runas', InstallerPath, Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox(DisplayName + ' installer could not be started with administrator permission. Please allow the Windows prompt and try again.', mbCriticalError, MB_OK, IDOK);
    Exit;
  end;

  if (ResultCode = 3010) or (ResultCode = 1641) then
  begin
    NeedsRestart := True;
    Result := True;
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    SuppressibleMsgBox(
      DisplayName + ' installation failed. Exit code: ' + IntToStr(ResultCode) + '.' + #13#10 +
      'Please confirm this Windows version is supported, or install the dependency manually and run DeskBox setup again.',
      mbCriticalError,
      MB_OK,
      IDOK);
    Exit;
  end;

  DependencyInstallPage.SetProgress(Step, StepCount);
  Result := True;
end;

function InstallDeskBoxDependencies(var NeedsRestart: Boolean): Boolean;
var
  Step: Integer;
  StepCount: Integer;
begin
  Result := True;
  Step := 0;
  StepCount := 0;

  if ShouldInstallDotNetRuntime then
    StepCount := StepCount + 1;

  if ShouldInstallWindowsAppRuntime then
    StepCount := StepCount + 1;

  if ShouldInstallVcRedist then
    StepCount := StepCount + 1;

  if StepCount = 0 then
    Exit;

  DependencyInstallPage.Show;
  try
    if ShouldInstallVcRedist then
    begin
      Step := Step + 1;
      if not InstallDownloadedDependency(
        'Visual C++ 2015-2022 Redistributable x64',
        VcRedistInstallerName,
        '/install /quiet /norestart',
        Step,
        StepCount,
        NeedsRestart) then
      begin
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallDotNetRuntime then
    begin
      Step := Step + 1;
      if not InstallDownloadedDependency(
        '.NET 10 Runtime x64',
        DotNetRuntimeInstallerName,
        '/install /quiet /norestart',
        Step,
        StepCount,
        NeedsRestart) then
      begin
        Result := False;
        Exit;
      end;
    end;

    if ShouldInstallWindowsAppRuntime then
    begin
      Step := Step + 1;
      if not InstallDownloadedDependency(
        'Windows App Runtime 2.2 x64',
        WindowsAppRuntimeInstallerName,
        '--quiet',
        Step,
        StepCount,
        NeedsRestart) then
      begin
        Result := False;
        Exit;
      end;
    end;
  finally
    DependencyInstallPage.Hide;
  end;
end;

function GetDeskBoxWindowsCompatibilityError: String;
var
  Version: TWindowsVersion;
begin
  Result := '';
  if not IsWin64 then
  begin
    Result :=
      'DeskBox requires 64-bit Windows.' + #13#10 +
      'DeskBox chỉ hỗ trợ Windows 64-bit.';
    Exit;
  end;

  GetWindowsVersionEx(Version);

  if (Version.Major < 10) or
     ((Version.Major = 10) and (Version.Build < MinimumDeskBoxWindowsBuild)) then
  begin
    Result :=
      'DeskBox requires Windows 10 21H2 (build 19044) or newer, 64-bit.' + #13#10 +
      'Windows hiện tại: ' + IntToStr(Version.Major) + '.' +
      IntToStr(Version.Minor) + ' (build ' + IntToStr(Version.Build) + ').' + #13#10 +
      'Hãy cập nhật Windows rồi chạy lại bộ cài.';
  end;
end;

procedure InitializeWizard;
begin
  DependencyDownloadPage := CreateDownloadPage(ExpandConstant('{cm:DependencyDownloadTitle}'), ExpandConstant('{cm:DependencyDownloadSubtitle}'), nil);
  DependencyDownloadPage.ShowBaseNameInsteadOfUrl := True;
  DependencyInstallPage := CreateOutputProgressPage(ExpandConstant('{cm:DependencyInstallTitle}'), ExpandConstant('{cm:DependencyInstallSubtitle}'));
end;

function PrepareDeskBoxDependencies(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if DependenciesPrepared then
    Exit;

  Result := GetDeskBoxWindowsCompatibilityError;
  if Result <> '' then
  begin
    Log('DeskBox compatibility preflight failed: ' + Result);
    Exit;
  end;

  NeedsRestart := False;
  DetectDeskBoxDependencies;

  if not DownloadDeskBoxDependencies then
  begin
    Result := 'DeskBox dependencies could not be downloaded.';
    Exit;
  end;

  if not InstallDeskBoxDependencies(NeedsRestart) then
  begin
    Result := 'DeskBox dependencies could not be installed.';
    Exit;
  end;

  if NeedsRestart then
  begin
    Result := ExpandConstant('{cm:NeedsRestart}');
    Exit;
  end;

  WaitForDeskBoxDependencies;
  if ShouldInstallDotNetRuntime or ShouldInstallVcRedist then
  begin
    Result := ExpandConstant('{cm:DependencyVerificationFailed}');
    Exit;
  end;

  if ShouldInstallWindowsAppRuntime then
  begin
    // A system-wide Windows App Runtime install can be successful while the
    // non-elevated Setup process cannot enumerate the package yet. A restart
    // completes user registration and prevents a false permanent failure.
    NeedsRestart := True;
    Result := ExpandConstant('{cm:NeedsRestart}');
    Log('Windows App Runtime was installed but is not visible to the current user yet; requesting restart.');
    Exit;
  end;

  DependenciesPrepared := True;
end;
