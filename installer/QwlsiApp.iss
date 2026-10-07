; Inno Setup 6 script - QWLSI single-frame wavefront. Per-user install (no administrator rights).
; Build:  ISCC.exe /DAppVer=1.0.0 /DSrcDir=<publish folder> /DOutDir=<output folder> QwlsiApp.iss   (tools\build-release.ps1 does this)
#ifndef AppVer
  #define AppVer "1.0.0"
#endif
#ifndef SrcDir
  #define SrcDir "..\dist\publish"
#endif
#ifndef OutDir
  #define OutDir "..\dist\release"
#endif

[Setup]
AppId={{6F2B8D1E-4C7A-4B59-9E0D-5A31C7E2F8B4}
AppName=QWLSI single-frame wavefront
AppVersion={#AppVer}
AppVerName=QWLSI single-frame wavefront {#AppVer}
AppPublisher=UH Physiological Optics Lab
DefaultDirName={autopf}\QwlsiApp
DefaultGroupName=QWLSI wavefront
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=QwlsiApp-{#AppVer}-setup
Compression=lzma2/max
SolidCompression=yes
LZMANumBlockThreads=8
WizardStyle=modern
UninstallDisplayName=QWLSI single-frame wavefront {#AppVer}
UninstallDisplayIcon={app}\QwlsiApp.exe
VersionInfoVersion={#AppVer}.0
VersionInfoProductName=QWLSI single-frame wavefront
VersionInfoProductVersion={#AppVer}
VersionInfoCompany=UH Physiological Optics Lab
VersionInfoDescription=QWLSI single-frame wavefront setup
InfoBeforeFile={#SrcDir}\THIRD-PARTY-NOTICES.txt
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SrcDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\QWLSI single-frame wavefront"; Filename: "{app}\QwlsiApp.exe"
Name: "{group}\User guide"; Filename: "{app}\USER-GUIDE.md"
Name: "{group}\Release notes"; Filename: "{app}\RELEASE-NOTES.md"
Name: "{group}\Third-party notices"; Filename: "{app}\THIRD-PARTY-NOTICES.txt"
Name: "{group}\Uninstall QWLSI wavefront"; Filename: "{uninstallexe}"
Name: "{autodesktop}\QWLSI single-frame wavefront"; Filename: "{app}\QwlsiApp.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\QwlsiApp.exe"; Description: "Start QWLSI single-frame wavefront"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; settings and logs live in %APPDATA%\QwlsiApp and are deliberately kept (see the user guide)
Type: filesandordirs; Name: "{app}"
