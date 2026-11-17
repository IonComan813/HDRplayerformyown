using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace HdrPlayer;

/// <summary>播放列表里的一项（XAML 模板里绑定 Display / Path / Current）。</summary>
public sealed record PlaylistEntry(int Index, string Display, string Path, bool Current);

public partial class MainWindow : FluentWindow
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
        ("SPACE", "pause"), ("MBTN_LEFT", "click"), ("MBTN_LEFT_DBL", "dblclick"), ("MBTN_RIGHT", "menu"), ("MBTN_MID", "pause"),
        ("LEFT", "seek-back"), ("RIGHT", "seek-fwd"), ("Shift+LEFT", "seek-back-big"), ("Shift+RIGHT", "seek-fwd-big"),
        ("UP", "vol-up"), ("DOWN", "vol-down"), ("WHEEL_UP", "vol-up"), ("WHEEL_DOWN", "vol-down"),
        ("m", "mute"), ("f", "fullscreen"), ("ENTER", "fullscreen"), ("ESC", "exit-fullscreen"),
        ("h", "hdr"), ("z", "sub-delay-dec"), ("x", "sub-delay-inc"), ("v", "sub-toggle"), ("j", "sub-cycle"),
        ("SHARP", "audio-cycle"), ("i", "info"), ("p", "playlist"), ("PGUP", "prev"), ("PGDWN", "next"),
        ("Ctrl+o", "open"), (".", "frame-step"), (",", "frame-back"), ("F5", "reload"),
    };

    /// <summary>双击判定时间，和 mpv 的 input-doubleclick-time 保持一致。单击画面要等这么久确认不是双击才暂停。</summary>
    private const int DoubleClickMs = 300;

    /// <summary>鼠标静止多久后隐藏悬浮控制栏（mpv 的鼠标指针也同时隐藏）。</summary>
    private const int BarHideMs = 2000;
    /// <summary>悬浮控制栏的最大宽度（DIP），超宽屏 / 全屏时居中显示。</summary>
    private const double BarMaxWidth = 1280;

    private readonly AppSettings S;
    private Mpv? _mpv;
    private readonly string[] _startupFiles;
    private readonly ControlBar _bar;
    private readonly Thumbnailer _thumbs = new();
    private readonly DispatcherTimer _uiTimer;
    private InfoWindow? _infoWindow;
    private IntPtr _hwnd;

    // 播放状态
    private volatile bool _timeDirty;
    private double _timePos, _duration;
    private bool _paused = true, _idle = true;
    private bool _hdr10Plus;
    private string? _dvProfile;
    private List<PlaylistEntry> _playlist = new();

    // 悬浮控制栏
    private Native.POINT _lastCursor;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _menuOpen;

    // 全屏
    private bool _fullscreen;
    private WindowState _prevState;
    private Rect _prevBounds;
    private bool _playlistWasVisible;

    // HDR：_hdrOn 是本次运行的输出状态。启动时跟随 Windows 当前的 HDR 开关（空着启动不改系统设置），
    // 打开 HDR 片源时自动开启；_originalWindowsHdr 记录第一次切换前的系统状态，退出时还原。
    private bool _hdrOn;
    private bool _autoHdrChecked;
    private bool? _originalWindowsHdr;
    private float _displayPeak;

    // 单击画面暂停
    private DispatcherTimer? _clickTimer;
    private DateTime _activatedAt = DateTime.MinValue, _lastDblClick = DateTime.MinValue;

    // 重新载入当前文件：期间临时设置了 mpv 的 start 选项，载入后要清掉
    private bool _reloading;

    // 关闭流程
    private bool _shuttingDown, _closeReady;

    public MainWindow(string[] args)
    {
        S = AppSettings.Load();
        _startupFiles = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();

        InitializeComponent();
        RestorePlacement();
        PlaylistPanel.Visibility = S.PlaylistVisible ? Visibility.Visible : Visibility.Collapsed;

        // ---- 悬浮控制栏 ----
        _bar = new ControlBar { FormatTime = FormatTime };
        _bar.BtnOpen.Click += (_, _) => DoAction("open");
        _bar.BtnPrev.Click += (_, _) => DoAction("prev");
        _bar.BtnPlay.Click += (_, _) => DoAction("pause");
        _bar.BtnNext.Click += (_, _) => DoAction("next");
        _bar.BtnMute.Click += (_, _) => DoAction("mute");
        _bar.BtnHdr.Click += (_, _) => DoAction("hdr");
        _bar.BtnPlaylist.Click += (_, _) => DoAction("playlist");
        _bar.BtnSettings.Click += (_, _) => OpenSettings();
        _bar.BtnFull.Click += (_, _) => DoAction("fullscreen");
        _bar.BtnReload.Click += (_, _) => DoAction("reload");
        _bar.KeyForwarded += e => OnPreviewKeyDown(_bar, e);
        _bar.SeekRequested += (v, final) =>
            _mpv?.Command("seek", v.ToString("0.###", Inv), final ? "absolute+exact" : "absolute+keyframes");
        _bar.VolumeRequested += v => _mpv?.SetProperty("volume", v);
        _bar.ThumbnailRequested += t => _thumbs.Request(t);
        _thumbs.ThumbnailReady += (_, at, bmp) =>
        {
            if (!_shuttingDown) Dispatcher.InvokeAsync(() => _bar.ShowThumbnail(at, bmp));
        };
        _bar.SetVolume(S.Volume);
        _bar.BtnPrev.IsEnabled = _bar.BtnNext.IsEnabled = false;
        _bar.DragOver += OnFileDragOver;
        _bar.Drop += OnFileDrop;
        _bar.SizeChanged += (_, _) => PositionBar();
        _bar.MouseRightButtonUp += (_, e) => { ShowContextMenu(); e.Handled = true; };

        // ---- 播放列表 ----
        BtnPlAdd.Click += (_, _) => OpenFiles();
        BtnPlRemove.Click += (_, _) => RemoveSelected();
        BtnPlClear.Click += (_, _) => _mpv?.Command("playlist-clear");
        BtnPlClose.Click += (_, _) => TogglePlaylist();
        PlMenuPlay.Click += (_, _) => PlaySelected();
        PlMenuRemove.Click += (_, _) => RemoveSelected();
        PlMenuReveal.Click += (_, _) => RevealSelected();
        PlMenuInfo.Click += (_, _) => { if (PlaylistBox.SelectedItem is PlaylistEntry e) ShowEntryInfo(e); };
        PlaylistBox.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(PlaylistBox, e.OriginalSource as DependencyObject) is ListBoxItem)
                PlaySelected();
        };

        // 画面上的拖放由 mpv 自己处理；播放列表、标题栏和控制栏上的拖放由这里处理
        DragOver += OnFileDragOver;
        Drop += OnFileDrop;
        PreviewKeyDown += OnPreviewKeyDown;

        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
        Activated += (_, _) => _activatedAt = DateTime.UtcNow;
        ContentRendered += OnContentRendered;
        LocationChanged += (_, _) => PositionBar();
        SizeChanged += (_, _) => PositionBar();
        VideoFrame.SizeChanged += (_, _) => PositionBar();
        DpiChanged += (_, _) => PositionBar();
        StateChanged += (_, _) =>
        {
            UpdateFrameMargins();
            if (WindowState == WindowState.Minimized) _bar.HideNow();
            else PositionBar();
        };
        UpdateFrameMargins();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UiTick();

        UpdateHdrButton();
    }

    private void RestorePlacement()
    {
        var work = SystemParameters.WorkArea;
        double w = Math.Clamp(S.WinW, MinWidth, Math.Max(MinWidth, work.Width));
        double h = Math.Clamp(S.WinH, MinHeight, Math.Max(MinHeight, work.Height));
        var saved = new Rect(S.WinX, S.WinY, w, h);
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        bool unset = S.WinX == -1 && S.WinY == -1;
        if (unset || !virt.IntersectsWith(saved))
            saved = new Rect(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
        (Left, Top, Width, Height) = (saved.X, saved.Y, saved.Width, saved.Height);
        if (S.WinMaximized) WindowState = WindowState.Maximized;
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        _bar.Owner = this;
        try
        {
            if (Video.Hwnd == IntPtr.Zero) throw new InvalidOperationException("视频窗口没有创建");
            InitMpv();
        }
        catch (Exception ex)
        {
            await Dialogs.ShowAsync(this, "HdrPlayer", "mpv 初始化失败：\n" + ex.Message);
            _closeReady = true;
            Close();
            return;
        }
        _uiTimer.Start();
        if (_startupFiles.Length > 0) AddFiles(_startupFiles, replace: true);
        PositionBar();
        _bar.Reveal();
    }

    // =====================================================================
    //  mpv 初始化
    // =====================================================================

    private void InitMpv()
    {
        Directory.CreateDirectory(AppSettings.Dir);
        string inputConf = Path.Combine(AppSettings.Dir, "input.conf");
        var sb = new StringBuilder("# 由 HdrPlayer 自动生成，所有按键都交给程序统一处理\n");
        foreach (var (k, a) in MpvBindings) sb.Append(k).Append(" script-message hp ").Append(a).Append('\n');
        File.WriteAllText(inputConf, sb.ToString(), new UTF8Encoding(false));

        var m = new Mpv();
        _mpv = m;

        m.SetOption("wid", Video.Hwnd.ToInt64().ToString(Inv));
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
        m.SetOption("input-doubleclick-time", DoubleClickMs.ToString(Inv));
        // 和悬浮控制栏同时隐藏
        m.SetOption("cursor-autohide", BarHideMs.ToString(Inv));
        // 拖到画面上：字幕文件 → sub-add；视频 → 加入播放列表（空闲时自动开始播放）
        m.SetOption("drag-and-drop", "append");
        // 杜比视界：默认保留 RPU，由 gpu-next（libplacebo）做重塑，Profile 5 也能正确显示
        m.SetOption("vf", DolbyVisionFilter());
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
        m.VideoReconfig += () => Ui(OnVideoReconfig);
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

        // 启动时不碰 Windows 的 HDR 开关：只读取当前状态，播放器的 HDR 输出跟随它
        _hdrOn = WindowsHdrIsOn();
        ApplyMpvHdrProps();
        UpdateHdrButton();
    }

    /// <summary>
    /// 杜比视界处理方式对应的视频滤镜。mpv 默认就保留 RPU（dolbyvision=yes），所以“应用 RPU”时不加滤镜；
    /// 只有“剥离 RPU”时才用 format 滤镜去掉元数据。
    /// </summary>
    private string DolbyVisionFilter() => S.DolbyVisionMode == "strip" ? "format:dolbyvision=no" : "";

    private bool DvStripped => S.DolbyVisionMode == "strip";

    /// <summary>可在初始化前（option）或运行时（property）应用的播放设置。</summary>
    private void ApplyPlaybackOptions(Mpv m, bool initial)
    {
        Func<string, string, int> set = initial ? m.SetOption : m.SetProperty;
        set("hwdec", S.Hwdec);
        // 软解线程数：运行时修改从下一个文件开始生效（解码器重新初始化时）
        set("vd-lavc-threads", Math.Clamp(S.DecoderThreads, 0, 64).ToString(Inv));
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

    private string CurrentGdiName() => _hwnd != IntPtr.Zero ? Native.MonitorOf(_hwnd).Device : "";

    private bool WindowsHdrIsOn()
    {
        var info = DisplayHelper.Query(CurrentGdiName());
        return info is { AdvancedColorSupported: true, AdvancedColorEnabled: true } || info is { HasDxgi: true, DxgiIsHdr: true };
    }

    /// <summary>
    /// 开关 HDR 输出。manual = 用户按 H / 按钮 / 菜单；否则是按片源自动切换（HDR 片源开，SDR 片源关）。
    /// 只有这里会去改 Windows 的“使用 HDR”。
    /// </summary>
    private void SetHdr(bool on, bool manual)
    {
        var m = _mpv;
        if (m == null || on == _hdrOn) return;

        bool switched = false;
        string gdi = CurrentGdiName();
        var info = DisplayHelper.Query(gdi);
        if (S.AutoSwitchWindowsHdr && info is { AdvancedColorSupported: true } && info.AdvancedColorEnabled != on)
        {
            bool before = info.AdvancedColorEnabled;
            if (DisplayHelper.SetWindowsHdr(gdi, on, out string err))
            {
                _originalWindowsHdr ??= before;
                switched = true;
            }
            else
            {
                m.ShowText("切换 Windows HDR 失败：" + err, 3000);
                if (!manual) return;
            }
        }
        // 自动开启时，如果系统 HDR 既没开、也没能切换（显示器不支持 / 设置里不允许切换），就什么都不做
        if (!manual && on && !switched && !WindowsHdrIsOn()) return;

        _hdrOn = on;
        ApplyMpvHdrProps();
        UpdateHdrButton();
        m.ShowText(!manual ? (on ? "HDR 片源，已开启 HDR 输出" : "SDR 片源，已关闭 HDR 输出")
                   : on ? "HDR 输出：开" : "HDR 输出：关（HDR 片源色调映射到 SDR）");

        if (switched)
        {
            // 显示器切换模式（黑屏一下）需要一点时间：自动切换时先暂停，免得错过开头；
            // 稍后重新读取 EDID 峰值、重新应用 HDR 参数，再继续播放
            bool resume = !manual && !_idle && m.GetFlag("pause") == false;
            if (resume) m.SetProperty("pause", true);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _displayPeak = 0;
                ApplyMpvHdrProps();
                UpdateFormatLabel();
                if (resume) _mpv?.SetProperty("pause", false);
            };
            t.Start();
        }
    }

    /// <summary>
    /// 视频参数确定后（每个文件第一次 video-reconfig）按片源自动切换：HDR 片源开启 HDR，SDR 片源关闭 HDR。
    /// </summary>
    private void MaybeAutoHdr()
    {
        var m = _mpv;
        if (_autoHdrChecked || m == null || _idle) return;
        string? gamma = m.GetString("video-params/gamma");
        if (gamma == null) return; // 还没解码出第一帧（纯音频文件永远不会到这里）
        _autoHdrChecked = true;
        if (!S.AutoHdrForHdrVideo) return;
        // 音乐文件的封面图也算“视频轨”，不能因为它把 HDR 关掉
        if (m.GetFlag("current-tracks/video/image") == true) return;
        bool hdrSource = gamma is "pq" or "hlg" || _dvProfile != null;
        if (hdrSource != _hdrOn) SetHdr(hdrSource, manual: false);
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
        if (_hdrOn)
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
        _bar.BtnHdr.Content = _hdrOn ? "HDR" : "SDR";
        _bar.BtnHdr.Foreground = (Brush)FindResource(_hdrOn ? "HdrOnBrush" : "TextFillColorSecondaryBrush");
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
        if (_shuttingDown) return;
        try { Dispatcher.InvokeAsync(a); } catch (InvalidOperationException) { }
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
                _bar.PlayIcon.Symbol = _paused ? SymbolRegular.Play24 : SymbolRegular.Pause24;
                break;
            case "idle-active":
                _idle = value is true;
                if (_idle)
                {
                    SetTitle("HdrPlayer");
                    _duration = 0; _timePos = 0; _timeDirty = true;
                    _bar.SetDuration(0);
                    _bar.FormatText.Text = "";
                    UpdateThumbnailSource();
                }
                ShowIdleHint(_idle);
                break;
            case "duration":
                _duration = value is double d ? d : 0;
                _bar.SetDuration(_duration);
                _bar.ThumbnailTolerance = Math.Max(3, _duration / 120);
                _timeDirty = true;
                break;
            case "volume":
                if (value is double v) { _bar.SetVolume(v); S.Volume = v; }
                break;
            case "mute":
                _bar.MuteIcon.Symbol = value is true ? SymbolRegular.SpeakerMute24 : SymbolRegular.Speaker224;
                _bar.SetMuted(value is true);
                break;
            case "media-title":
                SetTitle(value is string t && t.Length > 0 ? $"{t} - HdrPlayer" : "HdrPlayer");
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

    private void SetTitle(string text)
    {
        Title = text;
        AppTitleBar.Title = text;
    }

    /// <summary>没有在播放时，画面中央显示一行提示。</summary>
    private void ShowIdleHint(bool show)
    {
        var m = _mpv;
        if (m == null) return;
        if (show)
            m.Command("osd-overlay", "1", "ass-events", @"{\an5\fs26\bord1\1c&HC8C8C8&}拖入视频文件，或按 Ctrl+O 打开");
        else
            m.Command("osd-overlay", "1", "none", "");
    }

    private void OnStartFile()
    {
        _hdr10Plus = false;
        _dvProfile = null;
        _autoHdrChecked = false;
        ApplyToneMapping();
    }

    private async void OnFileLoaded()
    {
        var m = _mpv;
        if (m == null) return;
        if (_reloading)
        {
            // start 是全局选项，不清掉的话之后打开的每个文件都会从这个位置开始
            _reloading = false;
            m.SetProperty("start", "none");
        }
        _dvProfile = m.GetString("current-tracks/video/dolby-vision-profile");
        // 剥离 RPU 时 Profile 5 没有可用的基础层（IPTPQc2 色彩空间），只能跳过；应用 RPU 时可以正常播放
        if (_dvProfile == "5" && DvStripped)
        {
            string file = m.GetString("filename") ?? "";
            long pos = m.GetInt("playlist-pos") ?? -1, count = m.GetInt("playlist-count") ?? 0;
            if (pos >= 0 && pos < count - 1) m.Command("playlist-next", "force");
            else m.Command("stop");
            await Dialogs.ShowAsync(this, "不兼容的杜比视界",
                $"{file}\n\n这是杜比视界 Profile 5 片源，没有可直接显示的 HDR10 基础层（使用 IPTPQc2 色彩空间）。\n" +
                "当前设置为“剥离 RPU”，无法播放。请在 设置 → HDR → 杜比视界 中改为“应用 RPU”。");
            return;
        }
        UpdateFormatLabel();
        UpdateThumbnailSource();
        MaybeAutoHdr();
    }

    /// <summary>当前文件是有画面的本地文件、且设置里开启时，让缩略图线程跟着打开它。</summary>
    private void UpdateThumbnailSource()
    {
        var m = _mpv;
        string? path = m == null || _idle ? null : m.GetString("path");
        bool ok = S.SeekThumbnails && path != null && File.Exists(path) &&
                  !string.IsNullOrEmpty(m!.GetString("video-codec")) && m.GetFlag("current-tracks/video/image") != true &&
                  // 杜比视界 P5 的画面要靠 RPU 重塑才有正确颜色，软件截图不做这一步，缩略图会偏绿 / 偏紫
                  _dvProfile != "5";
        _bar.ThumbnailsEnabled = ok;
        _thumbs.SetFile(ok ? Path.GetFullPath(path!) : null);
    }

    private void OnVideoReconfig()
    {
        UpdateFormatLabel();
        MaybeAutoHdr();
    }

    private void OnEndFile(MpvEndReason reason, int error)
    {
        if (reason != MpvEndReason.Error) return;
        if (_reloading) { _reloading = false; _mpv?.SetProperty("start", "none"); }
        _mpv?.ShowText("播放失败：" + Mpv.ErrorString(error), 4000);
    }

    /// <summary>
    /// 重新载入当前文件并回到原来的位置。解码线程数、首选音轨 / 字幕语言、外挂字幕自动加载
    /// 这些设置只在打开文件时读取，重新载入后立即生效。
    /// </summary>
    private void ReloadCurrent()
    {
        var m = _mpv;
        if (m == null || _idle) return;
        double pos = m.GetDouble("time-pos") ?? 0;
        _reloading = true;
        m.SetProperty("start", pos.ToString("0.###", Inv));
        m.Command("playlist-play-index", "current");
        m.ShowText("重新载入…");
    }

    /// <summary>
    /// 单击画面：等双击时间过去、确认不是双击后再暂停 / 播放，免得双击全屏时画面先停一下。
    /// 窗口没激活时的那一下单击只用来激活窗口，不暂停。
    /// </summary>
    private void OnVideoClick()
    {
        if (_idle) return;
        var now = DateTime.UtcNow;
        if ((now - _lastDblClick).TotalMilliseconds < DoubleClickMs + 100) return; // 双击的第二下
        if ((now - _activatedAt).TotalMilliseconds < 300) return;
        if (_clickTimer == null)
        {
            _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DoubleClickMs) };
            _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); DoAction("pause"); };
        }
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    /// <summary>
    /// mpv 在右键按下时就发来 menu，这时鼠标还被 mpv 的窗口占着（按住期间鼠标归它）：
    /// 立刻弹菜单的话，松开右键时菜单会失去鼠标捕获、马上关闭。所以等右键松开再弹。
    /// </summary>
    private void ShowContextMenuAfterRightButtonUp()
    {
        var start = DateTime.UtcNow;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        t.Tick += (_, _) =>
        {
            if (Native.IsRightButtonDown() && (DateTime.UtcNow - start).TotalSeconds < 3) return;
            t.Stop();
            if (_shuttingDown) return;
            Activate();
            ShowContextMenu();
        };
        t.Start();
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
            case "click": OnVideoClick(); break;
            case "dblclick":
                _lastDblClick = DateTime.UtcNow;
                _clickTimer?.Stop();
                ToggleFullscreen();
                break;
            case "reload": ReloadCurrent(); break;
            case "exit-fullscreen": if (_fullscreen) ToggleFullscreen(); break;
            case "menu": ShowContextMenuAfterRightButtonUp(); break;
            case "hdr": SetHdr(!_hdrOn, manual: true); break;
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 播放列表获得焦点时，上下键 / 回车 / Delete 留给列表
        if (PlaylistBox.IsKeyboardFocusWithin)
        {
            switch (key)
            {
                case Key.Up: case Key.Down: case Key.Home: case Key.End: return;
                case Key.Enter: PlaySelected(); e.Handled = true; return;
                case Key.Delete: RemoveSelected(); e.Handled = true; return;
            }
        }

        string? action = (Keyboard.Modifiers, key) switch
        {
            (ModifierKeys.None, Key.Space) => "pause",
            (ModifierKeys.None, Key.Left) => "seek-back",
            (ModifierKeys.None, Key.Right) => "seek-fwd",
            (ModifierKeys.Shift, Key.Left) => "seek-back-big",
            (ModifierKeys.Shift, Key.Right) => "seek-fwd-big",
            (ModifierKeys.None, Key.Up) => "vol-up",
            (ModifierKeys.None, Key.Down) => "vol-down",
            (ModifierKeys.None, Key.M) => "mute",
            (ModifierKeys.None, Key.F) => "fullscreen",
            (ModifierKeys.None, Key.Enter) => "fullscreen",
            (ModifierKeys.None, Key.Escape) => "exit-fullscreen",
            (ModifierKeys.None, Key.H) => "hdr",
            (ModifierKeys.None, Key.Z) => "sub-delay-dec",
            (ModifierKeys.None, Key.X) => "sub-delay-inc",
            (ModifierKeys.None, Key.V) => "sub-toggle",
            (ModifierKeys.None, Key.J) => "sub-cycle",
            (ModifierKeys.Shift, Key.D3) => "audio-cycle",
            (ModifierKeys.None, Key.I) => "info",
            (ModifierKeys.None, Key.P) => "playlist",
            (ModifierKeys.None, Key.PageUp) => "prev",
            (ModifierKeys.None, Key.PageDown) => "next",
            (ModifierKeys.Control, Key.O) => "open",
            (ModifierKeys.None, Key.OemPeriod) => "frame-step",
            (ModifierKeys.None, Key.OemComma) => "frame-back",
            (ModifierKeys.None, Key.F5) => "reload",
            _ => null,
        };
        if (action != null)
        {
            DoAction(action);
            e.Handled = true;
        }
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

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
        e.Handled = true;
    }

    private string[] PickFiles(string filter, string title, bool multi)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = multi, Filter = filter, Title = title };
        return dlg.ShowDialog(this) == true ? dlg.FileNames : Array.Empty<string>();
    }

    private void OpenFiles() => AddFiles(PickFiles(VideoFilter, "打开视频", true));

    private void LoadSubtitle()
    {
        foreach (var f in PickFiles(SubFilter, "加载字幕", true)) _mpv?.Command("sub-add", f, "select");
    }

    private void LoadAudioTrack()
    {
        foreach (var f in PickFiles(AudioFilter, "加载外部音轨", false)) _mpv?.Command("audio-add", f, "select");
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
                list.Add(new PlaylistEntry(i, $"{i + 1}. {name}", path, cur));
            }
        }
        _playlist = list;
        int sel = PlaylistBox.SelectedIndex;
        PlaylistBox.ItemsSource = list;
        if (sel >= 0 && sel < list.Count) PlaylistBox.SelectedIndex = sel;
        _bar.BtnPrev.IsEnabled = _bar.BtnNext.IsEnabled = list.Count > 1;
    }

    private void PlaySelected()
    {
        if (PlaylistBox.SelectedItem is PlaylistEntry e)
            _mpv?.Command("playlist-play-index", e.Index.ToString(Inv));
    }

    private void RemoveSelected()
    {
        if (PlaylistBox.SelectedItem is PlaylistEntry e)
            _mpv?.Command("playlist-remove", e.Index.ToString(Inv));
    }

    private void RevealSelected()
    {
        if (PlaylistBox.SelectedItem is PlaylistEntry e && File.Exists(e.Path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{e.Path}\"");
    }

    private void TogglePlaylist()
    {
        if (_fullscreen) return;
        bool show = PlaylistPanel.Visibility != Visibility.Visible;
        PlaylistPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        S.PlaylistVisible = show;
    }

    // =====================================================================
    //  右键菜单
    // =====================================================================

    private static MenuItem Item(string header, Action? click, string? gesture = null, bool isChecked = false, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsChecked = isChecked, IsEnabled = enabled };
        if (click != null) mi.Click += (_, _) => click();
        return mi;
    }

    private void ShowContextMenu()
    {
        var m = _mpv;
        var menu = new ContextMenu();
        var items = menu.Items;
        items.Add(Item("视频参数…", ShowInfo, "I"));
        items.Add(new Separator());
        items.Add(Item("打开文件…", OpenFiles, "Ctrl+O"));
        items.Add(Item("加载字幕…", LoadSubtitle, enabled: !_idle));
        items.Add(Item("加载外部音轨…", LoadAudioTrack, enabled: !_idle));
        items.Add(Item("重新载入当前文件", ReloadCurrent, "F5", enabled: !_idle));
        items.Add(new Separator());

        var audioMenu = Item("音轨", null);
        var subMenu = Item("字幕", null);
        subMenu.Items.Add(Item("关闭字幕", () => m?.SetProperty("sid", "no"), isChecked: m?.GetString("sid") is "no" or null));
        if (m?.GetNode("track-list") is List<object?> tracks)
        {
            foreach (var t in tracks.OfType<Dictionary<string, object?>>())
            {
                string type = t.GetValueOrDefault("type") as string ?? "";
                if (type != "audio" && type != "sub") continue;
                long id = t.GetValueOrDefault("id") is long l ? l : 0;
                string prop = type == "audio" ? "aid" : "sid";
                var item = Item(TrackLabel(t), () => m?.SetProperty(prop, id.ToString(Inv)),
                    isChecked: t.GetValueOrDefault("selected") is true);
                (type == "audio" ? audioMenu : subMenu).Items.Add(item);
            }
        }
        audioMenu.IsEnabled = audioMenu.Items.Count > 0;
        items.Add(audioMenu);
        items.Add(subMenu);

        var delayMenu = Item("字幕延迟", null);
        delayMenu.Items.Add(Item($"提前 {S.SubDelayStepMs} ms", () => AdjustSubDelay(-S.SubDelayStepMs), "Z"));
        delayMenu.Items.Add(Item($"延后 {S.SubDelayStepMs} ms", () => AdjustSubDelay(S.SubDelayStepMs), "X"));
        delayMenu.Items.Add(Item("重置", () => { m?.SetProperty("sub-delay", "0"); m?.ShowText("字幕延迟：0 ms"); }));
        items.Add(delayMenu);
        items.Add(new Separator());

        items.Add(Item("HDR 输出", () => DoAction("hdr"), "H", isChecked: _hdrOn));
        var decMenu = Item("解码方式", null);
        foreach (var (val, name) in SettingsWindow.HwdecChoices)
        {
            string v = val, n = name;
            decMenu.Items.Add(Item(n, () =>
            {
                S.Hwdec = v;
                m?.SetProperty("hwdec", v);
                m?.ShowText("解码方式：" + n);
                S.Save();
            }, isChecked: S.Hwdec == v));
        }
        items.Add(decMenu);
        items.Add(new Separator());
        items.Add(Item("播放列表", TogglePlaylist, "P", isChecked: PlaylistPanel.Visibility == Visibility.Visible, enabled: !_fullscreen));
        items.Add(Item("全屏", ToggleFullscreen, "F / 双击", isChecked: _fullscreen));
        items.Add(Item("设置…", OpenSettings));
        items.Add(new Separator());
        items.Add(Item("退出", Close));

        menu.Closed += (_, _) => _menuOpen = false;
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        _menuOpen = true;
        menu.IsOpen = true;
    }

    private static string TrackLabel(Dictionary<string, object?> t) => TrackText(t).Replace("_", "__");

    private static string TrackText(Dictionary<string, object?> t)
    {
        var parts = new List<string>();
        if (t.GetValueOrDefault("id") is long id) parts.Add("#" + id);
        if (t.GetValueOrDefault("lang") is string lang) parts.Add($"[{lang}]");
        if (t.GetValueOrDefault("title") is string title) parts.Add(title);
        if (t.GetValueOrDefault("codec") is string codec) parts.Add(codec);
        if (t.GetValueOrDefault("demux-channel-count") is long ch) parts.Add(ch + "ch");
        if (t.GetValueOrDefault("default") is true) parts.Add("(默认)");
        if (t.GetValueOrDefault("external") is true) parts.Add("(外部)");
        return string.Join(" ", parts); // 用于菜单时由 TrackLabel 转义 “_”（MenuItem 会把它当作访问键）
    }

    // =====================================================================
    //  窗口 / 全屏 / 悬浮控制栏
    // =====================================================================

    /// <summary>窗口化时视频四周留 4px 给缩放边框；最大化 / 全屏时铺满。</summary>
    private void UpdateFrameMargins()
    {
        if (_fullscreen) return;
        bool max = WindowState == WindowState.Maximized;
        VideoFrame.Margin = max ? new Thickness(0) : new Thickness(4, 0, 4, 4);
        PlaylistPanel.Margin = max ? new Thickness(0, 0, 0, 0) : new Thickness(0, 0, 4, 4);
    }

    private void SetResizeBorder(bool on)
    {
        try
        {
            if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { IsFrozen: false } chrome)
                chrome.ResizeBorderThickness = on ? new Thickness(4) : new Thickness(0);
        }
        catch { }
    }

    private Matrix FromDevice() =>
        PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;

    private void ToggleFullscreen()
    {
        _bar.HideNow();
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _prevBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _playlistWasVisible = PlaylistPanel.Visibility == Visibility.Visible;
            _fullscreen = true;

            PlaylistPanel.Visibility = Visibility.Collapsed;
            AppTitleBar.Visibility = Visibility.Collapsed;
            VideoFrame.Margin = new Thickness(0);
            SetResizeBorder(false);
            WindowCornerPreference = WindowCornerPreference.DoNotRound;

            // 普通窗口状态 + 尺寸等于整块屏幕：Windows 会把它当作全屏窗口，任务栏自动让开
            var (_, mon) = Native.MonitorOf(_hwnd);
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            var m = FromDevice();
            var tl = m.Transform(new Point(mon.Left, mon.Top));
            var br = m.Transform(new Point(mon.Right, mon.Bottom));
            (Left, Top, Width, Height) = (tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
        }
        else
        {
            _fullscreen = false;
            AppTitleBar.Visibility = Visibility.Visible;
            PlaylistPanel.Visibility = _playlistWasVisible ? Visibility.Visible : Visibility.Collapsed;
            SetResizeBorder(true);
            WindowCornerPreference = WindowCornerPreference.Round;
            (Left, Top, Width, Height) = (_prevBounds.X, _prevBounds.Y, _prevBounds.Width, _prevBounds.Height);
            WindowState = _prevState;
            UpdateFrameMargins();
        }
        _bar.FullIcon.Symbol = _fullscreen ? SymbolRegular.FullScreenMinimize24 : SymbolRegular.FullScreenMaximize24;
        _lastActivity = DateTime.UtcNow;
        Dispatcher.InvokeAsync(PositionBar, DispatcherPriority.Loaded);
    }

    /// <summary>把悬浮控制栏摆到视频区域底部居中。</summary>
    private void PositionBar()
    {
        if (_hwnd == IntPtr.Zero || WindowState == WindowState.Minimized || !VideoFrame.IsVisible) return;
        var src = PresentationSource.FromVisual(VideoFrame);
        if (src?.CompositionTarget == null) return;
        var tl = src.CompositionTarget.TransformFromDevice.Transform(VideoFrame.PointToScreen(new Point(0, 0)));
        double vw = VideoFrame.ActualWidth, vh = VideoFrame.ActualHeight;
        double w = Math.Min(vw, BarMaxWidth);
        _bar.Width = w;
        _bar.Left = tl.X + (vw - w) / 2;
        _bar.Top = tl.Y + vh - _bar.ActualHeight;
    }

    private void UiTick()
    {
        if (_timeDirty)
        {
            _timeDirty = false;
            _bar.SetPosition(_timePos);
        }
        UpdateBarVisibility();
    }

    /// <summary>
    /// 鼠标在画面或控制栏上移动时显示控制栏，静止 2 秒后淡出。
    /// mpv 的画面是别的线程的原生窗口，收不到 WPF 的鼠标事件，所以用轮询光标位置的方式判断。
    /// </summary>
    private void UpdateBarVisibility()
    {
        if (_shuttingDown || _mpv == null || !IsVisible || WindowState == WindowState.Minimized) return;

        Native.GetCursorPos(out var p);
        bool moved = p.X != _lastCursor.X || p.Y != _lastCursor.Y;
        _lastCursor = p;

        IntPtr root = Native.GetAncestor(Native.WindowFromPoint(p), Native.GA_ROOT);
        bool overBar = _bar.IsRevealed && root == _bar.Handle;
        bool overVideo = root == _hwnd && Native.GetWindowRect(Video.Hwnd, out var vr) && vr.Contains(p);
        if (moved && (overVideo || overBar)) _lastActivity = DateTime.UtcNow;

        bool keep = _idle || overBar || _bar.IsSeekDragging || _menuOpen ||
                    (DateTime.UtcNow - _lastActivity).TotalMilliseconds < BarHideMs;
        if (keep)
        {
            if (!_bar.IsRevealed) PositionBar();
            _bar.Reveal();
        }
        else
        {
            _bar.Conceal();
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
        if (m == null || _idle) { _bar.FormatText.Text = ""; return; }
        var parts = new List<string>();

        string? gamma = m.GetString("video-params/gamma");
        string hdr = gamma switch
        {
            "pq" => _hdr10Plus ? "HDR10+" : "HDR10",
            "hlg" => "HLG",
            null => "",
            _ => "SDR",
        };
        if (_dvProfile != null)
            hdr = DvStripped ? $"DV P{_dvProfile}→{(hdr.Length > 0 ? hdr : "基础层")}" : $"杜比视界 P{_dvProfile}";
        if (hdr.Length > 0) parts.Add(hdr);

        string? hw = m.GetString("hwdec-current");
        if (!string.IsNullOrEmpty(m.GetString("video-codec")))
            parts.Add(string.IsNullOrEmpty(hw) || hw == "no" ? "软解" : "硬解");

        long? inCh = m.GetInt("audio-params/channel-count");
        long? outCh = m.GetInt("audio-out-params/channel-count");
        if (inCh is long a && outCh is long b)
            parts.Add(a == b ? $"{ChName(a)}" : $"{ChName(a)}→{ChName(b)}");

        _bar.FormatText.Text = string.Join(" · ", parts);
    }

    private static string ChName(long ch) => ch switch
    {
        1 => "单声道", 2 => "立体声", 6 => "5.1", 8 => "7.1", _ => ch + "ch",
    };

    // =====================================================================
    //  信息窗口 / 设置窗口
    // =====================================================================

    private void ShowInfo()
    {
        if (_infoWindow is { IsLoaded: true })
        {
            _infoWindow.RefreshText();
            _infoWindow.Activate();
            return;
        }
        _infoWindow = new InfoWindow(BuildInfoText) { Owner = this };
        _infoWindow.Closed += (_, _) => _infoWindow = null;
        _infoWindow.Show();
    }

    private async void OpenSettings()
    {
        var m = _mpv;
        if (m == null) return;
        if (_fullscreen) ToggleFullscreen();
        var copy = S.Clone();
        var dlg = new SettingsWindow(copy, m, CurrentGdiName(), _hdrOn) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        bool gpuApiChanged = copy.GpuApi != S.GpuApi;
        // 这几项只在打开文件时读取，正在播放的文件要重新载入才生效
        bool needsReload = copy.DecoderThreads != S.DecoderThreads || copy.AudioLanguages != S.AudioLanguages ||
                           copy.SubLanguages != S.SubLanguages || copy.SubAuto != S.SubAuto;
        bool dvChanged = copy.DolbyVisionMode != S.DolbyVisionMode;
        if (copy.TargetPeakNits != S.TargetPeakNits) _displayPeak = 0;
        CopySettings(copy, S);
        S.Save();
        ApplyPlaybackOptions(m, initial: false);
        if (dvChanged) m.SetProperty("vf", DolbyVisionFilter());
        UpdateThumbnailSource();
        // 改设置不会去切换 Windows HDR，只重新应用 mpv 的 HDR 参数
        ApplyMpvHdrProps();
        UpdateHdrButton();
        if (needsReload && !_idle)
            m.ShowText("部分设置要重新载入当前文件才生效：点控制栏的重新载入按钮，或按 F5", 5000);
        if (gpuApiChanged)
            await Dialogs.ShowAsync(this, "HdrPlayer", "渲染 API 的修改将在重启播放器后生效。");
    }

    private static void CopySettings(AppSettings from, AppSettings to)
    {
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
            p.SetValue(to, p.GetValue(from));
    }

    /// <summary>右键“视频参数”窗口的内容（当前播放的文件，每秒刷新）。</summary>
    private string BuildInfoText() => BuildInfoText(_mpv, live: true);

    /// <summary>
    /// live = 当前播放的文件：包含播放位置、丢帧、渲染输出、显示器等运行状态。
    /// 否则是播放列表里其它文件的探测结果（由后台的临时 mpv 实例打开，只读文件和视频参数）；这时会在后台线程调用。
    /// </summary>
    private string BuildInfoText(Mpv? m, bool live)
    {
        var sb = new StringBuilder();
        if (m == null) return "";

        string P(string name) => m.GetString(name) is { Length: > 0 } s ? s : "—";
        string O(string name) => m.GetOsdString(name) is { Length: > 0 } s ? s : "—";
        void Line(string k, string v) => sb.Append("  ").Append(k.PadRight(14 - CountWide(k))).Append(v).Append('\n');
        void Head(string h) => sb.Append("\n【").Append(h).Append("】\n");

        if (live && _idle)
        {
            sb.Append("当前没有在播放。\n");
        }
        else
        {
            double duration = live ? _duration : m.GetDouble("duration") ?? 0;
            string? dv = live ? _dvProfile : m.GetString("current-tracks/video/dolby-vision-profile");
            bool hdr10Plus = live ? _hdr10Plus
                : double.TryParse(m.GetString("video-params/scene-max-r"), NumberStyles.Float, Inv, out double smr) && smr > 0;
            bool hasVideo = !string.IsNullOrEmpty(m.GetString("video-codec"));

            Head("文件");
            Line("路径", P("path"));
            Line("容器", P("file-format"));
            Line("大小", O("file-size"));
            Line("时长", FormatTime(duration));
            if (live) Line("位置", $"{FormatTime(_timePos)}");

            if (hasVideo)
            {
                Head("视频");
                Line("编码", $"{P("video-format")}  ({P("video-codec")})");
                Line("分辨率", $"{P("video-params/w")} × {P("video-params/h")}  显示 {P("video-params/dw")} × {P("video-params/dh")}");
                Line("帧率", live ? $"容器 {P("container-fps")}  实际 {O("estimated-vf-fps")}  显示器 {O("display-fps")} Hz" : $"容器 {P("container-fps")}");
                if (live) Line("码率", O("video-bitrate"));
                Line("像素格式", $"{P("video-params/pixelformat")}  硬件格式 {P("video-params/hw-pixelformat")}");
                Line("色彩矩阵", $"{P("video-params/colormatrix")}  范围 {P("video-params/colorlevels")}");
                Line("原色", P("video-params/primaries"));
                Line("传输特性", P("video-params/gamma"));
                string gamma = P("video-params/gamma");
                string fmt = gamma switch { "pq" => hdr10Plus ? "HDR10+ (PQ + ST 2094-40 动态元数据)" : "HDR10 (PQ + 静态元数据)", "hlg" => "HLG", "—" => "—", _ => "SDR" };
                Line("HDR 格式", fmt);
                if (gamma is "pq" or "hlg")
                {
                    Line("MaxCLL/FALL", $"{P("video-params/max-cll")} / {P("video-params/max-fall")} nit");
                    Line("母版亮度", $"{P("video-params/min-luma")} – {P("video-params/max-luma")} nit");
                    if (double.TryParse(m.GetString("video-params/sig-peak"), NumberStyles.Float, Inv, out double sp))
                        Line("信号峰值", $"{sp * 203:0} nit (sig-peak {sp:0.###})");
                }
                if (hdr10Plus)
                {
                    Line("HDR10+ 场景", $"maxRGB R/G/B {P("video-params/scene-max-r")} / {P("video-params/scene-max-g")} / {P("video-params/scene-max-b")} nit");
                    Line("", $"场景平均 {P("video-params/scene-avg")} nit");
                }
                if (dv != null)
                    Line("杜比视界", $"Profile {dv}  Level {P("current-tracks/video/dolby-vision-level")}  → " +
                        (DvStripped ? "剥离 RPU，仅播放基础层" : "应用 RPU 动态元数据（libplacebo 重塑）"));
                if (live)
                {
                    string hw = P("hwdec-current");
                    Line("解码", hw is "—" or "no" ? "软解 (CPU)" : $"硬解 ({hw})");
                    Line("丢帧", $"解码 {P("decoder-frame-drop-count")}  输出 {P("frame-drop-count")}  延迟 {P("vo-delayed-frame-count")}");
                }
            }

            if (live && hasVideo)
            {
                Head("渲染输出");
                Line("VO", $"{P("current-vo")}  API {P("gpu-api")}");
                Line("输出色彩", $"{P("video-target-params/primaries")} / {P("video-target-params/gamma")}  峰值 {P("video-target-params/max-luma")} nit");
                Line("HDR 输出", _hdrOn ? $"开  目标峰值 {Math.Round(DisplayPeak())} nit" : "关（色调映射到 SDR）");
                Line("色调映射", $"{P("tone-mapping")}  峰值检测 {P("hdr-compute-peak")}  色域映射 {P("gamut-mapping-mode")}");
                Line("缩放", $"{P("scale")} / {P("dscale")}  去色带 {P("deband")}");
            }

            if (!string.IsNullOrEmpty(m.GetString("audio-codec")) || !string.IsNullOrEmpty(m.GetString("current-tracks/audio/codec")))
            {
                Head("音频");
                Line("编码", $"{P("audio-codec-name")}  ({P("audio-codec")})");
                Line("声道", $"{P("audio-params/channel-count")} ch  {P("audio-params/hr-channels")}");
                if (live)
                    Line("输出声道", $"{P("audio-out-params/channel-count")} ch  {P("audio-out-params/hr-channels")}" +
                        (S.DownmixToStereo ? "  (下混到立体声)" : ""));
                Line("采样率", $"{P("audio-params/samplerate")} Hz  {P("audio-params/format")}");
                if (live)
                {
                    Line("码率", O("audio-bitrate"));
                    Line("输出设备", $"{P("audio-device")}  ({P("current-ao")})");
                    Line("音频延迟", $"{(m.GetDouble("audio-delay") ?? 0) * 1000:0} ms");
                }
            }

            if (live)
            {
                Head("字幕");
                Line("当前", $"#{P("sid")} {P("current-tracks/sub/lang")} {P("current-tracks/sub/title")} {P("current-tracks/sub/codec")}");
                Line("字幕延迟", $"{(m.GetDouble("sub-delay") ?? 0) * 1000:0} ms");
            }

            if (m.GetNode("track-list") is List<object?> tracks && tracks.Count > 0)
            {
                Head("全部轨道");
                foreach (var t in tracks.OfType<Dictionary<string, object?>>())
                {
                    string type = (t.GetValueOrDefault("type") as string) switch
                    {
                        "video" => t.GetValueOrDefault("image") is true ? "封面" : "视频",
                        "audio" => "音频", "sub" => "字幕", var x => x ?? "?",
                    };
                    string mark = live && t.GetValueOrDefault("selected") is true ? "  ← 当前" : "";
                    Line(type, TrackText(t) + mark);
                }
            }
        }

        if (live)
        {
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
        }
        return sb.ToString();
    }

    /// <summary>播放列表右键“视频参数”：当前文件直接打开实时窗口，其它文件在后台探测后显示。</summary>
    private void ShowEntryInfo(PlaylistEntry e)
    {
        if (e.Current && !_idle) { ShowInfo(); return; }
        string text = $"正在读取 {Path.GetFileName(e.Path)} …";
        var win = new InfoWindow(() => text, "视频参数 - " + Path.GetFileName(e.Path), live: false) { Owner = this };
        win.Show();
        string path = e.Path;
        Task.Run(() => MediaProbe.Run(path, pm => BuildInfoText(pm, live: false))).ContinueWith(t =>
        {
            string result = t.IsFaulted ? "读取失败：" + t.Exception?.GetBaseException().Message : t.Result;
            Dispatcher.InvokeAsync(() =>
            {
                text = result;
                if (win.IsLoaded) win.RefreshText();
            });
        });
    }

    private static int CountWide(string s) => s.Count(c => c > 0x2E80);

    // =====================================================================
    //  关闭
    // =====================================================================

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeReady && _mpv != null)
        {
            e.Cancel = true;
            if (_shuttingDown) return;

            if (_fullscreen) ToggleFullscreen();
            S.WinMaximized = WindowState == WindowState.Maximized;
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            (S.WinX, S.WinY, S.WinW, S.WinH) = ((int)Math.Round(b.X), (int)Math.Round(b.Y), (int)Math.Round(b.Width), (int)Math.Round(b.Height));
            S.Save();

            _uiTimer.Stop();
            _infoWindow?.Close();
            _bar.HideNow();
            Hide();
            _shuttingDown = true;
            var m = _mpv;
            _mpv = null;
            // 在后台线程销毁 mpv，UI 线程继续处理消息，避免与 mpv 窗口线程互相等待
            Task.Run(() =>
            {
                try { _thumbs.Dispose(); } catch { }
                try { m.Dispose(); } catch { }
            }).ContinueWith(_ =>
            {
                try
                {
                    Dispatcher.InvokeAsync(() =>
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
        base.OnClosing(e);
    }
}
