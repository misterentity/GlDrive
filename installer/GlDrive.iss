#define MyAppName "GlDrive"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "GlDrive"
#define MyAppExeName "GlDrive.exe"
#define MyAppDescription "Mount a glftpd FTPS server as a Windows drive letter"

[Setup]
AppId={{B8F3A1D2-7C4E-4F5A-9B6D-1E2F3A4B5C6D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=GlDriveSetup
SetupIconFile=..\src\GlDrive\Assets\gldrive.ico
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start GlDrive automatically when Windows starts"; GroupDescription: "Startup:"

[Files]
; Published app files (self-contained)
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; WinFsp installer (bundled for silent install if needed)
Source: "deps\winfsp.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsWinFspInstalled

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Auto-start entry (only if user selected the task)
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Install WinFsp silently if not already installed
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; StatusMsg: "Installing WinFsp driver..."; Flags: runhidden waituntilterminated; Check: not IsWinFspInstalled

; Launch after install
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Kill GlDrive before uninstall
Filename: "taskkill.exe"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillGlDrive"

[UninstallDelete]
; Clean up app directory (but NOT %AppData%\GlDrive — keep user config)
Type: filesandordirs; Name: "{app}"

[Code]
function IsWinFspInstalled: Boolean;
var
  DllPath: String;
begin
  // Check for WinFsp DLL in System32
  DllPath := ExpandConstant('{sys}\winfsp-x64.dll');
  Result := FileExists(DllPath);

  // Also check registry as fallback
  if not Result then
    Result := RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\WinFsp');
end;

function InitializeSetup(): Boolean;
var
  WinFspMsi: String;
begin
  Result := True;

  // If WinFsp is not installed, check that we have the MSI bundled
  if not IsWinFspInstalled then
  begin
    WinFspMsi := ExpandConstant('{src}\deps\winfsp.msi');
    // The MSI will be extracted to {tmp} during install, so just warn if check fails at runtime
    Log('WinFsp not detected — will install from bundled MSI.');
  end
  else
    Log('WinFsp already installed — skipping driver install.');
end;
