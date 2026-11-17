# HdrPlayer

基于 libmpv 的本地视频播放器，只针对一台显示器调校：ViewSonic VX2781-4K-PRO（Mini LED，约 1000 nit 峰值）。
运行环境：Windows 11，RTX 3080 Ti。

## 运行

1. 安装 **.NET 8 Desktop Runtime (x64)**：https://dotnet.microsoft.com/download/dotnet/8.0
2. 下载 libmpv：https://github.com/shinchiro/mpv-winbuild-cmake/releases
   选最新的 `mpv-dev-x86_64-v3-*.7z`，解压出 **`libmpv-2.dll`**，放到 `HdrPlayer.exe` 同一目录。
   （libmpv 要用 2025 年及以后的版本，否则杜比视界 Profile 检测、HDR10+ 属性等功能可能缺失。）
3. 运行 `HdrPlayer.exe`。也可以把视频文件拖到 exe 上，或设为默认打开方式。

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
| 杜比视界 | `vf=format:dolbyvision=no` 剥离 RPU，只播放基础层（P7/P8.1 为 HDR10，P8.4 为 HLG）；**Profile 5 提示不兼容并跳过** |
| 5.1/7.1 → 立体声 | `audio-channels=stereo`（libswresample 标准下混矩阵） |
| 随时开关 HDR | 快捷键 H / 控制栏按钮 / 右键菜单；会同时切换 Windows 的“使用 HDR”，退出时恢复原状态 |
| 读取屏幕参数 | 运行时通过 DXGI `IDXGIOutput6::GetDesc1` 读取 EDID 的峰值/全屏亮度、原色等 |
| 字幕 | 拖到画面上即可加载；Z/X 调整延迟；设置里可改字体、字号、自动加载规则 |
| 播放列表 | 侧边栏（P），拖入的视频会加入列表 |
| 视频参数 | 右键 → 视频参数（I），每秒刷新 |

## 快捷键

| 键 | 功能 |
|---|---|
| 空格 / 中键 | 播放 / 暂停 |
| ← / → | 后退 / 前进 5 秒（可设置） |
| Shift + ← / → | 后退 / 前进 30 秒 |
| ↑ / ↓ / 滚轮 | 音量 |
| M | 静音 |
| F / 回车 / 双击 | 全屏；Esc 退出全屏 |
| H | 开关 HDR 输出 |
| I | 视频参数 |
| P | 播放列表 |
| PgUp / PgDn | 上一个 / 下一个 |
| Z / X | 字幕提前 / 延后 100 ms |
| V / J | 字幕显示开关 / 切换字幕轨 |
| # | 切换音轨 |
| . / , | 逐帧前进 / 后退 |
| Ctrl+O | 打开文件 |
| 右键 | 菜单（音轨、字幕、解码方式、设置……） |

设置保存在 `%APPDATA%\HdrPlayer\settings.json`。

## 已知限制

- 外部音轨请通过右键菜单 → 加载外部音轨，或拖到播放列表/控制栏上；拖到画面上会被 mpv 当作新视频加入播放列表。
- 全屏时鼠标移到屏幕底部会显示控制栏，此时画面会被压缩一条控制栏的高度。
