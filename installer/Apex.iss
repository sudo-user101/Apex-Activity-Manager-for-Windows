; Apex Activity Manager for Windows — installer (Inno Setup 6).
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{A9E1C3F2-5B7D-4E2A-9C61-0D3B7F8E4A21}
AppName=Apex Activity Manager
AppVersion={#AppVersion}
AppVerName=Apex Activity Manager {#AppVersion}
AppPublisher=CyberNet Labs
AppCopyright=© 2026 CyberNet Labs
DefaultDirName={autopf}\Apex Activity Manager
DefaultGroupName=Apex Activity Manager
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Apex-Activity-Manager-{#AppVersion}-Setup-x64
SetupIconFile=..\src\Apex.Windows\Assets\Apex.ico
UninstallDisplayIcon={app}\Apex.exe
UninstallDisplayName=Apex Activity Manager
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\publish\x64\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Apex Activity Manager"; Filename: "{app}\Apex.exe"
Name: "{autodesktop}\Apex Activity Manager"; Filename: "{app}\Apex.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Apex.exe"; Description: "Open Apex Activity Manager"; Flags: nowait postinstall skipifsilent
