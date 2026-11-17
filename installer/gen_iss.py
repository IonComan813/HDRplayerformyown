"""
按后缀表重新生成 HdrPlayer.iss 里的 [Tasks]、每个后缀的 [Registry] 条目、[Run] 和 [Code] 段。
要增删后缀：改下面的 EXTS，然后运行  python installer/gen_iss.py installer/HdrPlayer.iss
"""
import sys
p=sys.argv[1]
s=open(p,encoding='utf-8-sig').read().replace('\r\n','\n')

# (后缀, 说明, 默认勾选)
EXTS=[
 ("mkv","Matroska 视频",True), ("mp4","MPEG-4 视频",True), ("m4v","MPEG-4 视频（iTunes）",True),
 ("mov","QuickTime 视频",True), ("webm","WebM 视频",True), ("avi","AVI 视频",True), ("wmv","Windows Media 视频",True),
 ("flv","Flash 视频",True), ("m2ts","蓝光 BDAV 流",True), ("mts","AVCHD 视频",True),
 ("ts","MPEG-TS 传输流（注意：TypeScript 源文件也用 .ts，写代码的话别勾）",False),
 ("mpg","MPEG-1/2 视频",True), ("mpeg","MPEG-1/2 视频",True),
 ("hevc","H.265 裸流",True), ("h265","H.265 裸流",True), ("265","H.265 裸流",True), ("264","H.264 裸流",True),
 ("m2t","HDV / MPEG-TS",False), ("3gp","3GP 手机视频",False), ("3g2","3GPP2 视频",False), ("ogv","Ogg 视频",False),
 ("vob","DVD 视频",False), ("rmvb","RealMedia 视频",False), ("rm","RealMedia 视频",False), ("asf","ASF 视频",False),
 ("f4v","Flash MP4 视频",False), ("divx","DivX 视频",False), ("mxf","MXF 专业视频",False), ("y4m","YUV4MPEG 原始视频",False),
]

def section(name):
    """返回段落 [name] 的起止位置（按行首匹配，不会误中注释里的文字）。"""
    import re
    m=re.search(r'(?m)^\['+name+r'\]$', s)
    assert m, name
    n=re.search(r'(?m)^\[[A-Za-z]+\]$', s[m.end():])
    return m.start(), (m.end()+n.start() if n else len(s))

tasks='''[Tasks]
; 每个后缀一个勾选框：勾选的设为用 HdrPlayer 打开；不勾的也会出现在右键“打开方式”里
Name: "assoc"; Description: "设为以下文件的默认播放器（会替换你之前在 Windows 里为它们选的程序）"; GroupDescription: "文件关联（不勾选的后缀也会出现在右键“打开方式”里）："
'''
for e,d,on in EXTS:
    tasks+=f'Name: "assoc\\x{e}"; Description: ".{e}　{d}"{"" if on else "; Flags: unchecked"}\n'
tasks+='Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked\n\n'
i,j=section('Tasks'); s=s[:i]+tasks+s[j:]

i,j=section('Registry')
reg=s[i:j]
k=reg.index('; ---- 每个后缀 ----')
reg=reg[:k]+'''; ---- 每个后缀 ----
; 所有后缀：OpenWithProgids / SupportedTypes / Capabilities 让 HdrPlayer 出现在“打开方式”和“默认应用”里。
; 勾选的后缀：再把后缀的默认 ProgID 写成 HdrPlayer，并在安装后删掉 Windows 记录的旧选择（UserChoice，见下面的代码段），
; 否则 Windows 会继续用用户之前选的程序（只写默认值时“没绑定上”就是这个原因）。
'''
for e,d,on in EXTS:
    reg+=f'''; .{e}
Root: HKCU; Subkey: "Software\\Classes\\.{e}\\OpenWithProgids"; ValueType: string; ValueName: "{{#ProgId}}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\\Classes\\Applications\\{{#AppExe}}\\SupportedTypes"; ValueType: string; ValueName: ".{e}"; ValueData: ""
Root: HKCU; Subkey: "Software\\{{#AppName}}\\Capabilities\\FileAssociations"; ValueType: string; ValueName: ".{e}"; ValueData: "{{#ProgId}}"
Root: HKCU; Subkey: "Software\\Classes\\.{e}"; ValueType: string; ValueName: ""; ValueData: "{{#ProgId}}"; Flags: uninsdeletevalue; Tasks: assoc\\x{e}
'''
s=s[:i]+reg+'\n'+s[j:]

run='''[Run]
Filename: "{app}\\{#AppExe}"; Description: "启动 HdrPlayer"; Flags: nowait postinstall skipifsilent
; 可选：打开 Windows“默认应用”里 HdrPlayer 的页面检查结果（Windows 11 直接跳到该页面，旧版本打开默认应用总页面）
Filename: "ms-settings:defaultapps?registeredAppUser={#AppName}"; Description: "打开 Windows“默认应用”设置检查文件关联"; Flags: shellexec nowait postinstall skipifsilent unchecked

'''
i,j=section('Run'); s=s[:i]+run+s[j:]

ext_array=', '.join(f"'{e}'" for e,_,_ in EXTS)
code='''[Code]
const
  FileExtsKey = 'Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.';

// HdrPlayer 是依赖框架的程序，需要 .NET 8 桌面运行时（x64）。没装的话装完提示去下载。
function HasDesktopRuntime8: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\\dotnet\\shared\\Microsoft.WindowsDesktop.App\\8.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

// 删掉勾选后缀在 Windows 里记录的“用户选择”。程序不能写入这个键（有系统校验），但可以删除；
// 删除后 Windows 按 Software\\Classes\\.后缀 的默认值（上面写成了 HdrPlayer）打开文件。
procedure ApplyChosenAssociations(const Exts: array of String);
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(Exts) - 1 do
    if WizardIsTaskSelected('assoc\\x' + Exts[I]) then
    begin
      RegDeleteKeyIncludingSubkeys(HKCU, FileExtsKey + Exts[I] + '\\UserChoice');
      RegDeleteKeyIncludingSubkeys(HKCU, FileExtsKey + Exts[I] + '\\UserChoiceLatest');
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    ApplyChosenAssociations([EXTLIST]);
    if (not WizardSilent) and (not HasDesktopRuntime8) then
      if MsgBox('没有检测到 .NET 8 桌面运行时（x64），HdrPlayer 需要它才能运行。' + #13#10 + #13#10 +
                '现在打开下载页面吗？（选择“.NET Desktop Runtime 8.0 - Windows x64”）',
                mbConfirmation, MB_YESNO) = IDYES then
        ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrCode);
  end;
end;
'''.replace('EXTLIST', ext_array)
i,j=section('Code'); s=s[:i]+code
open(p,'w',encoding='utf-8-sig',newline='\r\n').write(s)
