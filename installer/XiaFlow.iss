; XiaFlow (匣流) 安装包脚本 — Inno Setup 6
; 用户级安装（无管理员权限要求），低完整性写入 HKCU

#define MyAppName "XiaFlow"
#define MyAppNameZh "匣流"
#ifndef MyAppVersion
  #error "Build with scripts/package.ps1 to supply MyAppVersion."
#endif
#define MyAppPublisher "XiaFlow"
#define MyAppExeName "XiaFlow.exe"
#define MyAppId "{{8E4F5A1C-6B2D-4E9A-9C71-8A7F10F10000}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName} ({#MyAppNameZh})
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppNameZh} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/yuchen0x1/XiaFlow
AppSupportURL=https://github.com/yuchen0x1/XiaFlow/issues
DefaultDirName={autopf}\XiaFlow
DefaultGroupName={#MyAppName} ({#MyAppNameZh})
UninstallDisplayName={#MyAppName} ({#MyAppNameZh})
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\artifacts\installer
OutputBaseFilename=XiaFlow-{#MyAppVersion}-setup-x64
SetupIconFile=..\src\XiaFlow.App\Assets\XiaFlow.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
DisableProgramGroupPage=yes
ChangesAssociations=no

[INI]
Filename: "{app}\xiaflow-installed.ini"; Section: "XiaFlow"; Key: "Version"; String: "{#MyAppVersion}"

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式(&D)
chinesesimplified.CreateQuickLaunch=创建快速启动栏快捷方式(&Q)
chinesesimplified.ContextMenu=添加 Windows 右键菜单（压缩 / 解压 / 打开）
chinesesimplified.Associate=将常用压缩格式加入「打开方式」候选列表
chinesesimplified.LaunchProgram=启动 %1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "附加图标:"
Name: "contextmenu"; Description: "{cm:ContextMenu}"; GroupDescription: "系统集成:"; Flags: checkedonce
Name: "associate"; Description: "{cm:Associate}"; GroupDescription: "系统集成:"; Flags: checkedonce unchecked

[Files]
Source: "..\artifacts\portable\XiaFlow\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName} ({#MyAppNameZh})"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName} ({#MyAppNameZh})"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: AppUpdate
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName} ({#MyAppNameZh})}"; Flags: nowait postinstall skipifsilent; Check: not AppUpdate

[Registry]
; 右键压缩菜单（用户级 HKCU，无需管理员）
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompress"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompress"; ValueType: string; ValueName: "MUIVerb"; ValueData: "添加到 XiaFlow 压缩包"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompress"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompress"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompress\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --compress ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompressZip"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompressZip"; ValueType: string; ValueName: "MUIVerb"; ValueData: "压缩为 ZIP"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompressZip\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --compress-zip ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompressPwd"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompressPwd"; ValueType: string; ValueName: "MUIVerb"; ValueData: "压缩并设置密码…"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\XiaFlowCompressPwd\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --compress-pwd ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\Directory\shell\XiaFlowCompress"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\XiaFlowCompress"; ValueType: string; ValueName: "MUIVerb"; ValueData: "添加到 XiaFlow 压缩包"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\XiaFlowCompress"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\XiaFlowCompress"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\XiaFlowCompress\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --compress ""%1"""; Tasks: contextmenu

; 压缩格式右键菜单（解压 / 打开 / 测试）— 常用格式
#define ArchiveExtList ".7z .zip .rar .tar .gz .bz2 .xz .zst .iso .wim .cab .cpio .rpm .deb .lz4 .001"
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow"; ValueType: string; ValueName: "MUIVerb"; ValueData: "匣流 XiaFlow"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow"; ValueType: string; ValueName: "SubCommands"; ValueData: ""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\open"; ValueType: string; ValueName: "MUIVerb"; ValueData: "使用匣流打开"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --open ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\extractHere"; ValueType: string; ValueName: "MUIVerb"; ValueData: "解压到当前目录"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\extractHere\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --extract-here ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\extractFolder"; ValueType: string; ValueName: "MUIVerb"; ValueData: "解压到独立文件夹"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\extractFolder\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --extract-to-folder ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\test"; ValueType: string; ValueName: "MUIVerb"; ValueData: "测试压缩包完整性"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\test\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --test ""%1"""; Tasks: contextmenu

Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow"; ValueType: string; ValueName: "MUIVerb"; ValueData: "匣流 XiaFlow"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow"; ValueType: string; ValueName: "SubCommands"; ValueData: ""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\open"; ValueType: string; ValueName: "MUIVerb"; ValueData: "使用匣流打开"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --open ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\extractHere"; ValueType: string; ValueName: "MUIVerb"; ValueData: "解压到当前目录"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\extractHere\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --extract-here ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\extractFolder"; ValueType: string; ValueName: "MUIVerb"; ValueData: "解压到独立文件夹"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\extractFolder\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --extract-to-folder ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\test"; ValueType: string; ValueName: "MUIVerb"; ValueData: "测试压缩包完整性"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\XiaFlow\shell\test\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --test ""%1"""; Tasks: contextmenu

; 文件关联候选（OpenWithProgids — 由用户在系统「打开方式」中确认）
Root: HKCU; Subkey: "Software\Classes\XiaFlow.Archive"; Flags: uninsdeletekey; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\XiaFlow.Archive"; ValueType: string; ValueData: "匣流压缩包"; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\XiaFlow.Archive\DefaultIcon"; ValueType: string; ValueData: "{app}\{#MyAppExeName},0"; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\XiaFlow.Archive\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" --open ""%1"""; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.7z\OpenWithProgids"; ValueType: string; ValueName: "XiaFlow.Archive"; ValueData: ""; Tasks: associate
Root: HKCU; Subkey: "Software\Classes\.zip\OpenWithProgids"; ValueType: string; ValueName: "XiaFlow.Archive"; ValueData: ""; Tasks: associate

[UninstallDelete]
Type: files; Name: "{app}\xiaflow-installed.ini"
; 卸载不删除用户数据（%APPDATA%\XiaFlow 备份 / 设置 / 密码库保留）

[UninstallRun]
; 卸载时不额外执行命令：注册表项经 uninsdeletekey 自动清理；
; 备份计划任务由应用在删除任务时自行删除；用户数据（%APPDATA%\XiaFlow）完整保留。

[Code]
function AppUpdate: Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), '/XIAFLOWUPDATE') = 0 then
      Result := True;
end;
