#define MyAppName "TurulLauncher"
#define MyAppVersion "4.6.0"
#define MyAppPublisher "TurulNetwork"
#define MyAppURL "https://turulnetwork.hu/launcher/"
#define MyAppExeName "TurulMC.Launcher.exe"

[Setup]
AppId={{6D95C43B-707E-4CF8-9BC9-2CDA21B5F5A7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL=https://turulnetwork.hu/
AppUpdatesURL=https://turulnetwork.hu/launcher/
DefaultDirName={localappdata}\Programs\TurulLauncher
DefaultGroupName=TurulLauncher
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\publish\Installer
OutputBaseFilename=TurulLauncher-Setup-{#MyAppVersion}
SetupIconFile=..\src\TurulMC.Launcher\Assets\turullauncher.ico
UninstallDisplayIcon={app}\Assets\turullauncher.ico
WizardStyle=modern dynamic slate includetitlebar
WizardSmallImageFile=..\src\TurulMC.Launcher\Assets\turul-logo.png
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
DisableWelcomePage=no
AllowNoIcons=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=TurulLauncher Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\GUI\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\prereqs\vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\TurulLauncher"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\turullauncher.ico"
Name: "{autodesktop}\TurulLauncher"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\turullauncher.ico"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Microsoft Visual C++ Runtime telepítése..."; Flags: waituntilterminated; Check: NeedVCRedist
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,TurulLauncher}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function NeedVCRedist(): Boolean;
var
  Installed: Cardinal;
begin
  Result := not (RegQueryDWordValue(HKLM64, 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'Installed', Installed) and (Installed = 1));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
end;
