; Built by installer\build.ps1, which publishes the app to installer\publish first.
#define Exe "publish\UsageBar.exe"
#define AppVersion GetVersionNumbersString(Exe)

[Setup]
; Never change AppId: upgrades and uninstall find the installed copy by it.
AppId={{3868A677-6A78-4E23-B137-F427D6881E56}
AppName=UsageBar
AppVersion={#AppVersion}
AppPublisher=Kibeom Kwon
; Per user, as the logon task and %LOCALAPPDATA%\UsageBar are, so no admin prompt.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\UsageBar
DisableProgramGroupPage=yes
UninstallDisplayName=UsageBar
UninstallDisplayIcon={app}\UsageBar.exe
; The app holds this mutex while it runs; setup asks to quit it instead of failing on locked files.
AppMutex=UsageBar.SingleInstance
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=UsageBar-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\UsageBar"; Filename: "{app}\UsageBar.exe"

[Run]
Filename: "{app}\UsageBar.exe"; Description: "{cm:LaunchProgram,UsageBar}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The launch-at-login task would point at a deleted exe. Settings in %LOCALAPPDATA%\UsageBar stay.
Filename: "schtasks.exe"; Parameters: "/Delete /TN UsageBar /F"; Flags: runhidden; RunOnceId: "DeleteLogonTask"
