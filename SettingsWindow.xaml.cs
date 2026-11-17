using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace HdrPlayer;

/// <summary>设置左侧导航的一项。</summary>
public sealed record SettingsNavItem(string Title, SymbolRegular Icon, StackPanel Page);

/// <summary>设置窗口。字幕/音频延迟即时生效，其它在点“确定”后应用。</summary>
public partial class SettingsWindow : FluentWindow
{
    public static readonly (string Value, string Name)[] HwdecChoices =
    {
        ("no", "软解 (CPU)"),
        ("d3d11va", "硬解 D3D11VA（推荐）"),
        ("nvdec", "硬解 NVDEC"),
        ("d3d11va-copy", "硬解 D3D11VA-copy（可用滤镜）"),
        ("nvdec-copy", "硬解 NVDEC-copy"),
        ("auto-safe", "自动"),
    };

    private static readonly (string, string)[] ToneMappingChoices =
    {
        ("auto", "自动"), ("spline", "spline"), ("bt.2390", "BT.2390"), ("bt.2446a", "BT.2446a"),
        ("st2094-40", "ST 2094-40 (HDR10+)"), ("st2094-10", "ST 2094-10"), ("hable", "Hable"),
        ("mobius", "Mobius"), ("reinhard", "Reinhard"), ("clip", "裁切 (clip)"),
    };

    private static readonly (string, string)[] DolbyVisionChoices =
    {
        ("rpu", "应用 RPU 动态元数据（推荐，支持 Profile 5）"),
        ("strip", "剥离 RPU，只播放 HDR10 / HLG 基础层"),
    };

    private static readonly (string, string)[] PeakDetectChoices =
        { ("auto", "自动"), ("yes", "开"), ("no", "关") };

    private static readonly (string, string)[] GamutChoices =
    {
        ("auto", "自动"), ("perceptual", "感知 (perceptual)"), ("relative", "相对 (relative)"),
        ("saturation", "饱和度 (saturation)"), ("clip", "裁切 (clip)"), ("desaturate", "去饱和 (desaturate)"),
    };

    private static readonly (string, string)[] ScaleChoices =
    {
        ("ewa_lanczossharp", "ewa_lanczossharp（高质量）"), ("ewa_lanczos", "ewa_lanczos"),
        ("lanczos", "lanczos"), ("spline36", "spline36"), ("mitchell", "mitchell"), ("bilinear", "bilinear（最快）"),
    };

    private static readonly (string, string)[] DscaleChoices =
    {
        ("mitchell", "mitchell"), ("hermite", "hermite"), ("catmull_rom", "catmull_rom"),
        ("lanczos", "lanczos"), ("bilinear", "bilinear"),
    };

    private static readonly (string, string)[] SyncChoices =
        { ("audio", "按音频同步（默认）"), ("display-resample", "按显示器刷新率重采样") };

    private static readonly (string, string)[] SubAutoChoices =
        { ("no", "不自动加载"), ("exact", "完全同名"), ("fuzzy", "包含文件名"), ("all", "目录下全部") };

    private static readonly (string, string)[] GpuApiChoices =
        { ("d3d11", "Direct3D 11（推荐）"), ("vulkan", "Vulkan") };

    private readonly AppSettings _s;
    private readonly Mpv _mpv;
    private readonly string _gdiName;
    private readonly bool _hdrOn;
    private readonly List<Action> _commit = new();

    internal SettingsWindow(AppSettings s, Mpv mpv, string gdiName, bool hdrOn)
    {
        _s = s;
        _mpv = mpv;
        _gdiName = gdiName;
        _hdrOn = hdrOn;
        InitializeComponent();

        var pages = new List<SettingsNavItem>
        {
            new("HDR", SymbolRegular.Lightbulb24, BuildHdrPage()),
            new("解码 / 渲染", SymbolRegular.Video24, BuildRenderPage()),
            new("音频", SymbolRegular.Speaker224, BuildAudioPage()),
            new("字幕", SymbolRegular.ClosedCaption24, BuildSubPage()),
            new("操作", SymbolRegular.Keyboard24, BuildControlPage()),
        };
        Nav.ItemsSource = pages;
        Nav.SelectionChanged += (_, _) =>
        {
            if (Nav.SelectedItem is not SettingsNavItem item) return;
            PageHost.Children.Clear();
            PageHost.Children.Add(item.Page);
            PageScroller.ScrollToTop();
        };
        Nav.SelectedIndex = 0;

        BtnOk.Click += (_, _) =>
        {
            foreach (var c in _commit) c();
            DialogResult = true;
        };
        BtnCancel.Click += (_, _) => DialogResult = false;
    }

    // ------------------------------------------------------------------ 页面

    private StackPanel BuildHdrPage()
    {
        var p = NewPage("HDR");
        var info = DisplayHelper.Query(_gdiName);
        string disp = info is { HasDxgi: true }
            ? $"当前显示器：{info.FriendlyName}　EDID 峰值 {info.MaxLuminance:0} nit / 全屏 {info.MaxFullFrameLuminance:0} nit　" +
              $"Windows HDR {(info.AdvancedColorEnabled ? "开" : "关")}　播放器 HDR 输出 {(_hdrOn ? "开" : "关")}"
            : "无法读取显示器 HDR 信息";
        AddNote(p, disp);

        AddToggle(p, "按片源自动切换 HDR",
            "HDR10 / HDR10+ / HLG / 杜比视界片源开始播放时自动开启 HDR，SDR 片源自动关闭。空着启动播放器不会改动系统设置。",
            _s.AutoHdrForHdrVideo, v => _s.AutoHdrForHdrVideo = v);
        AddToggle(p, "开关 HDR 时同时切换 Windows 的“使用 HDR”",
            "自动切换和手动按 H / 点 HDR 按钮都会切换系统 HDR。关闭此项则只改播放器的输出方式。",
            _s.AutoSwitchWindowsHdr, v => _s.AutoSwitchWindowsHdr = v);
        AddToggle(p, "退出时恢复原来的 Windows HDR 状态", "本次运行中播放器切换过 Windows HDR 时，退出后还原为启动前的状态。",
            _s.RestoreWindowsHdrOnExit, v => _s.RestoreWindowsHdrOnExit = v);
        AddCombo(p, "杜比视界", "应用 RPU 需要 vo=gpu-next（本播放器默认）；Profile 7 双层片源的增强层由新版 mpv 自动处理",
            DolbyVisionChoices, _s.DolbyVisionMode, v => _s.DolbyVisionMode = v);
        AddNumber(p, "目标峰值亮度 (nit)", "0 = 读取显示器 EDID 上报的峰值", _s.TargetPeakNits, 0, 10000, 50,
            v => _s.TargetPeakNits = (int)v);
        AddCombo(p, "色调映射算法", null, ToneMappingChoices, _s.ToneMapping, v => _s.ToneMapping = v);
        AddToggle(p, "HDR10+ 片源使用动态元数据", "使用 ST 2094-40 曲线，并关闭逐帧峰值检测",
            _s.Hdr10PlusUseMetadata, v => _s.Hdr10PlusUseMetadata = v);
        AddCombo(p, "逐帧峰值检测", null, PeakDetectChoices, _s.HdrComputePeak, v => _s.HdrComputePeak = v);
        AddCombo(p, "色域映射", "BT.2020 → 显示器色域", GamutChoices, _s.GamutMapping, v => _s.GamutMapping = v);
        AddNote(p, "片源亮度不超过目标峰值时不做色调映射，直接透传。\n" +
                   "杜比视界“应用 RPU”时按逐帧动态元数据重塑和色调映射，Profile 5 / 8.1 / 8.4 都能正确显示；\n" +
                   "“剥离 RPU”时 Profile 7/8 只播放基础层，Profile 5 会提示不兼容。");
        return p;
    }

    private StackPanel BuildRenderPage()
    {
        var p = NewPage("解码 / 渲染");
        AddCombo(p, "解码方式", null, HwdecChoices, _s.Hwdec, v => _s.Hwdec = v);
        AddNumber(p, "软解线程数", $"0 = 自动（按 CPU 逻辑核心数，最多 16；本机 {Environment.ProcessorCount} 个逻辑核心）。从下一个文件开始生效",
            _s.DecoderThreads, 0, 64, 1, v => _s.DecoderThreads = (int)v);
        AddCombo(p, "渲染 API", "重启播放器后生效", GpuApiChoices, _s.GpuApi, v => _s.GpuApi = v);
        AddCombo(p, "放大算法", null, ScaleChoices, _s.Scale, v => _s.Scale = v);
        AddCombo(p, "缩小算法", null, DscaleChoices, _s.Dscale, v => _s.Dscale = v);
        AddToggle(p, "去色带 (deband)", null, _s.Deband, v => _s.Deband = v);
        AddCombo(p, "视频同步", null, SyncChoices, _s.VideoSync, v => _s.VideoSync = v);
        AddToggle(p, "帧插值", "平滑 24p 抖动，会自动改为按显示器重采样", _s.Interpolation, v => _s.Interpolation = v);
        AddToggle(p, "写 mpv 日志", @"%APPDATA%\HdrPlayer\mpv.log，重启后生效", _s.DebugLog, v => _s.DebugLog = v);
        AddNote(p, "RTX 3080 Ti 可硬解 H.264 和 HEVC 8/10-bit；HDR10+ 动态元数据在硬解下同样可用。\n" +
                   "Hi10P（10-bit H.264）显卡不支持硬解，会自动回落到软解。");
        return p;
    }

    private StackPanel BuildAudioPage()
    {
        var p = NewPage("音频");
        // mpv 的设备列表里除了 WASAPI 设备，还有 OpenAL 等其它输出后端各自的 “Default (openal)” 之类的默认设备。
        // 它们和 “自动” 指向的都是系统默认设备，只是换了一个后端，看起来像重复项，这里只保留 WASAPI 设备。
        var devices = new List<(string, string)> { ("auto", "自动（跟随系统默认设备）") };
        if (_mpv.GetNode("audio-device-list") is List<object?> list)
        {
            foreach (var d in list.OfType<Dictionary<string, object?>>())
            {
                string name = d.GetValueOrDefault("name") as string ?? "";
                if (!name.StartsWith("wasapi/", StringComparison.OrdinalIgnoreCase)) continue;
                string desc = d.GetValueOrDefault("description") as string ?? name;
                devices.Add((name, desc));
            }
        }
        AddCombo(p, "输出设备", null, devices.ToArray(), _s.AudioDevice, v => _s.AudioDevice = v);
        AddToggle(p, "多声道下混到立体声", "5.1 / 7.1 → 2.0", _s.DownmixToStereo, v => _s.DownmixToStereo = v);
        AddToggle(p, "下混时归一化", "防削波，但整体音量会变小", _s.NormalizeDownmix, v => _s.NormalizeDownmix = v);
        AddText(p, "首选音轨语言", "如 chi,jpn,eng", _s.AudioLanguages, v => _s.AudioLanguages = v);
        double ad = (_mpv.GetDouble("audio-delay") ?? 0) * 1000;
        AddLiveNumber(p, "音频延迟 (ms)", "即时生效", ad, -10000, 10000, 10, v => _mpv.SetProperty("audio-delay", v / 1000.0));
        return p;
    }

    private StackPanel BuildSubPage()
    {
        var p = NewPage("字幕");
        double sd = (_mpv.GetDouble("sub-delay") ?? 0) * 1000;
        AddLiveNumber(p, "字幕延迟 (ms)", "负数 = 提前，即时生效", sd, -60000, 60000, 50, v => _mpv.SetProperty("sub-delay", v / 1000.0));
        AddText(p, "字体", "SRT 等纯文本字幕使用", _s.SubFont, v => _s.SubFont = v);
        AddNumber(p, "字号", null, _s.SubFontSize, 10, 120, 1, v => _s.SubFontSize = (int)v);
        AddCombo(p, "自动加载外挂字幕", null, SubAutoChoices, _s.SubAuto, v => _s.SubAuto = v);
        AddText(p, "首选字幕语言", null, _s.SubLanguages, v => _s.SubLanguages = v);
        AddNote(p, "把字幕文件直接拖到画面上即可加载。快捷键：Z 提前 / X 延后 / V 显示开关 / J 切换字幕轨。");
        return p;
    }

    private StackPanel BuildControlPage()
    {
        var p = NewPage("操作");
        AddNumber(p, "←/→ 跳转秒数", null, _s.SeekStep, 1, 600, 1, v => _s.SeekStep = v);
        AddNumber(p, "Shift+←/→ 跳转秒数", null, _s.SeekStepLarge, 1, 3600, 5, v => _s.SeekStepLarge = v);
        AddToggle(p, "精确跳转", "关闭则跳到关键帧，更快", _s.ExactSeek, v => _s.ExactSeek = v);
        AddToggle(p, "进度条悬停预览缩略图", "后台另开一个不出画面的 mpv 实例截图；仅本地文件。HDR 片源的缩略图高光会被裁切",
            _s.SeekThumbnails, v => _s.SeekThumbnails = v);
        AddNumber(p, "音量步进", null, _s.VolumeStep, 1, 50, 1, v => _s.VolumeStep = v);
        AddNumber(p, "字幕延迟步进 (ms)", null, _s.SubDelayStepMs, 10, 5000, 10, v => _s.SubDelayStepMs = (int)v);
        AddNote(p,
            "空格 播放/暂停　←/→ 快退/快进　Shift+←/→ 大步跳转\n" +
            "↑/↓/滚轮 音量　M 静音　F/回车/双击 全屏　Esc 退出全屏\n" +
            "H 开关 HDR　I 视频参数　P 播放列表　PgUp/PgDn 上/下一个\n" +
            "Z/X 字幕延迟　V 字幕开关　J 切换字幕　# 切换音轨\n" +
            ". / , 逐帧前进/后退　Ctrl+O 打开　右键 菜单");
        return p;
    }

    // ------------------------------------------------------------------ helpers

    private static StackPanel NewPage(string title)
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock
        {
            Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 4, 0, 12),
        });
        return page;
    }

    /// <summary>Windows 11 设置页风格的一行：左边标题 + 说明，右边控件。</summary>
    private static void AddCard(Panel page, string title, string? description, FrameworkElement control)
    {
        var header = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        header.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description))
        {
            var desc = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            header.Children.Add(desc);
        }
        control.VerticalAlignment = VerticalAlignment.Center;
        page.Children.Add(new CardControl
        {
            Header = header, Content = control, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(16, 10, 16, 10),
        });
    }

    private void AddToggle(Panel page, string title, string? description, bool value, Action<bool> setter)
    {
        var ts = new ToggleSwitch { IsChecked = value };
        AddCard(page, title, description, ts);
        _commit.Add(() => setter(ts.IsChecked == true));
    }

    private void AddCombo(Panel page, string title, string? description, (string Value, string Name)[] choices, string current, Action<string> setter)
    {
        var combo = new ComboBox { MinWidth = 260 };
        int idx = Array.FindIndex(choices, c => c.Value == current);
        if (idx < 0 && !string.IsNullOrEmpty(current))
        {
            // 设置文件里有列表之外的值（比如手动改过、或设备已拔掉）：保留它
            choices = choices.Append((current, current)).ToArray();
            idx = choices.Length - 1;
        }
        foreach (var c in choices) combo.Items.Add(c.Name);
        combo.SelectedIndex = Math.Max(0, idx);
        AddCard(page, title, description, combo);
        var ch = choices;
        _commit.Add(() => setter(ch[Math.Max(0, combo.SelectedIndex)].Value));
    }

    private static NumberBox NewNumber(double value, double min, double max, double step) => new()
    {
        Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10,
        MaxDecimalPlaces = step < 1 ? 1 : 0,
        Value = Math.Clamp(Math.Round(value), min, max),
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        ClearButtonEnabled = false,
        Width = 180,
    };

    /// <summary>NumberBox 的 Value 要失去焦点才更新，直接点“确定”时以文本为准。</summary>
    private static double ReadNumber(NumberBox nb, double fallback)
    {
        if (double.TryParse(nb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double t) ||
            double.TryParse(nb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out t))
            return Math.Clamp(t, nb.Minimum, nb.Maximum);
        return nb.Value ?? fallback;
    }

    private void AddNumber(Panel page, string title, string? description, double value, double min, double max, double step, Action<double> setter)
    {
        var nb = NewNumber(value, min, max, step);
        AddCard(page, title, description, nb);
        _commit.Add(() => setter(ReadNumber(nb, value)));
    }

    private static void AddLiveNumber(Panel page, string title, string? description, double value, double min, double max, double step, Action<double> apply)
    {
        var nb = NewNumber(value, min, max, step);
        nb.ValueChanged += (_, _) => { if (nb.Value is double v) apply(v); };
        AddCard(page, title, description, nb);
    }

    private void AddText(Panel page, string title, string? description, string value, Action<string> setter)
    {
        var tb = new TextBox { Text = value, Width = 260, ClearButtonEnabled = false };
        AddCard(page, title, description, tb);
        _commit.Add(() => setter(tb.Text.Trim()));
    }

    private static void AddNote(Panel page, string text)
    {
        var lbl = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 12) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        page.Children.Add(lbl);
    }
}
