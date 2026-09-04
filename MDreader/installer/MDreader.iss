; Inno Setup script for MDreader.
; Builds a single MDreaderSetup.exe from the self-contained dotnet publish output.
;
; Prerequisite (run once before compiling):
;   dotnet publish MDreader\MDreader.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o MDreader\publish\win-x64
;
; Compile with:
;   "C:\Users\Caleb\AppData\Local\Programs\Inno Setup 6\ISCC.exe" MDreader\installer\MDreader.iss

#define MyAppName "MDreader"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "MDreader"
#define MyAppExeName "MDreader.exe"
#define MyPublishDir "..\publish\win-x64"

[Setup]
AppId={{B6F2B8D1-6E3D-4C7B-9A2C-4C2A6B9E7A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
PrivilegesRequiredOverridesAllowed=commandline dialog
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename=MDreaderSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
SetupIconFile=..\Assets\AppIcon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "*.WebView2\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Registers MDreader as a candidate in the Windows "Open with" list/dialog for .md
; and .markdown files. Written under HKCU so it applies without extra elevation and
; doesn't force MDreader as the default handler - the user still picks that themselves.
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#MyAppExeName}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.md\OpenWithList\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\.markdown\OpenWithList\{#MyAppExeName}"; ValueType: string; ValueName: ""; ValueData: ""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
