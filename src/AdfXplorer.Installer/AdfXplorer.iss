#ifndef MyAppVersion
  #define MyAppVersion "0.1.2"
#endif

#ifndef PublishDir
  #define PublishDir "..\AdfXplorer.WinFsp\bin\x64\Release\net10.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{9C9C8F0E-6E9A-4A2B-9D5B-9C0E6E6B2B4F}
AppName=AdfXplorer
AppVersion={#MyAppVersion}
AppPublisher=AdfXplorer
DefaultDirName={autopf}\AdfXplorer
DefaultGroupName=AdfXplorer
UninstallDisplayIcon={app}\adfxplorer.exe
SetupIconFile=..\..\res\adfxplorer.ico
LicenseFile=License.rtf
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=bin\x64\Release
OutputBaseFilename=AdfXplorer-Setup-{#MyAppVersion}
WizardStyle=modern dynamic
ChangesAssociations=yes
DisableProgramGroupPage=yes

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AdfXplorer"; Filename: "{app}\adfxplorer.exe"; IconFilename: "{app}\adfxplorer.exe"
Name: "{group}\{cm:UninstallProgram,AdfXplorer}"; Filename: "{uninstallexe}"

[Registry]
; File association for .adf
Root: HKLM; Subkey: "SOFTWARE\Classes\.adf"; ValueType: string; ValueName: ""; ValueData: "AdfXplorer.AdfFile"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.AdfFile"; ValueType: string; ValueName: ""; ValueData: "Amiga Floppy Disk Image"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.AdfFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\adfxplorer.exe"",0"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.AdfFile\shell\open"; ValueType: string; ValueName: ""; ValueData: "Mount with AdfXplorer"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.AdfFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\adfxplorer.exe"" mount ""%1"""; Flags: uninsdeletekey

; File association for .hdf
Root: HKLM; Subkey: "SOFTWARE\Classes\.hdf"; ValueType: string; ValueName: ""; ValueData: "AdfXplorer.HdfFile"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.HdfFile"; ValueType: string; ValueName: ""; ValueData: "Amiga Hard Disk Image"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.HdfFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\adfxplorer.exe"",0"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.HdfFile\shell\open"; ValueType: string; ValueName: ""; ValueData: "Mount with AdfXplorer"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Classes\AdfXplorer.HdfFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\adfxplorer.exe"" mount ""%1"""; Flags: uninsdeletekey

; RegisteredApplications
Root: HKLM; Subkey: "SOFTWARE\RegisteredApplications"; ValueType: string; ValueName: "AdfXplorer"; ValueData: "SOFTWARE\AdfXplorer\Capabilities"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "SOFTWARE\AdfXplorer\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "AdfXplorer"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\AdfXplorer\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Mounts Amiga .adf/.hdf disk images as Windows drives"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\AdfXplorer\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\adfxplorer.exe"",0"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\AdfXplorer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".adf"; ValueData: "AdfXplorer.AdfFile"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\AdfXplorer\Capabilities\FileAssociations"; ValueType: string; ValueName: ".hdf"; ValueData: "AdfXplorer.HdfFile"; Flags: uninsdeletekey

[Code]
const
  DotNetDesktopUrl = 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-10.0.12-win-x64.exe';
  DotNetDesktopExe = 'windowsdesktop-runtime-10.0.12-win-x64.exe';
  DotNetDesktopSha256 = '55A67D8476CDE95A9CC43A95803B4F54446E7C9251ED22AA6421D4908174AE84';

  WinFspUrl = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi';
  WinFspMsi = 'winfsp-2.1.25156.msi';
  WinFspSha256 = '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A';

var
  DownloadPage: TDownloadWizardPage;
  NeedDotNetDesktop: Boolean;
  NeedWinFsp: Boolean;

function IsDotNetDesktop10Installed: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegKeyExists(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App\10.0.12') then
  begin
    Result := True;
    Exit;
  end;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function IsWinFspInstalled: Boolean;
begin
  Result := RegValueExists(HKLM64, 'SOFTWARE\WinFsp', 'InstallDir') or
            RegValueExists(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir') or
            RegKeyExists(HKLM64, 'SOFTWARE\WinFsp') or
            RegKeyExists(HKLM32, 'SOFTWARE\WinFsp');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  DownloadNeeded: Boolean;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    NeedDotNetDesktop := not IsDotNetDesktop10Installed;
    NeedWinFsp := not IsWinFspInstalled;

    DownloadNeeded := False;
    DownloadPage.Clear;

    if NeedDotNetDesktop then
    begin
      DownloadPage.Add(DotNetDesktopUrl, DotNetDesktopExe, DotNetDesktopSha256);
      DownloadNeeded := True;
    end;

    if NeedWinFsp then
    begin
      DownloadPage.Add(WinFspUrl, WinFspMsi, WinFspSha256);
      DownloadNeeded := True;
    end;

    if DownloadNeeded then
    begin
      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
          Result := True;
        except
          if DownloadPage.AbortedByUser then
            Log('Download aborted by user.')
          else
            SuppressibleMsgBox(AddPeriod(Format('%s: %s', [DownloadPage.LastBaseNameOrUrl, GetExceptionMessage])), mbCriticalError, MB_OK, IDOK);
          Result := False;
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  InstallerPath: String;
begin
  Result := '';

  if NeedDotNetDesktop then
  begin
    InstallerPath := ExpandConstant('{tmp}\' + DotNetDesktopExe);
    if FileExists(InstallerPath) then
    begin
      WizardForm.StatusLabel.Caption := 'Installing Microsoft .NET Desktop Runtime 10...';
      if not Exec(InstallerPath, '/quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) or ((ResultCode <> 0) and (ResultCode <> 3010)) then
      begin
        Result := Format('Failed to install Microsoft .NET Desktop Runtime (Exit code: %d).', [ResultCode]);
        Exit;
      end;
      if ResultCode = 3010 then
        NeedsRestart := True;
    end;
  end;

  if NeedWinFsp then
  begin
    InstallerPath := ExpandConstant('{tmp}\' + WinFspMsi);
    if FileExists(InstallerPath) then
    begin
      WizardForm.StatusLabel.Caption := 'Installing WinFsp...';
      if not Exec(ExpandConstant('{sys}\msiexec.exe'), Format('/i "%s" /quiet /norestart', [InstallerPath]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or ((ResultCode <> 0) and (ResultCode <> 3010)) then
      begin
        Result := Format('Failed to install WinFsp (Exit code: %d).', [ResultCode]);
        Exit;
      end;
      if ResultCode = 3010 then
        NeedsRestart := True;
    end;
  end;
end;
