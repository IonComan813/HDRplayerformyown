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
Name: "assoc"; Description: "用 HdrPlayer 作为以下文件的默认播放器（安装完成时可一键设置）"; GroupDescription: "文件关联（不勾选的后缀也会出现在右键“打开方式”里）："
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

[InstallDelete]
; tools 里是打包好的 ffsubsync（Python）和 ffmpeg，升级时先整个删掉，免得留下旧版本的文件
Type: filesandordirs; Name: "{app}\tools"

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
; 所有后缀：OpenWithProgids / SupportedTypes 让 HdrPlayer 出现在右键“打开方式”里。
; 勾选的后缀：写进 Capabilities\FileAssociations（Windows“默认应用”里 HdrPlayer 页面列出的类型），并写入后缀的默认 ProgID。
; Windows 10/11 不允许程序自己改默认程序（用户的选择带系统校验，新版 Windows 11 还有驱动保护），
; 所以由用户在 HdrPlayer 的“默认应用”页面点“设为默认值”确认：这个按钮只作用于 Capabilities 里列出的类型，正好就是勾选的这些。
; .mkv
Root: HKCU; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "{#ProgId}"; Tasks: assoc\xmkv
Root: HKCU; Subkey: "Software\Classes\.mkv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmkv
; .mp4
Root: HKCU; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "{#ProgId}"; Tasks: assoc\xmp4
Root: HKCU; Subkey: "Software\Classes\.mp4"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmp4
; .m4v
Root: HKCU; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "{#ProgId}"; Tasks: assoc\xm4v
Root: HKCU; Subkey: "Software\Classes\.m4v"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xm4v
; .mov
Root: HKCU; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "{#ProgId}"; Tasks: assoc\xmov
Root: HKCU; Subkey: "Software\Classes\.mov"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmov
; .webm
Root: HKCU; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "{#ProgId}"; Tasks: assoc\xwebm
Root: HKCU; Subkey: "Software\Classes\.webm"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xwebm
; .avi
Root: HKCU; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "{#ProgId}"; Tasks: assoc\xavi
Root: HKCU; Subkey: "Software\Classes\.avi"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xavi
; .wmv
Root: HKCU; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "{#ProgId}"; Tasks: assoc\xwmv
Root: HKCU; Subkey: "Software\Classes\.wmv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xwmv
; .flv
Root: HKCU; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "{#ProgId}"; Tasks: assoc\xflv
Root: HKCU; Subkey: "Software\Classes\.flv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xflv
; .m2ts
Root: HKCU; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "{#ProgId}"; Tasks: assoc\xm2ts
Root: HKCU; Subkey: "Software\Classes\.m2ts"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xm2ts
; .mts
Root: HKCU; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mts"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mts"; ValueData: "{#ProgId}"; Tasks: assoc\xmts
Root: HKCU; Subkey: "Software\Classes\.mts"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmts
; .ts
Root: HKCU; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "{#ProgId}"; Tasks: assoc\xts
Root: HKCU; Subkey: "Software\Classes\.ts"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xts
; .mpg
Root: HKCU; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mpg"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg"; ValueData: "{#ProgId}"; Tasks: assoc\xmpg
Root: HKCU; Subkey: "Software\Classes\.mpg"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmpg
; .mpeg
Root: HKCU; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mpeg"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "{#ProgId}"; Tasks: assoc\xmpeg
Root: HKCU; Subkey: "Software\Classes\.mpeg"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmpeg
; .hevc
Root: HKCU; Subkey: "Software\Classes\.hevc\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".hevc"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".hevc"; ValueData: "{#ProgId}"; Tasks: assoc\xhevc
Root: HKCU; Subkey: "Software\Classes\.hevc"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xhevc
; .h265
Root: HKCU; Subkey: "Software\Classes\.h265\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".h265"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".h265"; ValueData: "{#ProgId}"; Tasks: assoc\xh265
Root: HKCU; Subkey: "Software\Classes\.h265"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xh265
; .265
Root: HKCU; Subkey: "Software\Classes\.265\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".265"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".265"; ValueData: "{#ProgId}"; Tasks: assoc\x265
Root: HKCU; Subkey: "Software\Classes\.265"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x265
; .264
Root: HKCU; Subkey: "Software\Classes\.264\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".264"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".264"; ValueData: "{#ProgId}"; Tasks: assoc\x264
Root: HKCU; Subkey: "Software\Classes\.264"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x264
; .m2t
Root: HKCU; Subkey: "Software\Classes\.m2t\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m2t"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2t"; ValueData: "{#ProgId}"; Tasks: assoc\xm2t
Root: HKCU; Subkey: "Software\Classes\.m2t"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xm2t
; .3gp
Root: HKCU; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".3gp"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".3gp"; ValueData: "{#ProgId}"; Tasks: assoc\x3gp
Root: HKCU; Subkey: "Software\Classes\.3gp"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x3gp
; .3g2
Root: HKCU; Subkey: "Software\Classes\.3g2\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".3g2"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".3g2"; ValueData: "{#ProgId}"; Tasks: assoc\x3g2
Root: HKCU; Subkey: "Software\Classes\.3g2"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\x3g2
; .ogv
Root: HKCU; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "{#ProgId}"; Tasks: assoc\xogv
Root: HKCU; Subkey: "Software\Classes\.ogv"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xogv
; .vob
Root: HKCU; Subkey: "Software\Classes\.vob\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".vob"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".vob"; ValueData: "{#ProgId}"; Tasks: assoc\xvob
Root: HKCU; Subkey: "Software\Classes\.vob"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xvob
; .rmvb
Root: HKCU; Subkey: "Software\Classes\.rmvb\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".rmvb"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".rmvb"; ValueData: "{#ProgId}"; Tasks: assoc\xrmvb
Root: HKCU; Subkey: "Software\Classes\.rmvb"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xrmvb
; .rm
Root: HKCU; Subkey: "Software\Classes\.rm\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".rm"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".rm"; ValueData: "{#ProgId}"; Tasks: assoc\xrm
Root: HKCU; Subkey: "Software\Classes\.rm"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xrm
; .asf
Root: HKCU; Subkey: "Software\Classes\.asf\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".asf"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".asf"; ValueData: "{#ProgId}"; Tasks: assoc\xasf
Root: HKCU; Subkey: "Software\Classes\.asf"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xasf
; .f4v
Root: HKCU; Subkey: "Software\Classes\.f4v\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".f4v"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".f4v"; ValueData: "{#ProgId}"; Tasks: assoc\xf4v
Root: HKCU; Subkey: "Software\Classes\.f4v"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xf4v
; .divx
Root: HKCU; Subkey: "Software\Classes\.divx\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".divx"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".divx"; ValueData: "{#ProgId}"; Tasks: assoc\xdivx
Root: HKCU; Subkey: "Software\Classes\.divx"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xdivx
; .mxf
Root: HKCU; Subkey: "Software\Classes\.mxf\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mxf"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mxf"; ValueData: "{#ProgId}"; Tasks: assoc\xmxf
Root: HKCU; Subkey: "Software\Classes\.mxf"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xmxf
; .y4m
Root: HKCU; Subkey: "Software\Classes\.y4m\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".y4m"; ValueData: ""
Root: HKCU; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".y4m"; ValueData: "{#ProgId}"; Tasks: assoc\xy4m
Root: HKCU; Subkey: "Software\Classes\.y4m"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoc\xy4m

[Run]
; 程序内“检查更新”静默安装（/UPDATE=1）：装完自动重新启动 HdrPlayer。非 postinstall 的条目在静默安装时也会运行
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: IsUpdate
Filename: "{app}\{#AppExe}"; Description: "启动 HdrPlayer"; Flags: nowait postinstall skipifsilent
; 一键设为默认：以管理员身份运行 HdrPlayer 写 Windows 的“默认关联配置文件”策略（微软给企业管理员的正规机制，
; 由 Windows 在每次登录时应用；注销重新登录后生效）。详见 DefaultApps.cs
Filename: "{app}\{#AppExe}"; Parameters: "--apply-default-assoc ""{code:SelectedExts}"""; Description: "一键设为勾选后缀的默认播放器（需要管理员权限，注销重新登录后生效）"; Verb: "runas"; Flags: shellexec waituntilterminated postinstall skipifsilent; Check: AnyAssocSelected
; 手动方式：打开 Windows“默认应用”里 HdrPlayer 的页面（Windows 10 打开默认应用总页面）
Filename: "ms-settings:defaultapps?registeredAppUser={#AppName}"; Description: "打开 Windows“默认应用”设置（手动设置）"; Flags: shellexec nowait postinstall skipifsilent unchecked; Check: AnyAssocSelected

[Code]
const
  AssocExts = 'mkv mp4 m4v mov webm avi wmv flv m2ts mts ts mpg mpeg hevc h265 265 264 m2t 3gp 3g2 ogv vob rmvb rm asf f4v divx mxf y4m';

// HdrPlayer 是依赖框架的程序，需要 .NET 8 桌面运行时（x64）。没装的话装完提示去下载。
function HasDesktopRuntime8: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

// 是否勾选了任何一个要设为默认的后缀（AssocExts 是以空格分隔的后缀列表）
function AnyAssocSelected: Boolean;
var
  Rest, Ext: String;
  P: Integer;
begin
  Result := False;
  Rest := AssocExts + ' ';
  while (not Result) and (Length(Rest) > 0) do
  begin
    P := Pos(' ', Rest);
    Ext := Copy(Rest, 1, P - 1);
    Delete(Rest, 1, P);
    if (Ext <> '') and WizardIsTaskSelected('assoc\x' + Ext) then
      Result := True;
  end;
end;

// 由 HdrPlayer 的“检查更新”启动：/VERYSILENT /UPDATE=1
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

// 自动更新时 HdrPlayer 刚开始退出（保存进度、关闭 mpv），等它放开单实例互斥体再装，最多等 30 秒
function InitializeSetup: Boolean;
var
  I: Integer;
begin
  if IsUpdate then
  begin
    I := 0;
    while CheckForMutexes('Local\HdrPlayer.SingleInstance') and (I < 120) do
    begin
      Sleep(250);
      I := I + 1;
    end;
    // 互斥体放开后进程还要一点时间才完全结束、释放 exe / dll 文件
    Sleep(1500);
  end;
  Result := True;
end;

// 勾选的后缀，逗号分隔（给 --apply-default-assoc）
function SelectedExts(Param: String): String;
var
  Rest, Ext: String;
  P: Integer;
begin
  Result := '';
  Rest := AssocExts + ' ';
  while Length(Rest) > 0 do
  begin
    P := Pos(' ', Rest);
    Ext := Copy(Rest, 1, P - 1);
    Delete(Rest, 1, P);
    if (Ext <> '') and WizardIsTaskSelected('assoc\x' + Ext) then
    begin
      if Result <> '' then Result := Result + ',';
      Result := Result + '.' + Ext;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and AnyAssocSelected then
  begin
    WizardForm.FinishedLabel.Caption :=
      'HdrPlayer 已安装。' + #13#10 + #13#10 +
      '勾选下面的“一键设为默认播放器”并点“完成”，确认管理员权限后，注销并重新登录（或重启），' +
      '刚才勾选的视频后缀就都会用 HdrPlayer 打开。之后可在 HdrPlayer 的 全局设置 → 操作 里取消。' + #13#10 + #13#10 +
      '（Windows 家庭版官方不支持这种方式，可改用“打开 Windows 默认应用设置”手动设置。）';
    // 文字变长了：让标签按内容增高，并把下面的勾选列表往下挪，免得被挡住
    WizardForm.FinishedLabel.AdjustHeight;
    WizardForm.RunList.Top := WizardForm.FinishedLabel.Top + WizardForm.FinishedLabel.Height + ScaleY(12);
  end;
end;

// 卸载时撤销“默认关联配置文件”策略（只在它指向 HdrPlayer 写的文件时；需要管理员权限，会弹 UAC）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cur: String;
  ErrCode: Integer;
begin
  if (CurUninstallStep = usUninstall) and
     RegQueryStringValue(HKLM, 'SOFTWARE\Policies\Microsoft\Windows\System', 'DefaultAssociationsConfiguration', Cur) and
     (CompareText(Cur, ExpandConstant('{commonappdata}\HdrPlayer\DefaultAssociations.xml')) = 0) then
    ShellExec('runas', ExpandConstant('{app}\{#AppExe}'), '--remove-default-assoc', '', SW_HIDE, ewWaitUntilTerminated, ErrCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not HasDesktopRuntime8) then
    if MsgBox('没有检测到 .NET 8 桌面运行时（x64），HdrPlayer 需要它才能运行。' + #13#10 + #13#10 +
              '现在打开下载页面吗？（选择“.NET Desktop Runtime 8.0 - Windows x64”）',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
end;
