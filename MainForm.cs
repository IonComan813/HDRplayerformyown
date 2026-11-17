using System.Globalization;
using System.Text;

namespace HdrPlayer;

internal sealed class MainForm : Form
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly HashSet<string> SubExts = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".vtt", ".smi", ".lrc", ".pgs" };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mka", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".thd", ".truehd", ".aac", ".m4a",
          ".flac", ".mp3", ".opus", ".ogg", ".wav", ".w64" };

    private const string VideoFilter =
        "视频文件|*.mkv;*.mp4;*.m4v;*.mov;*.ts;*.m2ts;*.mts;*.avi;*.wmv;*.webm;*.flv;*.mpg;*.mpeg;*.hevc;*.h265;*.264;*.265|所有文件|*.*";
    private const string SubFilter = "字幕文件|*.srt;*.ass;*.ssa;*.sub;*.idx;*.sup;*.vtt;*.smi|所有文件|*.*";
    private const string AudioFilter =
        "音频文件|*.mka;*.ac3;*.eac3;*.ec3;*.dts;*.thd;*.truehd;*.aac;*.m4a;*.flac;*.mp3;*.opus;*.ogg;*.wav|所有文件|*.*";

    /// <summary>mpv 窗口拿到焦点时的按键 → 动作（经 script-message 回传给本程序统一处理）。</summary>
    private static readonly (string Key, string Action)[] MpvBindings =
    {
        ("SPACE", "pause"), ("MBTN_LEFT_DBL", "fullscreen"), ("MBTN_RIGHT", "menu"), ("MBTN_MID", "pause"),
        ("LEFT", "seek-back"), ("RIGHT", "seek-fwd"), ("Shift+LEFT", "seek-back-big"), ("Shift+RIGHT", "seek-fwd-big"),
        ("UP", "vol-up"), ("DOWN", "vol-down"), ("WHEEL_UP", "vol-up"), ("WHEEL_DOWN", "vol-down"),
        ("m", "mute"), ("f", "fullscreen"), ("ENTER", "fullscreen"), ("ESC", "exit-fullscreen"),
        ("h", "hdr"), ("z", "sub-delay-dec"), ("x", "sub-delay-inc"), ("v", "sub-toggle"), ("j", "sub-cycle"),
        ("SHARP", "audio-cycle"), ("i", "info"), ("p", "playlist"), ("PGUP", "prev"), ("PGDWN", "next"),
        ("Ctrl+o", "open"), (".", "frame-step"), (",", "frame-back"),
    };

    private readonly AppSettings S;
    private Mpv? _mpv;
    private readonly string[] _startupFiles;

    // UI
    private readonly Panel _videoPanel;
    private readonly TableLayoutPanel _controlBar;
    private readonly SeekBar _seekBar, _volumeBar;
    private readonly Button _btnPrev, _btnPlay, _btnNext, _btnHdr, _btnPlaylist, _btnSettings, _btnMute, _btnFull;
    private static readonly ToolTip Tips = Theme.CreateToolTip();
    private readonly Label _lblTime, _lblFormat;
    private readonly Panel _playlistPanel;
    private readonly ListBox _playlistBox;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private InfoForm? _infoForm;

    // 播放状态
    private volatile bool _timeDirty;
    private double _timePos, _duration;
    private bool _paused = true, _idle = true;
    private bool _hdr10Plus;
    private string? _dvProfile;
    private List<PlaylistEntry> _playlist = new();

    // 全屏
    private bool _fullscreen;
    private FormWindowState _prevState;
    private Rectangle _prevBounds;
    private bool _playlistWasVisible;

    // Windows HDR
    private bool? _originalWindowsHdr;
    private float _displayPeak;

    // 关闭流程
    private bool _shuttingDown, _closeReady;

    private sealed record PlaylistEntry(int Index, string Name, string Path, bool Current);

    public MainForm(string[] args)
    {
        S = AppSettings.Load();
        _startupFiles = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();

        Text = "HdrPlayer";
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        KeyPreview = true;
        AllowDrop = true;
        MinimumSize = new Size(640, 400);
        Font = new Font("Microsoft YaHei UI", 9f);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        if (S.WinX < 0 || S.WinY < 0)
            Bounds = new Rectangle(wa.X + (wa.Width - S.WinW) / 2, wa.Y + (wa.Height - S.WinH) / 2, S.WinW, S.WinH);
        else
            Bounds = new Rectangle(S.WinX, S.WinY, S.WinW, S.WinH);
        if (S.WinMaximized) WindowState = FormWindowState.Maximized;

        // ---- 视频区域（mpv 会在这个 Panel 里创建自己的子窗口）----
        _videoPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

        // ---- 播放列表 ----
        _playlistBox = new ListBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Panel,
            ForeColor = Theme.Text, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26,
            IntegralHeight = false, HorizontalScrollbar = false,
        };
        _playlistBox.DrawItem += PlaylistDrawItem;
        _playlistBox.DoubleClick += (_, _) => PlaySelected();
        var plHeader = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 34, BackColor = Theme.Bar, Padding = new Padding(4, 3, 4, 0),
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        };
        plHeader.Controls.Add(new Label
        {
            Text = "播放列表", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(4, 6, 12, 0),
        });
        plHeader.Controls.Add(MakeSmallButton("添加", (_, _) => OpenFiles()));
        plHeader.Controls.Add(MakeSmallButton("移除", (_, _) => RemoveSelected()));
        plHeader.Controls.Add(MakeSmallButton("清空", (_, _) => _mpv?.Command("playlist-clear")));
        var plMenu = Theme.NewMenu();
        plMenu.Items.Add("播放", null, (_, _) => PlaySelected());
        plMenu.Items.Add("移除", null, (_, _) => RemoveSelected());
        plMenu.Items.Add("在资源管理器中显示", null, (_, _) => RevealSelected());
        _playlistBox.ContextMenuStrip = plMenu;
        _playlistPanel = new Panel { Dock = DockStyle.Right, Width = 320, BackColor = Theme.Panel, Visible = S.PlaylistVisible };
        _playlistPanel.Controls.Add(_playlistBox);
        _playlistPanel.Controls.Add(plHeader);

        // ---- 控制栏 ----
        _btnPrev = MakeButton(Theme.IcoPrev, "上一个 (PgUp)", (_, _) => DoAction("prev"));
        _btnPlay = MakeButton(Theme.IcoPlay, "播放/暂停 (空格)", (_, _) => DoAction("pause"));
        _btnNext = MakeButton(Theme.IcoNext, "下一个 (PgDn)", (_, _) => DoAction("next"));
        _seekBar = new SeekBar { Dock = DockStyle.Fill, Margin = new Padding(6, 0, 6, 0) };
        _seekBar.ValueRequested += (v, final) =>
            _mpv?.Command("seek", v.ToString("0.###", Inv), final ? "absolute+exact" : "absolute+keyframes");
        _seekBar.TipFormatter = v => FormatTime(v);
        _lblTime = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Text,
            Text = "00:00 / 00:00", Font = new Font("Consolas", 10f),
        };
        _lblFormat = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Dim,
            Text = "", AutoEllipsis = true,
        };
        _btnMute = MakeButton(Theme.IcoVolume, "静音 (M)", (_, _) => DoAction("mute"));
        _volumeBar = new SeekBar { Dock = DockStyle.Fill, Maximum = 150, Margin = new Padding(0, 0, 6, 0) };
        _volumeBar.Value = S.Volume;
        _volumeBar.ValueRequested += (v, _) => _mpv?.SetProperty("volume", Math.Round(v));
        _volumeBar.TipFormatter = v => $"音量 {Math.Round(v)}";
        _btnHdr = MakeButton("HDR", "开关 HDR 输出 (H)", (_, _) => DoAction("hdr"));
        _btnHdr.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _btnPlaylist = MakeButton(Theme.IcoList, "播放列表 (P)", (_, _) => DoAction("playlist"));
        _btnSettings = MakeButton(Theme.IcoSettings, "设置", (_, _) => OpenSettings());
        _btnFull = MakeButton(Theme.IcoFullscreen, "全屏 (F / 双击)", (_, _) => DoAction("fullscreen"));

        _controlBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.Bar, ColumnCount = 12, RowCount = 1,
            Padding = new Padding(4, 2, 4, 2),
        };
        float[] widths = { 40, 44, 40, -1, 150, 260, 40, 100, 56, 40, 40, 40 };
        foreach (var w in widths)
            _controlBar.ColumnStyles.Add(w < 0 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.Absolute, w));
        _controlBar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Control[] bar = { _btnPrev, _btnPlay, _btnNext, _seekBar, _lblTime, _lblFormat, _btnMute, _volumeBar, _btnHdr, _btnPlaylist, _btnSettings, _btnFull };
        for (int i = 0; i < bar.Length; i++) _controlBar.Controls.Add(bar[i], i, 0);

        Controls.Add(_videoPanel);
        Controls.Add(_playlistPanel);
        Controls.Add(_controlBar);

        _menu = BuildContextMenu();

        // 画面上的拖放由 mpv 自己处理；播放列表和控制栏上的拖放由这里处理
        foreach (var c in new Control[] { _playlistBox, _playlistPanel, _controlBar, _lblTime, _lblFormat, _seekBar, _volumeBar })
        {
            c.AllowDrop = true;
            c.DragEnter += (_, e) => OnDragEnter(e);
            c.DragDrop += (_, e) => OnDragDrop(e);
        }

        Theme.DarkTitleBar(this);
        Theme.DarkScrollBars(_playlistBox);
        _controlBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, _controlBar.Width, 0);
        };

        _uiTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _uiTimer.Tick += (_, _) => UiTick();

        UpdateHdrButton();
    }

    // =====================================================================
    //  mpv 初始化
    // =====================================================================

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            InitMpv();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "mpv 初始化失败：\n" + ex.Message, "HdrPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        _uiTimer.Start();
        if (_startupFiles.Length > 0) AddFiles(_startupFiles, replace: true);
    }

    private void InitMpv()
    {
        Directory.CreateDirectory(AppSettings.Dir);
        string inputConf = Path.Combine(AppSettings.Dir, "input.conf");
        var sb = new StringBuilder("# 由 HdrPlayer 自动生成，所有按键都交给程序统一处理\n");
        foreach (var (k, a) in MpvBindings) sb.Append(k).Append(" script-message hp ").Append(a).Append('\n');
        File.WriteAllText(inputConf, sb.ToString(), new UTF8Encoding(false));

        var m = new Mpv();
        _mpv = m;

        m.SetOption("wid", _videoPanel.Handle.ToInt64().ToString(Inv));
        m.SetOption("vo", "gpu-next");
        m.SetOption("gpu-api", S.GpuApi);
        m.SetOption("idle", "yes");
        m.SetOption("force-window", "yes");
        m.SetOption("keep-open", "no");
        m.SetOption("terminal", "no");
        m.SetOption("osc", "no");
        m.SetOption("input-default-bindings", "no");
        m.SetOption("input-builtin-bindings", "no");
        m.SetOption("input-conf", inputConf);
        m.SetOption("input-vo-keyboard", "yes");
        m.SetOption("input-cursor", "yes");
        m.SetOption("cursor-autohide", "1000");
        // 拖到画面上：字幕文件 → sub-add；视频 → 加入播放列表（空闲时自动开始播放）
        m.SetOption("drag-and-drop", "append");
        // 杜比视界：剥离 RPU，只用 HDR10 / HLG / SDR 基础层
        m.SetOption("vf", "format:dolbyvision=no");
        m.SetOption("osd-font", "Microsoft YaHei");
        m.SetOption("osd-font-size", "30");
        m.SetOption("osd-duration", "1500");
        // OSD 样式：细进度条、柔和描边
        m.SetOption("osd-color", "#FFE8E8EC");
        m.SetOption("osd-border-color", "#B0000000");
        m.SetOption("osd-border-size", "1.5");
        m.SetOption("osd-shadow-offset", "0");
        m.SetOption("osd-bar-h", "1.2");
        m.SetOption("osd-bar-w", "60");
        m.SetOption("osd-bar-align-y", "0.85");
        m.SetOption("osd-margin-x", "32");
        m.SetOption("osd-margin-y", "28");
        m.SetOption("volume-max", "150");
        m.SetOption("volume", S.Volume.ToString(Inv));
        m.SetOption("screenshot-directory", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (S.DebugLog) m.SetOption("log-file", Path.Combine(AppSettings.Dir, "mpv.log"));
        ApplyPlaybackOptions(m, initial: true);

        m.PropertyChanged += OnMpvProperty;
        m.StartFile += () => Ui(OnStartFile);
        m.FileLoaded += () => Ui(OnFileLoaded);
        m.EndFile += (r, err) => Ui(() => OnEndFile(r, err));
        m.VideoReconfig += () => Ui(UpdateFormatLabel);
        m.AudioReconfig += () => Ui(UpdateFormatLabel);
        m.ClientMessage += a => Ui(() => OnClientMessage(a));

        m.Initialize();

        m.Observe("pause", MpvFormat.Flag);
        m.Observe("idle-active", MpvFormat.Flag);
        m.Observe("time-pos", MpvFormat.Double);
        m.Observe("duration", MpvFormat.Double);
        m.Observe("volume", MpvFormat.Double);
        m.Observe("mute", MpvFormat.Flag);
        m.Observe("media-title", MpvFormat.String);
        m.Observe("playlist-count", MpvFormat.Int64);
        m.Observe("playlist-pos", MpvFormat.Int64);
        m.Observe("hwdec-current", MpvFormat.String);
        // HDR10+ 动态元数据出现时，这些子属性会变得可用
        m.Observe("video-params/scene-max-r", MpvFormat.String);
        m.Observe("video-params/scene-avg", MpvFormat.String);

        ApplyHdrState(showOsd: false);
    }

    /// <summary>可在初始化前（option）或运行时（property）应用的播放设置。</summary>
    private void ApplyPlaybackOptions(Mpv m, bool initial)
    {
        Func<string, string, int> set = initial ? m.SetOption : m.SetProperty;
        set("hwdec", S.Hwdec);
        set("scale", S.Scale);
        set("dscale", S.Dscale);
        set("deband", S.Deband ? "yes" : "no");
        set("video-sync", S.Interpolation ? "display-resample" : S.VideoSync);
        set("interpolation", S.Interpolation ? "yes" : "no");
        set("audio-device", string.IsNullOrEmpty(S.AudioDevice) ? "auto" : S.AudioDevice);
        // 5.1 / 7.1 → 立体声由 mpv（libswresample）按标准矩阵下混
        set("audio-channels", S.DownmixToStereo ? "stereo" : "auto-safe");
        set("audio-normalize-downmix", S.NormalizeDownmix ? "yes" : "no");
        set("alang", S.AudioLanguages);
        set("slang", S.SubLanguages);
        set("sub-auto", S.SubAuto);
        set("sub-font", S.SubFont);
        set("sub-font-size", S.SubFontSize.ToString(Inv));
        set("gamut-mapping-mode", S.GamutMapping);
        if (!initial) ApplyToneMapping();
    }

    // =====================================================================
    //  HDR
    // =====================================================================

    private string CurrentGdiName() => Screen.FromControl(this).DeviceName;

    private void ApplyHdrState(bool showOsd)
    {
        bool switched = false;
        if (S.AutoSwitchWindowsHdr)
        {
            string gdi = CurrentGdiName();
            var info = DisplayHelper.Query(gdi);
            if (info is { AdvancedColorSupported: true } && info.AdvancedColorEnabled != S.HdrEnabled)
            {
                _originalWindowsHdr ??= info.AdvancedColorEnabled;
                if (DisplayHelper.SetWindowsHdr(gdi, S.HdrEnabled, out string err))
                    switched = true;
                else if (showOsd)
                    _mpv?.ShowText("切换 Windows HDR 失败：" + err, 3000);
            }
        }

        ApplyMpvHdrProps();
        UpdateHdrButton();
        if (showOsd) _mpv?.ShowText(S.HdrEnabled ? "HDR 输出：开" : "HDR 输出：关（HDR 片源色调映射到 SDR）");

        if (switched)
        {
            // 显示器切换模式需要一点时间，稍后重新读取 EDID 峰值并重新应用
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); _displayPeak = 0; ApplyMpvHdrProps(); UpdateFormatLabel(); };
            t.Start();
        }
    }

    private float DisplayPeak()
    {
        if (S.TargetPeakNits > 0) return S.TargetPeakNits;
        if (_displayPeak <= 0)
        {
            var info = DisplayHelper.Query(CurrentGdiName());
            // HDR 关闭时部分驱动上报的是 SDR 亮度，低于 400 视为不可信，回落到该显示器标称的 1000 nit
            _displayPeak = info is { HasDxgi: true, MaxLuminance: >= 400 } ? info.MaxLuminance : 1000f;
        }
        return _displayPeak;
    }

    private void ApplyMpvHdrProps()
    {
        var m = _mpv;
        if (m == null) return;
        if (S.HdrEnabled)
        {
            // 让 mpv 按片源切换 swapchain 色彩空间（HDR 片源 → PQ/BT.2020，SDR 片源 → sRGB）
            m.SetProperty("target-colorspace-hint", "yes");
            m.SetProperty("target-colorspace-hint-mode", "target"); // 较新的 mpv：向显示器上报目标亮度而非片源亮度
            m.SetProperty("target-trc", "auto");
            m.SetProperty("target-prim", "auto");
            m.SetProperty("target-peak", Math.Round(DisplayPeak()).ToString(Inv));
        }
        else
        {
            m.SetProperty("target-colorspace-hint", "no");
            m.SetProperty("target-trc", "auto");
            m.SetProperty("target-prim", "auto");
            m.SetProperty("target-peak", "auto");
        }
        ApplyToneMapping();
    }

    private void ApplyToneMapping()
    {
        var m = _mpv;
        if (m == null) return;
        string tm = S.ToneMapping, peak = S.HdrComputePeak;
        if (_hdr10Plus && S.Hdr10PlusUseMetadata)
        {
            // HDR10+：使用 SMPTE ST 2094-40 动态元数据曲线，并关闭逐帧峰值检测（否则检测结果会覆盖元数据）
            tm = "st2094-40";
            peak = "no";
        }
        m.SetProperty("tone-mapping", tm);
        m.SetProperty("hdr-compute-peak", peak);
    }

    private void UpdateHdrButton()
    {
        _btnHdr.ForeColor = S.HdrEnabled ? Theme.HdrOn : Theme.Dim;
        _btnHdr.Text = S.HdrEnabled ? "HDR" : "SDR";
    }

    private void RestoreWindowsHdr()
    {
        if (!S.RestoreWindowsHdrOnExit || _originalWindowsHdr is not bool orig) return;
        try { DisplayHelper.SetWindowsHdr(CurrentGdiName(), orig, out _); } catch { }
    }

    // =====================================================================
    //  mpv 事件
    // =====================================================================

    private void Ui(Action a)
    {
        if (_shuttingDown || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    /// <summary>在 mpv 事件线程上调用。time-pos 刷新很频繁，只记录值，由 UI 定时器取用。</summary>
    private void OnMpvProperty(string name, object? value)
    {
        if (name == "time-pos")
        {
            _timePos = value is double d ? d : 0;
            _timeDirty = true;
            return;
        }
        Ui(() => OnPropertyUi(name, value));
    }

    private void OnPropertyUi(string name, object? value)
    {
        switch (name)
        {
            case "pause":
                _paused = value is true;
                _btnPlay.Text = _paused ? Theme.IcoPlay : Theme.IcoPause;
                break;
            case "idle-active":
                _idle = value is true;
                if (_idle)
                {
                    Text = "HdrPlayer";
                    _duration = 0; _timePos = 0; _timeDirty = true;
                    _lblFormat.Text = "";
                }
                break;
            case "duration":
                _duration = value is double d ? d : 0;
                _seekBar.Maximum = Math.Max(_duration, 0.001);
                _timeDirty = true;
                break;
            case "volume":
                if (value is double v) { _volumeBar.Value = v; S.Volume = v; }
                break;
            case "mute":
                _btnMute.Text = value is true ? Theme.IcoMute : Theme.IcoVolume;
                break;
            case "media-title":
                Text = value is string t && t.Length > 0 ? $"{t} - HdrPlayer" : "HdrPlayer";
                break;
            case "playlist-count":
            case "playlist-pos":
                RefreshPlaylist();
                break;
            case "hwdec-current":
                UpdateFormatLabel();
                break;
            case "video-params/scene-max-r":
            case "video-params/scene-avg":
                if (!_hdr10Plus && value is string s && double.TryParse(s, NumberStyles.Float, Inv, out double x) && x > 0)
                {
                    _hdr10Plus = true;
                    ApplyToneMapping();
                    UpdateFormatLabel();
                    _mpv?.ShowText("检测到 HDR10+ 动态元数据");
                }
                break;
        }
    }

    private void OnStartFile()
    {
        _hdr10Plus = false;
        _dvProfile = null;
        ApplyToneMapping();
    }

    private void OnFileLoaded()
    {
        var m = _mpv;
        if (m == null) return;
        _dvProfile = m.GetString("current-tracks/video/dolby-vision-profile");
        if (_dvProfile == "5")
        {
            string file = m.GetString("filename") ?? "";
            long pos = m.GetInt("playlist-pos") ?? -1, count = m.GetInt("playlist-count") ?? 0;
            if (pos >= 0 && pos < count - 1) m.Command("playlist-next", "force");
            else m.Command("stop");
            MessageBox.Show(this,
                $"{file}\n\n这是杜比视界 Profile 5 片源，没有可直接显示的 HDR10 基础层（使用 IPTPQc2 色彩空间），本播放器不支持。",
                "不兼容的杜比视界", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        UpdateFormatLabel();
    }

    private void OnEndFile(MpvEndReason reason, int error)
    {
        if (reason == MpvEndReason.Error)
            _mpv?.ShowText("播放失败：" + Mpv.ErrorString(error), 4000);
    }

    private void OnClientMessage(string[] args)
    {
        if (args.Length >= 2 && args[0] == "hp") DoAction(args[1]);
    }

    // =====================================================================
    //  动作（键盘 / 鼠标 / 菜单共用）
    // =====================================================================

    private void DoAction(string action)
    {
        var m = _mpv;
        if (m == null) return;
        string seekMode = S.ExactSeek ? "relative+exact" : "relative";
        switch (action)
        {
            case "pause":
                if (_idle && _playlist.Count > 0) m.Command("playlist-play-index", "0");
                else if (_idle) OpenFiles();
                else m.CommandString("cycle pause");
                break;
            case "seek-back": m.CommandString($"osd-msg-bar seek {(-S.SeekStep).ToString(Inv)} {seekMode}"); break;
            case "seek-fwd": m.CommandString($"osd-msg-bar seek {S.SeekStep.ToString(Inv)} {seekMode}"); break;
            case "seek-back-big": m.CommandString($"osd-msg-bar seek {(-S.SeekStepLarge).ToString(Inv)} {seekMode}"); break;
            case "seek-fwd-big": m.CommandString($"osd-msg-bar seek {S.SeekStepLarge.ToString(Inv)} {seekMode}"); break;
            case "vol-up": m.CommandString($"no-osd add volume {S.VolumeStep.ToString(Inv)}"); m.ShowText("音量 ${volume}"); break;
            case "vol-down": m.CommandString($"no-osd add volume {(-S.VolumeStep).ToString(Inv)}"); m.ShowText("音量 ${volume}"); break;
            case "mute": m.CommandString("no-osd cycle mute"); m.ShowText("静音：${mute}"); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "exit-fullscreen": if (_fullscreen) ToggleFullscreen(); break;
            case "menu": _menu.Show(Cursor.Position); break;
            case "hdr":
                S.HdrEnabled = !S.HdrEnabled;
                ApplyHdrState(showOsd: true);
                S.Save();
                break;
            case "sub-delay-dec": AdjustSubDelay(-S.SubDelayStepMs); break;
            case "sub-delay-inc": AdjustSubDelay(S.SubDelayStepMs); break;
            case "sub-toggle": m.CommandString("no-osd cycle sub-visibility"); m.ShowText("字幕显示：${sub-visibility}"); break;
            case "sub-cycle": m.CommandString("no-osd cycle sub"); m.ShowText("字幕：${sid} ${current-tracks/sub/title:}"); break;
            case "audio-cycle": m.CommandString("no-osd cycle audio"); m.ShowText("音轨：${aid} ${current-tracks/audio/title:}"); break;
            case "info": ShowInfo(); break;
            case "playlist": TogglePlaylist(); break;
            case "prev": m.Command("playlist-prev"); break;
            case "next": m.Command("playlist-next"); break;
            case "open": OpenFiles(); break;
            case "frame-step": m.CommandString("frame-step"); break;
            case "frame-back": m.CommandString("frame-back-step"); break;
        }
    }

    private void AdjustSubDelay(int ms)
    {
        var m = _mpv;
        if (m == null) return;
        m.CommandString($"no-osd add sub-delay {(ms / 1000.0).ToString(Inv)}");
        double d = m.GetDouble("sub-delay") ?? 0;
        m.ShowText($"字幕延迟：{d * 1000:+0;-0;0} ms（{(d < 0 ? "提前" : d > 0 ? "延后" : "无")}）");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 播放列表获得焦点时，上下键 / 回车 / Delete 留给列表
        if (_playlistBox.Focused)
        {
            switch (keyData)
            {
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: return base.ProcessCmdKey(ref msg, keyData);
                case Keys.Enter: PlaySelected(); return true;
                case Keys.Delete: RemoveSelected(); return true;
            }
        }

        string? action = keyData switch
        {
            Keys.Space => "pause",
            Keys.Left => "seek-back",
            Keys.Right => "seek-fwd",
            Keys.Shift | Keys.Left => "seek-back-big",
            Keys.Shift | Keys.Right => "seek-fwd-big",
            Keys.Up => "vol-up",
            Keys.Down => "vol-down",
            Keys.M => "mute",
            Keys.F => "fullscreen",
            Keys.Enter => "fullscreen",
            Keys.Escape => "exit-fullscreen",
            Keys.H => "hdr",
            Keys.Z => "sub-delay-dec",
            Keys.X => "sub-delay-inc",
            Keys.V => "sub-toggle",
            Keys.J => "sub-cycle",
            Keys.I => "info",
            Keys.P => "playlist",
            Keys.PageUp => "prev",
            Keys.PageDown => "next",
            Keys.Control | Keys.O => "open",
            Keys.OemPeriod => "frame-step",
            Keys.Oemcomma => "frame-back",
            _ => null,
        };
        if (action != null)
        {
            DoAction(action);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // =====================================================================
    //  文件 / 播放列表
    // =====================================================================

    private void AddFiles(IEnumerable<string> paths, bool replace = false)
    {
        var m = _mpv;
        if (m == null) return;
        bool first = true;
        foreach (var p in paths)
        {
            string ext = Path.GetExtension(p);
            if (SubExts.Contains(ext))
            {
                m.Command("sub-add", p, "select");
                m.ShowText("已加载字幕：" + Path.GetFileName(p));
            }
            else if (AudioExts.Contains(ext) && !_idle)
            {
                m.Command("audio-add", p, "select");
                m.ShowText("已加载音轨：" + Path.GetFileName(p));
            }
            else
            {
                m.Command("loadfile", p, first && replace ? "replace" : "append-play");
                first = false;
            }
        }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        base.OnDragEnter(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        base.OnDragDrop(e);
    }

    private void OpenFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Filter = VideoFilter, Title = "打开视频" };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
    }

    private void LoadSubtitle()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Filter = SubFilter, Title = "加载字幕" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var f in dlg.FileNames) _mpv?.Command("sub-add", f, "select");
    }

    private void LoadAudioTrack()
    {
        using var dlg = new OpenFileDialog { Filter = AudioFilter, Title = "加载外部音轨" };
        if (dlg.ShowDialog(this) == DialogResult.OK) _mpv?.Command("audio-add", dlg.FileName, "select");
    }

    private void RefreshPlaylist()
    {
        var list = new List<PlaylistEntry>();
        if (_mpv?.GetNode("playlist") is List<object?> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is not Dictionary<string, object?> d) continue;
                string path = d.GetValueOrDefault("filename") as string ?? "";
                string name = d.GetValueOrDefault("title") as string ?? Path.GetFileName(path);
                bool cur = d.GetValueOrDefault("current") is true || d.GetValueOrDefault("playing") is true;
                list.Add(new PlaylistEntry(i, name, path, cur));
            }
        }
        _playlist = list;
        int sel = _playlistBox.SelectedIndex;
        _playlistBox.BeginUpdate();
        _playlistBox.Items.Clear();
        foreach (var e in list) _playlistBox.Items.Add(e);
        if (sel >= 0 && sel < list.Count) _playlistBox.SelectedIndex = sel;
        _playlistBox.EndUpdate();
        _btnPrev.Enabled = _btnNext.Enabled = list.Count > 1;
    }

    private void PlaylistDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _playlistBox.Items.Count) return;
        var item = (PlaylistEntry)_playlistBox.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using var bg = new SolidBrush(selected ? Color.FromArgb(50, 60, 80) : Theme.Panel);
        e.Graphics.FillRectangle(bg, e.Bounds);
        var color = item.Current ? Theme.Accent : Theme.Text;
        string text = (item.Current ? "▶ " : "   ") + $"{item.Index + 1}. {item.Name}";
        TextRenderer.DrawText(e.Graphics, text, _playlistBox.Font, Rectangle.Inflate(e.Bounds, -6, 0), color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void PlaySelected()
    {
        if (_playlistBox.SelectedItem is PlaylistEntry e)
            _mpv?.Command("playlist-play-index", e.Index.ToString(Inv));
    }

    private void RemoveSelected()
    {
        if (_playlistBox.SelectedItem is PlaylistEntry e)
            _mpv?.Command("playlist-remove", e.Index.ToString(Inv));
    }

    private void RevealSelected()
    {
        if (_playlistBox.SelectedItem is PlaylistEntry e && File.Exists(e.Path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{e.Path}\"");
    }

    private void TogglePlaylist()
    {
        if (_fullscreen) return;
        _playlistPanel.Visible = !_playlistPanel.Visible;
        S.PlaylistVisible = _playlistPanel.Visible;
    }

    // =====================================================================
    //  右键菜单
    // =====================================================================

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = Theme.NewMenu();
        // 菜单为空时 WinForms 会默认取消打开，填充后要显式设回 false
        menu.Opening += (_, e) => { PopulateMenu(menu); e.Cancel = false; };
        return menu;
    }

    private void PopulateMenu(ContextMenuStrip menu)
    {
        var m = _mpv;
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("视频参数…", null, (_, _) => ShowInfo()) { ShortcutKeyDisplayString = "I" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("打开文件…", null, (_, _) => OpenFiles()) { ShortcutKeyDisplayString = "Ctrl+O" });
        menu.Items.Add(new ToolStripMenuItem("加载字幕…", null, (_, _) => LoadSubtitle()) { Enabled = !_idle });
        menu.Items.Add(new ToolStripMenuItem("加载外部音轨…", null, (_, _) => LoadAudioTrack()) { Enabled = !_idle });
        menu.Items.Add(new ToolStripSeparator());

        var audioMenu = new ToolStripMenuItem("音轨");
        var subMenu = new ToolStripMenuItem("字幕");
        subMenu.DropDownItems.Add(new ToolStripMenuItem("关闭字幕", null, (_, _) => m?.SetProperty("sid", "no"))
        {
            Checked = m?.GetString("sid") is "no" or null,
        });
        if (m?.GetNode("track-list") is List<object?> tracks)
        {
            foreach (var t in tracks.OfType<Dictionary<string, object?>>())
            {
                string type = t.GetValueOrDefault("type") as string ?? "";
                if (type != "audio" && type != "sub") continue;
                long id = t.GetValueOrDefault("id") is long l ? l : 0;
                string label = TrackLabel(t);
                bool selected = t.GetValueOrDefault("selected") is true;
                var item = new ToolStripMenuItem(label) { Checked = selected };
                string prop = type == "audio" ? "aid" : "sid";
                item.Click += (_, _) => m?.SetProperty(prop, id.ToString(Inv));
                (type == "audio" ? audioMenu : subMenu).DropDownItems.Add(item);
            }
        }
        audioMenu.Enabled = audioMenu.DropDownItems.Count > 0;
        menu.Items.Add(audioMenu);
        menu.Items.Add(subMenu);

        var delayMenu = new ToolStripMenuItem("字幕延迟");
        delayMenu.DropDownItems.Add(new ToolStripMenuItem($"提前 {S.SubDelayStepMs} ms", null, (_, _) => AdjustSubDelay(-S.SubDelayStepMs)) { ShortcutKeyDisplayString = "Z" });
        delayMenu.DropDownItems.Add(new ToolStripMenuItem($"延后 {S.SubDelayStepMs} ms", null, (_, _) => AdjustSubDelay(S.SubDelayStepMs)) { ShortcutKeyDisplayString = "X" });
        delayMenu.DropDownItems.Add(new ToolStripMenuItem("重置", null, (_, _) => { m?.SetProperty("sub-delay", "0"); m?.ShowText("字幕延迟：0 ms"); }));
        menu.Items.Add(delayMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("HDR 输出", null, (_, _) => DoAction("hdr"))
        {
            Checked = S.HdrEnabled, ShortcutKeyDisplayString = "H",
        });
        var decMenu = new ToolStripMenuItem("解码方式");
        foreach (var (val, name) in SettingsForm.HwdecChoices)
        {
            string v = val;
            decMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                S.Hwdec = v;
                m?.SetProperty("hwdec", v);
                m?.ShowText("解码方式：" + name);
                S.Save();
            }) { Checked = S.Hwdec == v });
        }
        menu.Items.Add(decMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("播放列表", null, (_, _) => TogglePlaylist())
        {
            Checked = _playlistPanel.Visible, ShortcutKeyDisplayString = "P",
        });
        menu.Items.Add(new ToolStripMenuItem("全屏", null, (_, _) => ToggleFullscreen())
        {
            Checked = _fullscreen, ShortcutKeyDisplayString = "F / 双击",
        });
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Close()));
        StyleDropDowns(menu.Items);
    }

    /// <summary>子菜单也套上深色渲染器。</summary>
    private static void StyleDropDowns(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            if (!item.HasDropDownItems) continue;
            item.DropDown.Renderer = Theme.MenuRenderer;
            item.DropDown.BackColor = Theme.Surface;
            item.DropDown.ForeColor = Theme.Text;
            StyleDropDowns(item.DropDownItems);
        }
    }

    private static string TrackLabel(Dictionary<string, object?> t)
    {
        var parts = new List<string>();
        if (t.GetValueOrDefault("id") is long id) parts.Add("#" + id);
        if (t.GetValueOrDefault("lang") is string lang) parts.Add($"[{lang}]");
        if (t.GetValueOrDefault("title") is string title) parts.Add(title);
        if (t.GetValueOrDefault("codec") is string codec) parts.Add(codec);
        if (t.GetValueOrDefault("demux-channel-count") is long ch) parts.Add(ch + "ch");
        if (t.GetValueOrDefault("default") is true) parts.Add("(默认)");
        if (t.GetValueOrDefault("external") is true) parts.Add("(外部)");
        return string.Join(" ", parts);
    }

    // =====================================================================
    //  窗口 / 全屏 / UI 刷新
    // =====================================================================

    private void ToggleFullscreen()
    {
        SuspendLayout();
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _prevBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _playlistWasVisible = _playlistPanel.Visible;
            _playlistPanel.Visible = false;
            _controlBar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _prevState;
            if (_prevState == FormWindowState.Normal) Bounds = _prevBounds;
            _controlBar.Visible = true;
            _playlistPanel.Visible = _playlistWasVisible;
            _fullscreen = false;
        }
        ResumeLayout();
    }

    private void UiTick()
    {
        if (_timeDirty)
        {
            _timeDirty = false;
            _seekBar.Value = _timePos;
            _lblTime.Text = $"{FormatTime(_timePos)} / {FormatTime(_duration)}";
        }

        // 全屏时鼠标移到屏幕底部显示控制栏
        if (_fullscreen)
        {
            var p = PointToClient(Cursor.Position);
            bool inside = ClientRectangle.Contains(p) && ActiveForm == this;
            int threshold = _controlBar.Visible ? _controlBar.Height + 30 : 60;
            bool show = inside && p.Y >= ClientSize.Height - threshold;
            if (show != _controlBar.Visible) _controlBar.Visible = show;
        }
    }

    private static string FormatTime(double sec)
    {
        if (double.IsNaN(sec) || sec < 0) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>控制栏上显示：片源 HDR 格式 · 解码方式 · 声道下混情况。</summary>
    private void UpdateFormatLabel()
    {
        var m = _mpv;
        if (m == null || _idle) { _lblFormat.Text = ""; return; }
        var parts = new List<string>();

        string? gamma = m.GetString("video-params/gamma");
        string hdr = gamma switch
        {
            "pq" => _hdr10Plus ? "HDR10+" : "HDR10",
            "hlg" => "HLG",
            null => "",
            _ => "SDR",
        };
        if (_dvProfile != null) hdr = $"DV P{_dvProfile}→{(hdr.Length > 0 ? hdr : "基础层")}";
        if (hdr.Length > 0) parts.Add(hdr);

        string? hw = m.GetString("hwdec-current");
        if (!string.IsNullOrEmpty(m.GetString("video-codec")))
            parts.Add(string.IsNullOrEmpty(hw) || hw == "no" ? "软解" : "硬解");

        long? inCh = m.GetInt("audio-params/channel-count");
        long? outCh = m.GetInt("audio-out-params/channel-count");
        if (inCh is long a && outCh is long b)
            parts.Add(a == b ? $"{ChName(a)}" : $"{ChName(a)}→{ChName(b)}");

        _lblFormat.Text = string.Join(" · ", parts);
    }

    private static string ChName(long ch) => ch switch
    {
        1 => "单声道", 2 => "立体声", 6 => "5.1", 8 => "7.1", _ => ch + "ch",
    };

    private static Button MakeButton(string text, string tip, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text,
            BackColor = Theme.Bar, TabStop = false, Margin = new Padding(1),
            Font = Theme.IconFont(12f), Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.FlatAppearance.MouseDownBackColor = Theme.Surface;
        b.Click += onClick;
        Tips.SetToolTip(b, tip);
        return b;
    }

    private static Button MakeSmallButton(string text, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Theme.Text, BackColor = Theme.Panel,
            TabStop = false, Margin = new Padding(2, 0, 2, 0), Height = 26,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.Hover;
        b.Cursor = Cursors.Hand;
        b.Click += onClick;
        return b;
    }

    // =====================================================================
    //  信息窗口 / 设置窗口
    // =====================================================================

    private void ShowInfo()
    {
        if (_infoForm == null || _infoForm.IsDisposed)
        {
            _infoForm = new InfoForm(BuildInfoText);
            _infoForm.Show(this);
        }
        else
        {
            _infoForm.RefreshText();
            _infoForm.Activate();
        }
    }

    private void OpenSettings()
    {
        var m = _mpv;
        if (m == null) return;
        bool wasFull = _fullscreen;
        if (wasFull) ToggleFullscreen();
        var copy = S.Clone();
        using var dlg = new SettingsForm(copy, m);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        bool gpuApiChanged = copy.GpuApi != S.GpuApi;
        bool hdrChanged = copy.HdrEnabled != S.HdrEnabled || copy.AutoSwitchWindowsHdr != S.AutoSwitchWindowsHdr ||
                          copy.TargetPeakNits != S.TargetPeakNits;
        CopySettings(copy, S);
        S.Save();
        ApplyPlaybackOptions(m, initial: false);
        if (hdrChanged) { _displayPeak = 0; ApplyHdrState(showOsd: true); }
        else ApplyMpvHdrProps();
        UpdateHdrButton();
        if (gpuApiChanged)
            MessageBox.Show(this, "渲染 API 的修改将在重启播放器后生效。", "HdrPlayer", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void CopySettings(AppSettings from, AppSettings to)
    {
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
            p.SetValue(to, p.GetValue(from));
    }

    /// <summary>右键“视频参数”窗口的内容。</summary>
    private string BuildInfoText()
    {
        var m = _mpv;
        var sb = new StringBuilder();
        if (m == null) return "";

        string P(string name) => m.GetString(name) is { Length: > 0 } s ? s : "—";
        string O(string name) => m.GetOsdString(name) is { Length: > 0 } s ? s : "—";
        void Line(string k, string v) => sb.Append("  ").Append(k.PadRight(14 - CountWide(k))).Append(v).Append("\r\n");
        void Head(string h) => sb.Append("\r\n【").Append(h).Append("】\r\n");

        if (_idle)
        {
            sb.Append("当前没有在播放。\r\n");
        }
        else
        {
            Head("文件");
            Line("路径", P("path"));
            Line("容器", P("file-format"));
            Line("大小", O("file-size"));
            Line("时长", FormatTime(_duration));
            Line("位置", $"{FormatTime(_timePos)}");

            Head("视频");
            Line("编码", $"{P("video-format")}  ({P("video-codec")})");
            Line("分辨率", $"{P("video-params/w")} × {P("video-params/h")}  显示 {P("video-params/dw")} × {P("video-params/dh")}");
            Line("帧率", $"容器 {P("container-fps")}  实际 {O("estimated-vf-fps")}  显示器 {O("display-fps")} Hz");
            Line("码率", O("video-bitrate"));
            Line("像素格式", $"{P("video-params/pixelformat")}  硬件格式 {P("video-params/hw-pixelformat")}");
            Line("色彩矩阵", $"{P("video-params/colormatrix")}  范围 {P("video-params/colorlevels")}");
            Line("原色", P("video-params/primaries"));
            Line("传输特性", P("video-params/gamma"));
            string gamma = P("video-params/gamma");
            string fmt = gamma switch { "pq" => _hdr10Plus ? "HDR10+ (PQ + ST 2094-40 动态元数据)" : "HDR10 (PQ + 静态元数据)", "hlg" => "HLG", "—" => "—", _ => "SDR" };
            Line("HDR 格式", fmt);
            if (gamma is "pq" or "hlg")
            {
                Line("MaxCLL/FALL", $"{P("video-params/max-cll")} / {P("video-params/max-fall")} nit");
                Line("母版亮度", $"{P("video-params/min-luma")} – {P("video-params/max-luma")} nit");
                if (double.TryParse(m.GetString("video-params/sig-peak"), NumberStyles.Float, Inv, out double sp))
                    Line("信号峰值", $"{sp * 203:0} nit (sig-peak {sp:0.###})");
            }
            if (_hdr10Plus)
            {
                Line("HDR10+ 场景", $"maxRGB R/G/B {P("video-params/scene-max-r")} / {P("video-params/scene-max-g")} / {P("video-params/scene-max-b")} nit");
                Line("", $"场景平均 {P("video-params/scene-avg")} nit");
            }
            if (_dvProfile != null)
                Line("杜比视界", $"Profile {_dvProfile}  Level {P("current-tracks/video/dolby-vision-level")}  → 已剥离 RPU，仅播放基础层");
            string hw = P("hwdec-current");
            Line("解码", hw is "—" or "no" ? "软解 (CPU)" : $"硬解 ({hw})");
            Line("丢帧", $"解码 {P("decoder-frame-drop-count")}  输出 {P("frame-drop-count")}  延迟 {P("vo-delayed-frame-count")}");

            Head("渲染输出");
            Line("VO", $"{P("current-vo")}  API {P("gpu-api")}");
            Line("输出色彩", $"{P("video-target-params/primaries")} / {P("video-target-params/gamma")}  峰值 {P("video-target-params/max-luma")} nit");
            Line("HDR 输出", S.HdrEnabled ? $"开  目标峰值 {Math.Round(DisplayPeak())} nit" : "关（色调映射到 SDR）");
            Line("色调映射", $"{P("tone-mapping")}  峰值检测 {P("hdr-compute-peak")}  色域映射 {P("gamut-mapping-mode")}");
            Line("缩放", $"{P("scale")} / {P("dscale")}  去色带 {P("deband")}");

            Head("音频");
            Line("编码", $"{P("audio-codec-name")}  ({P("audio-codec")})");
            Line("输入声道", $"{P("audio-params/channel-count")} ch  {P("audio-params/hr-channels")}");
            Line("输出声道", $"{P("audio-out-params/channel-count")} ch  {P("audio-out-params/hr-channels")}" +
                (S.DownmixToStereo ? "  (下混到立体声)" : ""));
            Line("采样率", $"{P("audio-params/samplerate")} Hz  {P("audio-params/format")}");
            Line("码率", O("audio-bitrate"));
            Line("输出设备", $"{P("audio-device")}  ({P("current-ao")})");
            Line("音频延迟", $"{(m.GetDouble("audio-delay") ?? 0) * 1000:0} ms");

            Head("字幕");
            Line("当前", $"#{P("sid")} {P("current-tracks/sub/lang")} {P("current-tracks/sub/title")} {P("current-tracks/sub/codec")}");
            Line("字幕延迟", $"{(m.GetDouble("sub-delay") ?? 0) * 1000:0} ms");
        }

        Head("显示器");
        var d = DisplayHelper.Query(CurrentGdiName());
        if (d != null)
        {
            Line("名称", $"{(d.FriendlyName.Length > 0 ? d.FriendlyName : "?")}  ({d.GdiName})");
            Line("Windows HDR", d.AdvancedColorSupported ? (d.AdvancedColorEnabled ? "开" : "关") : "不支持");
            if (d.HasDxgi)
            {
                Line("位深", $"{d.DxgiBitsPerColor} bit  色彩空间 {(d.DxgiIsHdr ? "HDR10 (PQ/BT.2020)" : "sRGB")}");
                Line("EDID 亮度", $"峰值 {d.MaxLuminance:0} nit  全屏 {d.MaxFullFrameLuminance:0} nit  最低 {d.MinLuminance:0.####} nit");
                Line("EDID 原色", $"R({d.Red[0]:0.000},{d.Red[1]:0.000}) G({d.Green[0]:0.000},{d.Green[1]:0.000}) " +
                                 $"B({d.Blue[0]:0.000},{d.Blue[1]:0.000}) W({d.White[0]:0.0000},{d.White[1]:0.0000})");
            }
        }
        return sb.ToString();
    }

    private static int CountWide(string s) => s.Count(c => c > 0x2E80);

    // =====================================================================
    //  关闭
    // =====================================================================

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closeReady && _mpv != null)
        {
            e.Cancel = true;
            if (_shuttingDown) return;

            if (_fullscreen) ToggleFullscreen();
            S.WinMaximized = WindowState == FormWindowState.Maximized;
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            (S.WinX, S.WinY, S.WinW, S.WinH) = (b.X, b.Y, b.Width, b.Height);
            S.Save();

            _uiTimer.Stop();
            _infoForm?.Close();
            Hide();
            _shuttingDown = true;
            var m = _mpv;
            _mpv = null;
            // 在后台线程销毁 mpv，UI 线程继续处理消息，避免与 mpv 窗口线程互相等待
            Task.Run(() => { try { m.Dispose(); } catch { } }).ContinueWith(_ =>
            {
                try
                {
                    BeginInvoke(() =>
                    {
                        RestoreWindowsHdr();
                        _closeReady = true;
                        Close();
                    });
                }
                catch { }
            });
            return;
        }
        base.OnFormClosing(e);
    }
}
