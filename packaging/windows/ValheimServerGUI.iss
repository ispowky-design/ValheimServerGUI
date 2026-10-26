; Inno Setup script for the Windows installer (see .github/workflows/build.yml).
;
; Installs the self-contained, single-file ValheimServerGUI.exe for the current user (no admin prompt),
; with a Start menu entry, an optional desktop shortcut and an uninstaller. The app keeps its profiles,
; preferences and logs in the user's profile, never in the install folder, so neither an update nor an
; uninstall touches them.
;
; Build (on Windows, with Inno Setup 6):
; ISCC.exe /DAppVersion=3.1.0 /DFileVersion=3.1.0 /DSourceExe=path\to\ValheimServerGUI.exe /DOutputDir=dist packaging\windows\ValheimServerGUI.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Windows file versions are numbers only, so a pre-release like 3.1.0-rc.1 passes /DFileVersion=3.1.0.
#ifndef FileVersion
  #define FileVersion "0.0.0"
#endif
#ifndef SourceExe
  #error Pass /DSourceExe=<path to the published ValheimServerGUI.exe>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "ValheimServerGUI"
#define AppExeName "ValheimServerGUI.exe"
#define AppPublisher "ispowky-design"
#define AppUrl "https://github.com/ispowky-design/ValheimServerGUI"

[Setup]
; Never change AppId: Windows uses it to recognise an installed copy, so an update installs over it.
AppId={{6D4F2B7E-3C1A-4E8B-9A57-0B1E2C9F4A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no UAC prompt); the user can still choose "all users" on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\..\LICENSE
SetupIconFile=..\..\src\ValheimServerGUI.App\Assets\ApplicationIcon.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; An update closes a running copy of the app first (only a copy started from this install folder).
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#FileVersion}
VersionInfoProductName={#AppName}

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "{#AppExeName}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; The app's own updater runs this installer silently with /relaunch=1 and exits; start the new version when done.
Filename: "{app}\{#AppExeName}"; Flags: nowait runasoriginaluser; Check: ShouldRelaunch

[Code]
function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:relaunch|0}') = '1');
end;
