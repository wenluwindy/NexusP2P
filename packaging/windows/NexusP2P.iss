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
;
; 简体中文不在 Inno Setup 的安装包里 —— 它是官方站点上的「用户贡献翻译」，
; 只存在于源码仓库的 Files/Languages/Unofficial/ 下，装完 Inno Setup 后
; compiler:Languages\ 里**没有**这个文件。写成 compiler: 前缀会在编译时报
;   Couldn't open include file "...\Languages\ChineseSimplified.isl"
; 而这在开发机上可能碰巧不报（如果谁手动拷过一份），只在干净的 CI 上炸。
;
; 所以把它随仓库一起带上，并用相对路径引用：编译不依赖网络，也不依赖
; 目标机器装的 Inno Setup 里恰好有什么。文件取自 jrsoftware/issrc 的
; is-6_7_3 标签。
Name: "chinesesimp"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"

[Files]
Source: "{#RepositoryRoot}\dist\nexusp2p-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\NexusP2P"; Filename: "{app}\NexusP2P-Desktop.exe"
Name: "{group}\卸载 NexusP2P"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\NexusP2P-Desktop.exe"; Description: "启动 NexusP2P"; Flags: nowait postinstall skipifsilent
