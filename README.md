# HdrPlayer

基于 libmpv 的本地视频播放器，界面用 WPF + [WPF-UI](https://github.com/lepoco/wpfui)（Fluent 风格，深色 + Mica），只针对一台显示器调校：ViewSonic VX2781-4K-PRO（Mini LED，约 1000 nit 峰值）。
运行环境：Windows 11，RTX 3080 Ti。

## 运行

**安装版**：从 Release 下载 `HdrPlayer-<版本>-win-x64-setup.exe`，不需要管理员权限，安装包已带 libmpv（也需要下面第 1 步的 .NET 运行时）。
安装时会列出所有支持的视频后缀，逐个勾选要用 HdrPlayer 打开的；所有后缀都会出现在右键“打开方式”里。`.ts` 默认不勾，因为 TypeScript 源文件也用这个后缀。
**一键设为默认播放器**：安装完成页勾选“一键设为默认播放器”（或以后在 设置 → 操作 → “一键设为默认”），确认一次管理员权限，**注销并重新登录（或重启）**后，勾选的后缀就全部用 HdrPlayer 打开，不用在 Windows 设置里逐个后缀点。
原理：Windows 10/11 不允许程序直接写用户的默认程序选择（带系统校验，新版 Windows 11 还换成了带机器标识的 UserChoiceLatest），这里用的是微软给企业管理员的正规机制——组策略“设置默认关联配置文件”（`HKLM\SOFTWARE\Policies\Microsoft\Windows\System\DefaultAssociationsConfiguration` 指向 `C:\ProgramData\HdrPlayer\DefaultAssociations.xml`），由 Windows 自己在登录时应用。不伪造系统校验，所以不怕系统更新改算法。
注意：策略生效期间，每次登录 Windows 都会把这些后缀恢复成 HdrPlayer；想换回别的播放器，先在设置里点“取消”（卸载时也会自动撤销）。微软官方只支持专业版 / 企业版 / 教育版，家庭版可能无效，可改用设置里的“打开默认应用设置”手动设置。
要增删后缀，改 `installer/gen_iss.py` 里的列表后重新生成 `HdrPlayer.iss`。

**便携版 / 自己编译**：

1. 安装 **.NET 8 Desktop Runtime (x64)**：https://dotnet.microsoft.com/download/dotnet/8.0
2. 下载 libmpv：https://github.com/shinchiro/mpv-winbuild-cmake/releases
   选最新的 `mpv-dev-x86_64-v3-*.7z`，解压出 **`libmpv-2.dll`**，放到 `HdrPlayer.exe` 同一目录。
   （libmpv 要用 2025 年及以后的版本，否则杜比视界 Profile 检测、HDR10+ 属性等功能可能缺失。）
3. 运行 `HdrPlayer.exe`。也可以把视频文件拖到 exe 上，或设为默认打开方式。

## 自动构建

推送 `v` 开头的标签（或在 GitHub 网页上新建 Release 时创建新标签）会触发 GitHub Actions：
编译、下载最新的 libmpv-2.dll（shinchiro/mpv-winbuild-cmake），打包成便携版 `HdrPlayer-<版本>-win-x64.zip` 和安装程序 `HdrPlayer-<版本>-win-x64-setup.exe`（Inno Setup，脚本在 `installer/`），上传到该 Release。

```
git tag v0.3.0
git push origin v0.3.0
```

标签带 `-`（如 `v0.3.0-beta`）会发成预发布。也可以在 Actions 页面手动运行，只生成构建产物、不发布。

## 从源码编译

```
dotnet build -c Release
```
把 `libmpv-2.dll` 放到项目目录，编译时会自动复制到输出目录。

## 功能

| 需求 | 实现 |
|---|---|
| HDR10 | mpv `vo=gpu-next` + D3D11，输出 PQ/BT.2020，按显示器 EDID 峰值做色调映射 |
| HDR10+ | 检测到动态元数据后，自动改用 ST 2094-40 曲线并关闭逐帧峰值检测 |
| 杜比视界 | 默认保留 RPU，由 gpu-next（libplacebo）按逐帧动态元数据重塑 + 色调映射，**Profile 5（IPTPQc2）也能正确播放**，P8.1 / P8.4 / P7 同样适用。设置里可改为“剥离 RPU”只播放基础层（此时 P5 会提示不兼容） |
| 解码 | 默认 D3D11VA 硬解；显卡不支持的格式（如 H.264 10-bit / Hi10P）自动改用软解，硬解中途连续出错也会回退。软解线程数可在设置里改，默认自动（按逻辑核心数，最多 16） |
| 5.1/7.1 → 立体声 | `audio-channels=stereo`（libswresample 标准下混矩阵） |
| 自动 HDR | 空着启动播放器**不会**改动 Windows 的 HDR 开关；打开 HDR 片源（HDR10 / HDR10+ / HLG / 杜比视界，包括通过“打开方式”直接启动）时自动开启 HDR 输出并打开 Windows 的“使用 HDR”；SDR 片源自动关闭。切换期间短暂暂停，纯音频和带封面的音乐文件不触发切换。退出时把 Windows HDR 还原为启动前的状态。设置里关掉“由软件自动管理系统 HDR 开关”则只手动切换 |
| 随时开关 HDR | 快捷键 H / 控制栏按钮 / 右键菜单，同样会切换 Windows 的“使用 HDR” |
| 读取屏幕参数 | 运行时通过 DXGI `IDXGIOutput6::GetDesc1` 读取 EDID 的峰值/全屏亮度、原色等 |
| 字幕 | 拖到画面上即可加载；Z/X 或右键菜单调整延迟（按文件记忆）；设置里可选字体（列出已安装字体）、字号、自动加载规则；首选音轨 / 字幕语言可从下拉框选（简体 / 繁体中文找不到时互相替代，再找不到用英语；其它语言找不到用英语），或手动输入语言代码列表（严格按顺序匹配，全角 / 半角逗号均可） |
| 播放列表 | 侧边栏（P），拖入的视频会加入列表；退出时保存，下次空着启动时恢复（只恢复列表，不自动播放），覆盖安装 / 升级后也保留 |
| 记住进度和延迟 | 每个文件分别记住上次的播放进度、音频延迟、字幕延迟，再次打开时自动套用（Home 键从头开始）；看完的文件下次从头开始。设置 → 操作 里可分别关闭 |
| 视频参数 | 右键 → 视频参数（I），每秒刷新；播放列表里右键其它文件也能查看（后台读取，不影响播放），并列出全部音轨 / 字幕轨 |
| 悬浮控制栏 | 浮在画面底部，鼠标移动时出现、静止 2 秒后淡出（同时隐藏鼠标指针）；窗口和全屏下都一样，不占画面高度 |
| 进度条预览 | 鼠标悬停在进度条上显示该位置的缩略图（后台另开一个不出画面的 mpv 实例，精确跳到最近的整秒截图，结果缓存；新图生成前旧图调暗显示）；仅本地文件，杜比视界 P5 只显示时间 |

## 快捷键

| 键 | 功能 |
|---|---|
| 空格 / 单击画面 / 中键 | 播放 / 暂停 |
| ← / → | 后退 / 前进 5 秒（可设置） |
| Shift + ← / → | 后退 / 前进 30 秒 |
| ↑ / ↓ / 滚轮 | 音量 |
| M | 静音 |
| F / 回车 / 双击 | 全屏；Esc 退出全屏 |
| H | 开关 HDR 输出 |
| I | 视频参数 |
| P | 播放列表 |
| PgUp / PgDn | 上一个 / 下一个 |
| Z / X | 字幕提前 / 延后（步进可设置，默认 100 ms） |
| V / J | 字幕显示开关 / 切换字幕轨 |
| # | 切换音轨 |
| . / , | 逐帧前进 / 后退 |
| Ctrl+O | 打开文件 |
| Home | 从头开始播放 |
| F5 | 重新载入当前文件（回到原位置），让“打开文件时才读取”的设置立即生效 |
| 右键 | 菜单：视频参数、在资源管理器中显示、切换音轨 / 字幕轨、音频 / 字幕延迟、HDR、解码方式、设置……（mpv 原生菜单） |

设置保存在 `%APPDATA%\HdrPlayer\settings.json`，播放列表在同目录的 `playlist.json`，每个文件的进度和延迟在 `file-states.json`。

## 已知限制

- 外部音轨请通过右键菜单 → 加载外部音轨，或拖到播放列表/控制栏上；拖到画面上会被 mpv 当作新视频加入播放列表。
- mpv 的画面是原生子窗口（HDR 需要 mpv 自己的交换链直接输出），WPF 内容不能直接叠在上面，所以悬浮控制栏是一个单独的透明小窗口，拖动主窗口时会跟随移动。
- 窗口化时画面四周留了 4 px 边距，用来拖动缩放窗口（原生子窗口会挡住窗口边缘的鼠标）。
- 音频输出设备只列出 WASAPI 设备。mpv 的设备列表里还有 OpenAL 等其它后端的 “Default (openal)” 之类条目，它们和“自动”一样指向系统默认设备，只是换了个输出后端，所以不再列出。
