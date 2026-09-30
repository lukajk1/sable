; Sable installer (Inno Setup 6). Build it with installer/build.py, which publishes Sable self-contained into
; installer/publish and passes the version from Sable.csproj. Compiling this file directly needs that publish folder.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif
#define AppName      "Sable"
#define AppPublisher "Luka"
#define AppExeName   "Sable.exe"
#define AppUrl       "https://github.com/lukajk1/sable"
#define ProgId       "Sable.Model"

[Setup]
AppId={{AF9EFD93-72DA-4AF7-9FE6-65043A096112}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}
; Per-user by default (no admin prompt; lands in %LOCALAPPDATA%\Programs), with the option to install for everyone.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=SableSetup-{#AppVersion}
SetupIconFile=..\assets\icon\sable.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Replacing the files of a running Sable would fail; ask to close it instead.
CloseApplications=yes
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "openwith"; Description: "Add Sable to ""Open with"" for model files (.fbx, .glb, .gltf, .obj, .blend, .dae, .3ds, .ply)"; GroupDescription: "File types:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; "Open with Sable" for model files, without taking over what they open with by default. HKA is the current user
; for a per-user install and the machine for an all-users one.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "3D model"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: """{app}\{#AppExeName}"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: openwith
#define Ext(E) \
  'Root: HKA; Subkey: "Software\Classes\' + E + '\OpenWithProgids"; ValueType: string; ValueName: "' + ProgId + '"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith' + NewLine + \
  'Root: HKA; Subkey: "Software\Classes\Applications\' + AppExeName + '\SupportedTypes"; ValueType: string; ValueName: "' + E + '"; ValueData: ""; Tasks: openwith' + NewLine
#emit Ext(".fbx")
#emit Ext(".glb")
#emit Ext(".gltf")
#emit Ext(".obj")
#emit Ext(".blend")
#emit Ext(".dae")
#emit Ext(".3ds")
#emit Ext(".ply")

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
