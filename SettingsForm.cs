using System.Globalization;

namespace HdrPlayer;

/// <summary>设置窗口。字幕/音频延迟即时生效，其它在点“确定”后应用。</summary>
internal sealed class SettingsForm : Form
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
    private readonly List<Action> _commit = new();

    public SettingsForm(AppSettings s, Mpv mpv)
    {
        _s = s;
        _mpv = mpv;
        Text = "设置";
        Size = new Size(780, 620);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9f);

        BackColor = Theme.Panel;
        ForeColor = Theme.Text;

        // 左侧导航 + 右侧内容（比 TabControl 更容易做成全深色）
        var pages = new[] { BuildHdrTab(), BuildRenderTab(), BuildAudioTab(), BuildSubTab(), BuildControlTab() };
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 12, 0), BackColor = Theme.Panel };
        foreach (var p in pages) { p.Visible = false; content.Controls.Add(p); }
        pages[0].Visible = true;

        var nav = new ListBox
        {
            Dock = DockStyle.Left, Width = 150, BorderStyle = BorderStyle.None, BackColor = Theme.Bar,
            ForeColor = Theme.Text, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 40, IntegralHeight = false,
        };
        foreach (var p in pages) nav.Items.Add(p.Tag!);
        nav.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool sel = e.Index == nav.SelectedIndex;
            using var bg = new SolidBrush(sel ? Theme.Surface : Theme.Bar);
            e.Graphics.FillRectangle(bg, e.Bounds);
            if (sel)
            {
                using var acc = new SolidBrush(Theme.Accent);
                e.Graphics.FillRectangle(acc, e.Bounds.X, e.Bounds.Y + 10, 3, e.Bounds.Height - 20);
            }
            TextRenderer.DrawText(e.Graphics, nav.Items[e.Index].ToString(), Font,
                new Rectangle(e.Bounds.X + 18, e.Bounds.Y, e.Bounds.Width - 18, e.Bounds.Height),
                sel ? Theme.Text : Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        };
        nav.SelectedIndexChanged += (_, _) =>
        {
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == nav.SelectedIndex;
            nav.Invalidate();
        };
        nav.SelectedIndex = 0;

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10), BackColor = Theme.Bar,
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
        ok.Click += (_, _) => { foreach (var c in _commit) c(); };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(content);
        Controls.Add(nav);
        Controls.Add(bottom);

        Theme.StyleTree(this);
        ok.BackColor = Theme.Accent;
        ok.FlatAppearance.BorderColor = Theme.Accent;
        ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(96, 170, 255);
        Theme.DarkTitleBar(this);
    }

    // ------------------------------------------------------------------

    private Panel BuildHdrTab()
    {
        var (page, grid) = NewPage("HDR");
        AddCheck(grid, "启用 HDR 输出（快捷键 H）", _s.HdrEnabled, v => _s.HdrEnabled = v);
        AddCheck(grid, "自动切换 Windows 的“使用 HDR”开关", _s.AutoSwitchWindowsHdr, v => _s.AutoSwitchWindowsHdr = v);
        AddCheck(grid, "退出时恢复原来的 Windows HDR 状态", _s.RestoreWindowsHdrOnExit, v => _s.RestoreWindowsHdrOnExit = v);
        AddNumber(grid, "目标峰值亮度 (nit，0 = 读取显示器)", _s.TargetPeakNits, 0, 10000, 50, v => _s.TargetPeakNits = (int)v);
        AddCombo(grid, "色调映射算法", ToneMappingChoices, _s.ToneMapping, v => _s.ToneMapping = v);
        AddCheck(grid, "HDR10+ 片源使用动态元数据（ST 2094-40，关闭峰值检测）", _s.Hdr10PlusUseMetadata, v => _s.Hdr10PlusUseMetadata = v);
        AddCombo(grid, "逐帧峰值检测", PeakDetectChoices, _s.HdrComputePeak, v => _s.HdrComputePeak = v);
        AddCombo(grid, "色域映射 (BT.2020 → 显示器)", GamutChoices, _s.GamutMapping, v => _s.GamutMapping = v);

        var info = DisplayHelper.Query(Screen.FromControl(Form.ActiveForm ?? this).DeviceName);
        string disp = info is { HasDxgi: true }
            ? $"当前显示器：{info.FriendlyName}  EDID 峰值 {info.MaxLuminance:0} nit / 全屏 {info.MaxFullFrameLuminance:0} nit，" +
              $"Windows HDR {(info.AdvancedColorEnabled ? "开" : "关")}"
            : "无法读取显示器 HDR 信息";
        AddNote(grid, disp + "\n片源亮度不超过目标峰值时不做色调映射，直接透传。\n杜比视界 Profile 7/8 只播放基础层；Profile 5 会提示不兼容。");
        return page;
    }

    private Panel BuildRenderTab()
    {
        var (page, grid) = NewPage("解码 / 渲染");
        AddCombo(grid, "解码方式", HwdecChoices, _s.Hwdec, v => _s.Hwdec = v);
        AddCombo(grid, "渲染 API（重启生效）", GpuApiChoices, _s.GpuApi, v => _s.GpuApi = v);
        AddCombo(grid, "放大算法", ScaleChoices, _s.Scale, v => _s.Scale = v);
        AddCombo(grid, "缩小算法", DscaleChoices, _s.Dscale, v => _s.Dscale = v);
        AddCheck(grid, "去色带 (deband)", _s.Deband, v => _s.Deband = v);
        AddCombo(grid, "视频同步", SyncChoices, _s.VideoSync, v => _s.VideoSync = v);
        AddCheck(grid, "帧插值（平滑 24p 抖动，会自动改为按显示器重采样）", _s.Interpolation, v => _s.Interpolation = v);
        AddCheck(grid, "写 mpv 日志到 %APPDATA%\\HdrPlayer\\mpv.log（重启生效）", _s.DebugLog, v => _s.DebugLog = v);
        AddNote(grid, "RTX 3080 Ti 可硬解 H.264 和 HEVC 8/10-bit；HDR10+ 动态元数据在硬解下同样可用。\n" +
                      "Hi10P（10-bit H.264）显卡不支持硬解，会自动回落到软解。");
        return page;
    }

    private Panel BuildAudioTab()
    {
        var (page, grid) = NewPage("音频");
        var devices = new List<(string, string)> { ("auto", "自动（系统默认）") };
        if (_mpv.GetNode("audio-device-list") is List<object?> list)
        {
            foreach (var d in list.OfType<Dictionary<string, object?>>())
            {
                string name = d.GetValueOrDefault("name") as string ?? "";
                string desc = d.GetValueOrDefault("description") as string ?? name;
                if (name.Length > 0 && name != "auto") devices.Add((name, desc));
            }
        }
        AddCombo(grid, "输出设备", devices.ToArray(), _s.AudioDevice, v => _s.AudioDevice = v);
        AddCheck(grid, "多声道 (5.1 / 7.1) 下混到立体声", _s.DownmixToStereo, v => _s.DownmixToStereo = v);
        AddCheck(grid, "下混时归一化（防削波，但整体音量会变小）", _s.NormalizeDownmix, v => _s.NormalizeDownmix = v);
        AddText(grid, "首选音轨语言（如 chi,jpn,eng）", _s.AudioLanguages, v => _s.AudioLanguages = v);
        double ad = (_mpv.GetDouble("audio-delay") ?? 0) * 1000;
        AddLiveNumber(grid, "音频延迟 (ms，即时生效)", ad, -10000, 10000, 10, v => _mpv.SetProperty("audio-delay", v / 1000.0));
        return page;
    }

    private Panel BuildSubTab()
    {
        var (page, grid) = NewPage("字幕");
        double sd = (_mpv.GetDouble("sub-delay") ?? 0) * 1000;
        AddLiveNumber(grid, "字幕延迟 (ms，负数=提前，即时生效)", sd, -60000, 60000, 50, v => _mpv.SetProperty("sub-delay", v / 1000.0));
        AddText(grid, "字体（SRT 等纯文本字幕）", _s.SubFont, v => _s.SubFont = v);
        AddNumber(grid, "字号", _s.SubFontSize, 10, 120, 1, v => _s.SubFontSize = (int)v);
        AddCombo(grid, "自动加载外挂字幕", SubAutoChoices, _s.SubAuto, v => _s.SubAuto = v);
        AddText(grid, "首选字幕语言", _s.SubLanguages, v => _s.SubLanguages = v);
        AddNote(grid, "把字幕文件直接拖到画面上即可加载。快捷键：Z 提前 / X 延后 / V 显示开关 / J 切换字幕轨。");
        return page;
    }

    private Panel BuildControlTab()
    {
        var (page, grid) = NewPage("操作");
        AddNumber(grid, "←/→ 跳转秒数", _s.SeekStep, 1, 600, 1, v => _s.SeekStep = v);
        AddNumber(grid, "Shift+←/→ 跳转秒数", _s.SeekStepLarge, 1, 3600, 5, v => _s.SeekStepLarge = v);
        AddCheck(grid, "精确跳转（关闭则跳到关键帧，更快）", _s.ExactSeek, v => _s.ExactSeek = v);
        AddNumber(grid, "音量步进", _s.VolumeStep, 1, 50, 1, v => _s.VolumeStep = v);
        AddNumber(grid, "字幕延迟步进 (ms)", _s.SubDelayStepMs, 10, 5000, 10, v => _s.SubDelayStepMs = (int)v);
        AddNote(grid,
            "空格 播放/暂停    ←/→ 快退/快进    Shift+←/→ 大步跳转\n" +
            "↑/↓/滚轮 音量    M 静音    F/回车/双击 全屏    Esc 退出全屏\n" +
            "H 开关 HDR    I 视频参数    P 播放列表    PgUp/PgDn 上/下一个\n" +
            "Z/X 字幕延迟    V 字幕开关    J 切换字幕    # 切换音轨\n" +
            ". / , 逐帧前进/后退    Ctrl+O 打开    右键 菜单");
        return page;
    }

    // ------------------------------------------------------------------ helpers

    private static (Panel, TableLayoutPanel) NewPage(string title)
    {
        var page = new Panel { Dock = DockStyle.Fill, Tag = title };
        var header = new Label
        {
            Text = title, Dock = DockStyle.Top, Height = 40, ForeColor = Theme.Text,
            Font = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold),
        };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
        page.Controls.Add(grid);
        page.Controls.Add(header);
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return (page, grid);
    }

    private static void AddRow(TableLayoutPanel grid, string label, Control c)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, row);
        c.Margin = new Padding(3, 3, 3, 3);
        c.Dock = DockStyle.Fill;
        grid.Controls.Add(c, 1, row);
    }

    private void AddCheck(TableLayoutPanel grid, string label, bool value, Action<bool> setter)
    {
        var cb = new CheckBox { Text = label, Checked = value, AutoSize = true, Margin = new Padding(3, 5, 3, 3) };
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(cb, 0, row);
        grid.SetColumnSpan(cb, 2);
        _commit.Add(() => setter(cb.Checked));
    }

    private void AddCombo(TableLayoutPanel grid, string label, (string Value, string Name)[] choices, string current, Action<string> setter)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var c in choices) combo.Items.Add(c.Name);
        int idx = Array.FindIndex(choices, c => c.Value == current);
        if (idx < 0 && !string.IsNullOrEmpty(current))
        {
            // 设置文件里有列表之外的值（比如手动改过）：保留它
            combo.Items.Add(current);
            choices = choices.Append((current, current)).ToArray();
            idx = choices.Length - 1;
        }
        combo.SelectedIndex = Math.Max(0, idx);
        AddRow(grid, label, combo);
        var ch = choices;
        _commit.Add(() => setter(ch[Math.Max(0, combo.SelectedIndex)].Value));
    }

    private void AddNumber(TableLayoutPanel grid, string label, double value, double min, double max, double step, Action<double> setter)
    {
        var num = NewNumeric(value, min, max, step);
        AddRow(grid, label, num);
        _commit.Add(() => setter((double)num.Value));
    }

    private static void AddLiveNumber(TableLayoutPanel grid, string label, double value, double min, double max, double step, Action<double> apply)
    {
        var num = NewNumeric(value, min, max, step);
        num.ValueChanged += (_, _) => apply((double)num.Value);
        AddRow(grid, label, num);
    }

    private static NumericUpDown NewNumeric(double value, double min, double max, double step) => new()
    {
        Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step,
        DecimalPlaces = step < 1 ? 1 : 0,
        Value = (decimal)Math.Clamp(Math.Round(value), min, max),
    };

    private void AddText(TableLayoutPanel grid, string label, string value, Action<string> setter)
    {
        var tb = new TextBox { Text = value };
        AddRow(grid, label, tb);
        _commit.Add(() => setter(tb.Text.Trim()));
    }

    private static void AddNote(TableLayoutPanel grid, string text)
    {
        var lbl = new Label
        {
            Text = text, AutoSize = true, ForeColor = Theme.Dim, Margin = new Padding(3, 14, 3, 3),
            MaximumSize = new Size(540, 0),
        };
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(lbl, 0, row);
        grid.SetColumnSpan(lbl, 2);
    }
}
