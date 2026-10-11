; SPDX-License-Identifier: AGPL-3.0-or-later
; Inno Setup script. Packages a release layout (the content of an AppleRemote-<version>-<rid>.zip):
;   iscc /DAppVersion=1.1.0 /DSourceDir=C:\path\to\unzipped /DArch=x64 installer\AppleRemote.iss
; Arch: x64 (default) or arm64. The kernel filter ("driver" subfolder) only exists for x64.

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\Publish"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{6F0B7C58-3D0E-4C55-9A2A-5B1F7E0D4A21}
AppName=Apple Remote
AppVersion={#AppVersion}
AppPublisher=AnthoDingo
DefaultDirName={autopf}\AppleRemote
DefaultGroupName=Apple Remote
UninstallDisplayIcon={app}\AppleRemote.exe
OutputDir=..\Output
OutputBaseFilename=AppleRemote-{#AppVersion}-win-{#Arch}-setup
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed={#Arch}compatible
ArchitecturesInstallIn64BitMode={#Arch}compatible
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "driver\*"; Flags: ignoreversion recursesubdirs
Source: "{#SourceDir}\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\keymap.example.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Apple Remote"; Filename: "{app}\AppleRemote.exe"
Name: "{autodesktop}\Apple Remote"; Filename: "{app}\AppleRemote.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AppleRemote.exe"; Description: "{cm:LaunchProgram,Apple Remote}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the kernel filter if it was installed from the app (no-op otherwise).
Filename: "{app}\AppleRemote.DriverInstaller.exe"; Parameters: "uninstall"; Flags: runhidden skipifdoesntexist; RunOnceId: "RemoveDriver"
