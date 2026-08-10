#define AppVersion GetEnv("NEXUSP2P_VERSION")
#if AppVersion == ""
  #error NEXUSP2P_VERSION is required (for example: 1.2.0)
#endif

#define RepositoryRoot SourcePath + "\..\.."

[Setup]
AppId={{B457CB19-65D5-46A1-9C3B-24A67B683C2A}
AppName=NexusP2P
AppVersion={#AppVersion}
AppPublisher=NexusP2P
AppPublisherURL=https://github.com/wenluwindy/NexusP2P
AppSupportURL=https://github.com/wenluwindy/NexusP2P/issues
AppUpdatesURL=https://github.com/wenluwindy/NexusP2P/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\NexusP2P
DefaultGroupName=NexusP2P
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#RepositoryRoot}\dist
OutputBaseFilename=NexusP2P-Setup-{#AppVersion}-win-x64
SetupIconFile={#RepositoryRoot}\src\NexusP2P.Desktop\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
AppMutex=Local\NexusP2P.Desktop.SingleInstance
UninstallDisplayIcon={app}\NexusP2P-Desktop.exe

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#RepositoryRoot}\dist\nexusp2p-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NexusP2P"; Filename: "{app}\NexusP2P-Desktop.exe"
Name: "{group}\卸载 NexusP2P"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\NexusP2P-Desktop.exe"; Description: "启动 NexusP2P"; Flags: nowait postinstall skipifsilent
