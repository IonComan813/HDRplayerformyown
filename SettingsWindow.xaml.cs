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

    /// <summary>字幕字体：先列常用的中文字体（装了的才列），再列其余已安装字体；显示中文名（如果有）+ 英文名。</summary>
    private static (string Value, string Name)[] FontChoices()
    {
        string[] preferred =
        {
            "Microsoft YaHei", "Microsoft YaHei UI", "DengXian", "SimHei", "SimSun", "KaiTi", "FangSong",
            "Source Han Sans SC", "Noto Sans CJK SC", "Noto Sans SC", "Sarasa Gothic SC", "LXGW WenKai",
            "HarmonyOS Sans SC", "MiSans", "Segoe UI", "Arial",
        };
        var zh = System.Windows.Markup.XmlLanguage.GetLanguage("zh-cn");
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                string en = f.Source;
                if (string.IsNullOrEmpty(en) || installed.ContainsKey(en)) continue;
                string label = f.FamilyNames.TryGetValue(zh, out var local) && !string.Equals(local, en, StringComparison.OrdinalIgnoreCase)
                    ? $"{local}（{en}）" : en;
                installed[en] = label;
            }
        }
        catch { /* 枚举失败就只给常用列表 */ }
        var list = new List<(string, string)>();
        foreach (var name in preferred)
            if (installed.TryGetValue(name, out var label)) list.Add((name, label));
        foreach (var kv in installed.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            if (!preferred.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) list.Add((kv.Key, kv.Value));
        if (list.Count == 0) list.Add(("Microsoft YaHei", "微软雅黑（Microsoft YaHei）"));
        return list.ToArray();
    }

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

    /// <summary>只在打开文件时读取的设置：正在播放的文件要重新载入才生效。</summary>
    private const string ReloadNote = "打开下一个文件时生效；当前文件可点控制栏的重新载入按钮（F5）立即生效";

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

        AddToggle(p, "由软件自动管理系统 HDR 开关",
            "开：打开 HDR 视频（HDR10 / HDR10+ / HLG / 杜比视界）时自动开启 Windows 的“使用 HDR”，打开 SDR 视频时自动关闭；" +
            "空着启动播放器不会改动系统设置。\n" +
            "关：始终由你手动点控制栏的 HDR / SDR 按钮（或按 H）切换。\n" +
            "两种方式下，退出播放器时都会把 Windows HDR 还原为启动前的状态。",
            _s.AutoManageHdr, v => _s.AutoManageHdr = v);
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
        AddNumber(p, "软解线程数", $"0 = 自动（按 CPU 逻辑核心数，最多 16；本机 {Environment.ProcessorCount} 个逻辑核心）。" + ReloadNote,
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
        AddLanguagePicker(p, "首选音轨语言",
            "有多条音轨时优先选这种语言；选的不是英语时，找不到这种语言就用英语音轨。" +
            "手动输入则完全按你填的顺序匹配，不会自动回退。" + ReloadNote,
            LanguagePresets.Audio, _s.AudioLanguages, v => _s.AudioLanguages = v);
        AddNote(p, "音频延迟按文件记忆，在画面右键菜单 → 音频延迟 里调整。");
        return p;
    }

    private StackPanel BuildSubPage()
    {
        var p = NewPage("字幕");
        AddCombo(p, "字体", "SRT 等纯文本字幕使用（ASS 字幕自带字体样式）", FontChoices(), _s.SubFont, v => _s.SubFont = v);
        AddNumber(p, "字号", null, _s.SubFontSize, 10, 120, 1, v => _s.SubFontSize = (int)v);
        AddCombo(p, "自动加载外挂字幕", ReloadNote, SubAutoChoices, _s.SubAuto, v => _s.SubAuto = v);
        AddLanguagePicker(p, "首选字幕语言",
            "有多条字幕轨时优先选这种语言。简体中文找不到就用繁体中文，繁体中文找不到就用简体中文，都没有就用英语；" +
            "其它语言找不到也用英语。手动输入则完全按你填的顺序匹配，不会自动回退。" + ReloadNote,
            LanguagePresets.Subtitle, _s.SubLanguages, v => _s.SubLanguages = v);
        AddNote(p, "把字幕文件直接拖到画面上即可加载。字幕延迟按文件记忆，在画面右键菜单 → 字幕延迟 里调整。\n" +
                   "快捷键：Z 提前 / X 延后 / V 显示开关 / J 切换字幕轨。");
        return p;
    }

    private StackPanel BuildControlPage()
    {
        var p = NewPage("操作");
        AddNumber(p, "←/→ 跳转秒数", null, _s.SeekStep, 1, 600, 1, v => _s.SeekStep = v);
        AddNumber(p, "Shift+←/→ 跳转秒数", null, _s.SeekStepLarge, 1, 3600, 5, v => _s.SeekStepLarge = v);
        AddToggle(p, "精确跳转", "关闭则跳到关键帧，更快", _s.ExactSeek, v => _s.ExactSeek = v);
        AddToggle(p, "启动时恢复上次的播放列表", "空着启动时只恢复列表，不会自动开始播放；双击视频文件启动时，恢复列表后把这个文件加到末尾并播放",
            _s.RestorePlaylist, v => _s.RestorePlaylist = v);
        AddToggle(p, "记住每个文件的播放进度", "再次打开时从上次的位置继续（Home 键从头开始）；看完或只剩最后 10 秒的下次从头开始",
            _s.RememberPosition, v => _s.RememberPosition = v);
        AddToggle(p, "记住每个文件的音频 / 字幕延迟", "关闭则延迟在切换文件时保持不变，退出后归零",
            _s.RememberDelays, v => _s.RememberDelays = v);
        AddToggle(p, "进度条悬停预览缩略图", "后台另开一个不出画面的 mpv 实例截图；仅本地文件。HDR 片源的缩略图高光会被裁切",
            _s.SeekThumbnails, v => _s.SeekThumbnails = v);
        AddNumber(p, "音量步进", null, _s.VolumeStep, 1, 50, 1, v => _s.VolumeStep = v);
        AddNumber(p, "延迟调整步进 (ms)", "右键菜单里的音频 / 字幕延迟，以及 Z / X 每次调整多少", _s.SubDelayStepMs, 10, 5000, 10,
            v => _s.SubDelayStepMs = (int)v);
        AddDefaultAppCards(p);
        AddNote(p,
            "空格 播放/暂停　←/→ 快退/快进　Shift+←/→ 大步跳转\n" +
            "↑/↓/滚轮 音量　M 静音　F/回车/双击 全屏　Esc 退出全屏\n" +
            "H 开关 HDR　I 视频参数　P 播放列表　PgUp/PgDn 上/下一个\n" +
            "Z/X 字幕延迟　V 字幕开关　J 切换字幕　# 切换音轨\n" +
            ". / , 逐帧前进/后退　Ctrl+O 打开　F5 重新载入　单击画面 暂停/播放　右键 菜单");
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

    /// <summary>
    /// 语言下拉框 + “手动输入”。手动输入时显示输入框，按顺序填语言代码，例如 chi,zho,chs,zh,zh-Hans,eng,en；
    /// 全角 / 半角逗号、逗号后有无空格都可以，保存时统一整理。输入框留空表示不指定（用文件标记的默认轨道）。
    /// </summary>
    private void AddLanguagePicker(Panel page, string title, string? description, (string Value, string Name)[] choices,
        string current, Action<string> setter)
    {
        const string Manual = "手动输入";
        string cur = AppSettings.NormalizeLanguageList(current);
        int idx = Array.FindIndex(choices, c => string.Equals(c.Value, cur, StringComparison.OrdinalIgnoreCase));

        var combo = new ComboBox { MinWidth = 200 };
        foreach (var c in choices) combo.Items.Add(c.Name);
        combo.Items.Add(Manual);
        var box = new TextBox
        {
            Width = 280, Margin = new Thickness(8, 0, 0, 0), ClearButtonEnabled = false,
            PlaceholderText = "如 chi,zho,chs,zh,zh-Hans,eng,en",
            Text = idx >= 0 ? choices[idx].Value : cur,
        };
        combo.SelectionChanged += (_, _) =>
        {
            bool manual = combo.SelectedIndex == choices.Length;
            box.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
            // 从某个预设切到手动输入时，用那个预设的代码作为起点，方便在它的基础上改
            if (!manual && combo.SelectedIndex >= 0) box.Text = choices[combo.SelectedIndex].Value;
        };
        combo.SelectedIndex = idx >= 0 ? idx : choices.Length;
        box.Visibility = idx >= 0 ? Visibility.Collapsed : Visibility.Visible;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(combo);
        panel.Children.Add(box);
        AddCard(page, title, description, panel);
        _commit.Add(() => setter(combo.SelectedIndex >= 0 && combo.SelectedIndex < choices.Length
            ? choices[combo.SelectedIndex].Value
            : AppSettings.NormalizeLanguageList(box.Text)));
    }

    private static void AddNote(Panel page, string text)
    {
        var lbl = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8, 4, 12) };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        page.Children.Add(lbl);
    }

    // ------------------------------------------------------------------ 默认播放器

    private void AddDefaultAppCards(Panel p)
    {
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var setBtn = new Wpf.Ui.Controls.Button
        {
            Content = "一键设为默认", Appearance = ControlAppearance.Primary,
            Icon = new SymbolIcon { Symbol = SymbolRegular.Shield24 },
        };
        var undoBtn = new Wpf.Ui.Controls.Button { Content = "取消", Margin = new Thickness(8, 0, 0, 0) };
        void Refresh()
        {
            bool on = DefaultApps.PolicyActive();
            undoBtn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            status.Text = on
                ? "已设置：注销并重新登录（或重启）后，" + string.Join(" ", DefaultApps.ChosenExtensions()) +
                  " 由 HdrPlayer 打开。设置期间 Windows 每次登录都会恢复成 HdrPlayer，想改用别的播放器请先点“取消”。"
                : "";
            status.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        setBtn.Click += async (_, _) =>
        {
            var exts = DefaultApps.ChosenExtensions();
            try { DefaultApps.RegisterForCurrentUser(exts); } catch { }
            string? err = DefaultApps.RunElevated(DefaultApps.ApplyArg, exts);
            Refresh();
            if (err != null) await Dialogs.ShowAsync(this, "设为默认播放器", "没有完成：" + err);
            else await Dialogs.ShowAsync(this, "设为默认播放器",
                "已写入 Windows 的默认关联配置。注销并重新登录（或重启）后生效。" +
                (DefaultApps.IsHomeEdition() ? "\n\n注意：这台电脑是 Windows 家庭版，微软官方只在专业版 / 企业版 / 教育版上支持这个设置，可能不生效；不生效时请用“打开默认应用设置”。" : ""));
        };
        undoBtn.Click += async (_, _) =>
        {
            string? err = DefaultApps.RunElevated(DefaultApps.RemoveArg);
            Refresh();
            if (err != null) await Dialogs.ShowAsync(this, "取消默认播放器设置", "没有完成：" + err);
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(setBtn);
        row.Children.Add(undoBtn);
        AddCard(p, "用 HdrPlayer 打开视频文件",
            "一次设置所有视频后缀（安装时勾选的；便携版用常见后缀）。用的是 Windows 给企业管理员的“默认关联配置文件”，" +
            "需要管理员权限，注销重新登录后生效", row);
        p.Children.Add(status);
        Refresh();

        var openBtn = new Wpf.Ui.Controls.Button { Content = "打开默认应用设置", Icon = new SymbolIcon { Symbol = SymbolRegular.Open24 } };
        openBtn.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "ms-settings:defaultapps?registeredAppUser=HdrPlayer") { UseShellExecute = true });
            }
            catch { }
        };
        AddCard(p, "手动设置", "不想用上面的方式时：在 Windows 设置里 HdrPlayer 的页面点“设为默认值”，或逐个后缀选择", openBtn);
    }
}
