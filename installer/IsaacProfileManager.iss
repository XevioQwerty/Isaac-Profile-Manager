; Inno Setup script for Isaac Profile Manager.
;
; Built by Package.ps1, which passes AppVersion and SourceDir on the command
; line. Do not run this directly unless you set those yourself.
;
; Two deliberate choices:
;
;  * PrivilegesRequired=lowest, installing under %LOCALAPPDATA%. Junction
;    creation needs no elevation, and a modding tool that demands admin looks
;    like malware to exactly the audience this is for.
;
;  * The config, backups and logs are NOT removed on uninstall. isaac-profiles.json
;    holds the user's profile setup and the backup folder can be the only
;    remaining copy of a mod. An uninstaller is not permission to delete those.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\portable"
#endif

#define AppName      "Isaac Profile Manager"
#define AppExe       "IsaacProfileManager.exe"
#define HelperExe    "ipm-steam-helper.exe"
#define AppPublisher "XevioQwerty"
#define AppUrl       "https://github.com/XevioQwerty/Isaac-Profile-Manager"

[Setup]
AppId={{8A4E1C2F-6B93-4F5A-9D71-2C0B7E5A3D18}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={localappdata}\IsaacProfileManager
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0

OutputDir=..\dist
OutputBaseFilename=IsaacProfileManager-Setup-v{#AppVersion}
SetupIconFile=..\src\IsaacProfileManager\app.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern

; The payload is a ~160 MB self-contained exe that is mostly already-compressed
; .NET assemblies. lzma2/max earns its keep here; solid mode does not, with two
; files.
Compression=lzma2/max
SolidCompression=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}";    DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\{#HelperExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md";    DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "{#SourceDir}\LICENSE";      DestDir: "{app}"; Flags: ignoreversion
; The bundled tools and ready-made patches. skipifsourcedoesntexist so a build
; staged without them still produces an installer.
Source: "{#SourceDir}\bundled\*"; DestDir: "{app}\bundled"; \
    Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}";       Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only the extraction scratch the single-file host leaves behind. Nothing here
; touches isaac-profiles.json, backups\ or logs\.
Type: filesandordirs; Name: "{localappdata}\Temp\.net\IsaacProfileManager"

[Code]
// The app cannot overwrite itself while it is open, and a half-written 160 MB
// exe is a worse outcome than asking the user to close it.
function InitializeSetup(): Boolean;
var
  Running: Boolean;
begin
  Running := FindWindowByClassName('HwndWrapper[IsaacProfileManager.exe;;]') <> 0;
  if Running then
  begin
    MsgBox('Isaac Profile Manager is open. Close it and run this installer again.',
           mbError, MB_OK);
    Result := False;
  end
  else
    Result := True;
end;
