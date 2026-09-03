; Inno Setup script for CANVideoEmulator.
;
; Produces installer/CANVideoEmulator-Setup-<version>-win-x64.exe from the
; self-contained single-file publish. Invoked by scripts/build_release.ps1,
; which passes the paths in on the command line:
;
;   ISCC.exe /DAppVersion=1.0.0 /DPublishDir=... /DScenarioDir=... /DOutputDir=...
;
; PEAK's driver is deliberately NOT bundled. PEAK's PCAN-Basic EULA covers the
; PCAN-Basic package and permits free redistribution when its terms travel with
; it, but that is a different artifact from the Windows Driver Setup, whose
; redistribution terms are not stated in anything shipped with the package. Since
; requirement 60 says not to bundle what cannot be confirmed, the installer
; instead offers to open PEAK's official download page at the end.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\release\portable"
#endif
#ifndef OutputDir
  #define OutputDir "..\release\installer"
#endif

#define AppName "CANVideoEmulator"
#define AppExeName "CANVideoEmulator.exe"
#define AppPublisher "CANVideoEmulator"
#define DriverUrl "https://www.peak-system.com/quick/DrvSetup"

[Setup]
AppId={{7E3A9C41-5D2B-4E88-9C13-2F6A8B4D1E70}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir={#OutputDir}
OutputBaseFilename=CANVideoEmulator-Setup-{#AppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The player is a 64-bit self-contained build; PCAN-Basic and the PEAK driver are
; Windows-only, so there is no 32-bit or ARM configuration to offer.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-machine install by default, but allow a non-admin per-user install so the
; app can be put on a locked-down exhibition laptop.
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0
DisableProgramGroupPage=yes
LicenseFile=
InfoBeforeFile=

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
  GroupDescription: "Additional shortcuts:"

[Files]
; The published tree is a single self-contained EXE plus its portable marker.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifdef ScenarioDir
; Scenarios are large; they are installed beside the EXE rather than embedded in
; it (requirement 56). Omitted entirely when no scenario directory is passed.
Source: "{#ScenarioDir}\*"; DestDir: "{app}\Scenarios"; \
  Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Scenarios folder"; Filename: "{app}\Scenarios"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
  Flags: nowait postinstall skipifsilent
Filename: "{#DriverUrl}"; Description: "Open the PEAK-System driver download page"; \
  Flags: shellexec nowait postinstall skipifsilent unchecked; \
  Check: not PcanBasicInstalled

[Code]
{ Detect the native PCAN-Basic library so the driver prompt is only offered when
  it is actually missing. This mirrors what the application itself checks at
  start-up: the presence of PCANBasic.dll, not a registry key, is what separates
  "driver not installed" from "device not plugged in". }
function PcanBasicInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\PCANBasic.dll')) or
            FileExists(ExpandConstant('{win}\SysWOW64\PCANBasic.dll'));
end;

{ True when at least one scenario folder was installed alongside the player. }
function ScenariosInstalled: Boolean;
var
  Found: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{app}\Scenarios\*'), Found) then
  begin
    try
      repeat
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          if (Found.Name <> '.') and (Found.Name <> '..') then
            if FileExists(ExpandConstant('{app}\Scenarios\') + Found.Name + '\scenario.json') then
            begin
              Result := True;
              Exit;
            end;
      until not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  if not PcanBasicInstalled then
    MsgBox('The PEAK-System PCAN-USB driver was not detected on this computer.' + #13#10#13#10 +
           'CANVideoEmulator will still start and play scenarios in Demo Mode, ' +
           'but it cannot transmit to a PCAN-USB until the driver is installed.' + #13#10#13#10 +
           'Install it from:' + #13#10 + '{#DriverUrl}',
           mbInformation, MB_OK);

  { An install with no scenarios starts to an empty browser, which looks broken
    unless the operator is told where to get them. The download itself is not
    done here: it is around 9 GB, and an installer is the wrong place to sit
    through that. The application has a Get Scenarios button for it. }
  if not ScenariosInstalled then
    MsgBox('No scenarios were installed.' + #13#10#13#10 +
           'The scenario browser will be empty until some are added. To fetch ' +
           'recorded driving data, start CANVideoEmulator and use the ' +
           '"Get Scenarios..." button next to the scenario list.' + #13#10#13#10 +
           'It downloads a chunk of comma.ai''s comma2k19 dataset (about 9 GB) ' +
           'and prints the commands to convert it into scenarios.',
           mbInformation, MB_OK);
end;
