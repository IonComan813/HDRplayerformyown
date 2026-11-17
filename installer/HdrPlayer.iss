; HdrPlayer 安装程序（Inno Setup 6.5+）
; 编译：ISCC.exe /DAppVersion=0.3.0 /DSourceDir=..\publish /DOutputDir=..\dist installer\HdrPlayer.iss
;
; 按用户安装（不需要管理员权限），注册到“打开方式”和 Windows“默认应用”设置。
; Windows 8 以后程序不能自己把某个后缀的默认程序改成自己（用户的选择有系统校验），
; 所以安装完成时可以选择打开“默认应用”设置页，由用户点一下确认。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "HdrPlayer"
#define AppExe "HdrPlayer.exe"
#define ProgId "HdrPlayer.Video"

[Setup]
AppId={{6F0B8F5E-2C1D-4C7A-9B3E-5D2A8E4F1C90}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=..\Assets\HdrPlayer.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesAssociations=yes
CloseApplications=yes

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
; 每个后缀一个勾选框：勾选的设为用 HdrPlayer 打开；不勾的也会出现在右键“打开方式”里
Name: "assoc"; Description: "设为以下文件的默认播放器（会替换你之前在 Windows 里为它们选的程序）"; GroupDescription: "文件关联（不勾选的后缀也会出现在右键“打开方式”里）："
Name: "assoc\xmkv"; Description: ".mkv　Matroska 视频"
Name: "assoc\xmp4"; Description: ".mp4　MPEG-4 视频"
Name: "assoc\xm4v"; Description: ".m4v　MPEG-4 视频（iTunes）"
Name: "assoc\xmov"; Description: ".mov　QuickTime 视频"
Name: "assoc\xwebm"; Description: ".webm　WebM 视频"
Name: "assoc\xavi"; Description: ".avi　AVI 视频"
Name: "assoc\xwmv"; Description: ".wmv　Windows Media 视频"
Name: "assoc\xflv"; Description: ".flv　Flash 视频"
Name: "assoc\xm2ts"; Description: ".m2ts　蓝光 BDAV 流"
Name: "assoc\xmts"; Description: ".mts　AVCHD 视频"
Name: "assoc\xts"; Description: ".ts　MPEG-TS 传输流（注意：TypeScript 源文件也用 .ts，写代码的话别勾）"; Flags: unchecked
Name: "assoc\xmpg"; Description: ".mpg　MPEG-1/2 视频"
Name: "assoc\xmpeg"; Description: ".mpeg　MPEG-1/2 视频"
Name: "assoc\xhevc"; Description: ".hevc　H.265 裸流"
Name: "assoc\xh265"; Description: ".h265　H.265 裸流"
Name: "assoc\x265"; Description: ".265　H.265 裸流"
Name: "assoc\x264"; Description: ".264　H.264 裸流"
Name: "assoc\xm2t"; Description: ".m2t　HDV / MPEG-TS"; Flags: unchecked
Name: "assoc\x3gp"; Description: ".3gp　3GP 手机视频"; Flags: unchecked
Name: "assoc\x3g2"; Description: ".3g2　3GPP2 视频"; Flags: unchecked
Name: "assoc\xogv"; Description: ".ogv　Ogg 视频"; Flags: unchecked
Name: "assoc\xvob"; Description: ".vob　DVD 视频"; Flags: unchecked
Name: "assoc\xrmvb"; Description: ".rmvb　RealMedia 视频"; Flags: unchecked
Name: "assoc\xrm"; Description: ".rm　RealMedia 视频"; Flags: unchecked
Name: "assoc\xasf"; Description: ".asf　ASF 视频"; Flags: unchecked
Name: "assoc\xf4v"; Description: ".f4v　Flash MP4 视频"; Flags: unchecked
Name: "assoc\xdivx"; Description: ".divx　DivX 视频"; Flags: unchecked
Name: "assoc\xmxf"; Description: ".mxf　MXF 专业视频"; Flags: unchecked
Name: "assoc\xy4m"; Description: ".y4m　YUV4MPEG 原始视频"; Flags: unchecked
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; ---- ProgID：图标和打开命令 ----
Root: HKCU; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "视频文件 (HdrPlayer)"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"
Root: HKCU; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

; ---- Applications\HdrPlayer.exe：出现在“打开方式”列表里 ----
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

; ---- “默认应用”设置里的 HdrPlayer 条目 ----
Root: HKCU; Subkey: "Software\{#AppName}"; Flags: uninsdeletekeyifempty
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "HDR 视频播放器（基于 mpv）"
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue

; ---- 每个后缀 ----
; 所有后缀：OpenWithProgids / SupportedTypes / Capabilities 让 HdrPlayer 出现在“打开方式”和“默认应用”里。
; 勾选的后缀：再把后缀的默认 ProgID 写成 HdrPlayer，并在安装后删掉 Windows 记录的旧选择（UserChoice，见下面的代码段），
; 否则 Windows 会继续用用户之前选的程序（只写默认值时“没绑定上”就是这个原因）。
; .mkv
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.mkv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmkv
; .mp4
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.mp4"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmp4
; .m4v
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.m4v"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xm4v
; .mov
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.mov"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmov
; .webm
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.webm"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xwebm
; .avi
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.avi"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xavi
; .wmv
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.wmv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xwmv
; .flv
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.flv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xflv
; .m2ts
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.m2ts"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xm2ts
; .mts
Root: HKCU; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mts"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mts"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.mts"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmts
; .ts
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.ts"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xts
; .mpg
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mpg"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.mpg"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmpg
; .mpeg
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mpeg"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.mpeg"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmpeg
; .hevc
Root: HKCU; Subkey: "Software\Classes\.hevc\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".hevc"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".hevc"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.hevc"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xhevc
; .h265
Root: HKCU; Subkey: "Software\Classes\.h265\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".h265"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".h265"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.h265"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xh265
; .265
Root: HKCU; Subkey: "Software\Classes\.265\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".265"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".265"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.265"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x265
; .264
Root: HKCU; Subkey: "Software\Classes\.264\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".264"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".264"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.264"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x264
; .m2t
Root: HKCU; Subkey: "Software\Classes\.m2t\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m2t"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2t"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.m2t"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xm2t
; .3gp
Root: HKCU; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".3gp"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".3gp"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.3gp"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x3gp
; .3g2
Root: HKCU; Subkey: "Software\Classes\.3g2\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".3g2"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".3g2"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.3g2"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x3g2
; .ogv
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.ogv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xogv
; .vob
Root: HKCU; Subkey: "Software\Classes\.vob\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".vob"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".vob"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.vob"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xvob
; .rmvb
Root: HKCU; Subkey: "Software\Classes\.rmvb\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".rmvb"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".rmvb"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.rmvb"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xrmvb
; .rm
Root: HKCU; Subkey: "Software\Classes\.rm\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".rm"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".rm"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.rm"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xrm
; .asf
Root: HKCU; Subkey: "Software\Classes\.asf\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".asf"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".asf"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.asf"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xasf
; .f4v
Root: HKCU; Subkey: "Software\Classes\.f4v\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".f4v"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".f4v"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.f4v"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xf4v
; .divx
Root: HKCU; Subkey: "Software\Classes\.divx\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".divx"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".divx"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.divx"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xdivx
; .mxf
Root: HKCU; Subkey: "Software\Classes\.mxf\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mxf"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mxf"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.mxf"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmxf
; .y4m
Root: HKCU; Subkey: "Software\Classes\.y4m\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".y4m"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".y4m"; ValueData: "{#ProgId}"
Root: HKCU; Subkey: "Software\Classes\.y4m"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xy4m

[Run]
Filename: "{app}\{#AppExe}"; Description: "启动 HdrPlayer"; Flags: nowait postinstall skipifsilent
; 可选：打开 Windows“默认应用”里 HdrPlayer 的页面检查结果（Windows 11 直接跳到该页面，旧版本打开默认应用总页面）
Filename: "ms-settings:defaultapps?registeredAppUser={#AppName}"; Description: "打开 Windows“默认应用”设置检查文件关联"; Flags: shellexec nowait postinstall skipifsilent unchecked

[Code]
const
  FileExtsKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.';

// HdrPlayer 是依赖框架的程序，需要 .NET 8 桌面运行时（x64）。没装的话装完提示去下载。
function HasDesktopRuntime8: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

// 删掉勾选后缀在 Windows 里记录的“用户选择”。程序不能写入这个键（有系统校验），但可以删除；
// 删除后 Windows 按 Software\Classes\.后缀 的默认值（上面写成了 HdrPlayer）打开文件。
procedure ApplyChosenAssociations(const Exts: array of String);
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(Exts) - 1 do
    if WizardIsTaskSelected('assoc\x' + Exts[I]) then
    begin
      RegDeleteKeyIncludingSubkeys(HKCU, FileExtsKey + Exts[I] + '\UserChoice');
      RegDeleteKeyIncludingSubkeys(HKCU, FileExtsKey + Exts[I] + '\UserChoiceLatest');
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    ApplyChosenAssociations(['mkv', 'mp4', 'm4v', 'mov', 'webm', 'avi', 'wmv', 'flv', 'm2ts', 'mts', 'ts', 'mpg', 'mpeg', 'hevc', 'h265', '265', '264', 'm2t', '3gp', '3g2', 'ogv', 'vob', 'rmvb', 'rm', 'asf', 'f4v', 'divx', 'mxf', 'y4m']);
    if (not WizardSilent) and (not HasDesktopRuntime8) then
      if MsgBox('没有检测到 .NET 8 桌面运行时（x64），HdrPlayer 需要它才能运行。' + #13#10 + #13#10 +
                '现在打开下载页面吗？（选择“.NET Desktop Runtime 8.0 - Windows x64”）',
                mbConfirmation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
  end;
end;
