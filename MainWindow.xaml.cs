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

/// <summary>
/// 播放列表里的一项（XAML 模板里绑定）。列表内容没变时（只是换了正在播放的项、缩略图生成好了）
/// 原地改属性，不重建列表，滚动位置和选中项都不动。
/// </summary>
public sealed class PlaylistEntry : INotifyPropertyChanged
{
    public PlaylistEntry(int index, string display, string path)
    {
        Index = index;
        Display = display;
        Path = path;
    }

    public int Index { get; }
    public string Display { get; }
    public string Path { get; }

    private bool _current, _failed, _isAudio;
    private ImageSource? _thumbnail;

    /// <summary>正在播放（mpv 播放列表的 current 项，同一时间只有一项）。</summary>
    public bool Current { get => _current; set => Set(ref _current, value, nameof(Current)); }
    /// <summary>打不开（格式无法识别等），提示可以修复。</summary>
    public bool Failed { get => _failed; set => Set(ref _failed, value, nameof(Failed)); }
    /// <summary>没有画面（纯音频），占位图换成音符。</summary>
    public bool IsAudio { get => _isAudio; set => Set(ref _isAudio, value, nameof(IsAudio)); }
    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value, nameof(Thumbnail)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public partial class MainWindow : FluentWindow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly HashSet<string> SubExts = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".vtt", ".smi", ".lrc", ".pgs" };

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mka", ".ac3", ".eac3", ".ec3", ".dts", ".dtshd", ".thd", ".truehd", ".aac", ".m4a",
          ".flac", ".mp3", ".opus", ".ogg", ".wav", ".w64" };

    private const string VideoFilter =
        "视频文件|*.mkv;*.mp4;*.m4v;*.mov;*.ts;*.m2ts;*.mts;*.m2t;*.avi;*.wmv;*.asf;*.webm;*.flv;*.f4v;*.mpg;*.mpeg;*.vob;" +
        "*.hevc;*.h265;*.264;*.265;*.3gp;*.3g2;*.ogv;*.rm;*.rmvb;*.divx;*.mxf;*.y4m|所有文件|*.*";
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
        ("Ctrl+o", "open"), (".", "frame-step"), (",", "frame-back"), ("F5", "reload"), ("HOME", "restart"),
    };

    /// <summary>双击判定时间：跟随系统设置（画面的双击由 Windows 判定），mpv 的 input-doubleclick-time 也设成一样。
    /// 单击画面要等这么久确认不是双击才暂停。</summary>
    private static readonly int DoubleClickMs = (int)Math.Clamp(Native.GetDoubleClickTime(), 200u, 900u);

    /// <summary>鼠标静止多久后隐藏悬浮控制栏（mpv 的鼠标指针也同时隐藏）。</summary>
    private const int BarHideMs = 2000;
    /// <summary>悬浮控制栏的最大宽度（DIP），超宽屏 / 全屏时居中显示。</summary>
    private const double BarMaxWidth = 1280;

    private readonly AppSettings S;
    private Mpv? _mpv;
    private readonly string[] _startupFiles;
    /// <summary>mpv 初始化完成；之前从其它实例转来的文件先排队。</summary>
    private bool _ready;
    private readonly List<string[]> _pendingOpen = new();
    private readonly ControlBar _bar;
    private readonly Thumbnailer _thumbs = new();
    private readonly SubSync _subSync = new();
    private DispatcherTimer? _subSyncOsd;
    private readonly VideoRepair _repair = new();
    private DispatcherTimer? _repairOsd;
    /// <summary>解除静音时恢复到的音量（静音 = 音量 0，不单独用 mpv 的 mute）。</summary>
    private const double UnmuteVolume = 5;

    // 播放列表缩略图：按完整路径缓存（Pos = 截图位置，0 = 没有记住的位置）
    private readonly PlaylistThumbs _plThumbs = new();
    private sealed class ThumbInfo
    {
        public double Pos;
        public PlaylistThumbs.Kind Kind = PlaylistThumbs.Kind.None;
        public ImageSource? Image;
    }
    private readonly Dictionary<string, ThumbInfo> _thumbInfo = new(StringComparer.OrdinalIgnoreCase);
    // 打不开的文件（本次运行里 mpv 报错的）；_startingPath 是正在打开的文件，打开失败时没有 file-loaded，只能在 start-file 时记下
    private readonly HashSet<string> _failedPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _startingPath, _lastFailedPath;
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
    /// <summary>最近一次键盘跳转：只让控制栏出现，不显示鼠标指针。</summary>
    private DateTime _lastKeyActivity = DateTime.MinValue;
    private bool _menuOpen;

    // 全屏
    private bool _fullscreen;
    private WindowState _prevState;
    private Rect _prevBounds;
    private bool _playlistWasVisible;
    /// <summary>最小化之前的窗口状态：被另一个实例唤起时还原成这个。</summary>
    private WindowState _prevStateBeforeMinimize = WindowState.Normal;

    // HDR：_hdrOn 是本次运行的输出状态。启动时跟随 Windows 当前的 HDR 开关（空着启动不改系统设置），
    // 打开 HDR 片源时自动开启；_originalWindowsHdr 记录第一次切换前的系统状态，退出时还原。
    private bool _hdrOn;
    private bool _autoHdrChecked;
    private bool? _originalWindowsHdr;
    private float _displayPeak;

    // 单击画面暂停
    private DispatcherTimer? _clickTimer;
    private DateTime _activatedAt = DateTime.MinValue, _deactivatedAt = DateTime.MinValue, _lastDblClick = DateTime.MinValue;

    // 重新载入当前文件：期间临时设置了 mpv 的 start 选项，载入后要清掉
    private bool _reloading;

    // 每个文件记住的进度 / 音频延迟 / 字幕延迟
    private readonly FileStateStore _fileStates = FileStateStore.Load();
    private string? _currentPath;      // 当前已载入的文件（file-loaded 时设置，记录状态后清空）
    private double _currentDuration;
    private double _lastPos;
    // 当前文件选中的音轨 / 字幕（文件卸载时轨道列表已经清空，所以在播放中随时记下来，结束时写进 file-states.json）
    private TrackRef? _audioSel, _subSel;
    // 这个文件打开后用户亲手换过音轨 / 字幕：只有换过的才记，没动过的继续按首选语言规则（或之前记住的）来
    private bool _audioTouched, _subTouched;           // 只在 mpv 事件线程上读写：最后一个有效的 time-pos（文件卸载时 time-pos 会变成空）

    // 关闭流程
    private bool _shuttingDown, _closeReady;

    public MainWindow(string[] args)
    {
        S = AppSettings.Load();
        _startupFiles = ExistingPaths(args);

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
        _bar.SetMuted(S.Volume <= 0);
        _bar.BtnPrev.IsEnabled = _bar.BtnNext.IsEnabled = false;
        _bar.DragOver += OnFileDragOver;
        _bar.Drop += OnFileDrop;
        _bar.SizeChanged += (_, _) => PositionBar();
        _bar.MouseRightButtonUp += (_, e) => { ShowContextMenu(); e.Handled = true; };

        // ---- 画面上的鼠标 ----（mpv 嵌入时自己的窗口被禁用，收不到鼠标，见 VideoHost 的说明）
        Video.LeftClick += OnVideoClick;
        Video.LeftDoubleClick += () => DoAction("dblclick");
        Video.RightClick += ShowContextMenu;
        Video.MiddleClick += () => { if (!_idle) DoAction("pause"); };
        Video.Wheel += delta => DoAction(delta > 0 ? "vol-up" : "vol-down");

        // ---- 播放列表 ----
        BtnPlAdd.Click += (_, _) => OpenFiles();
        BtnPlRemove.Click += (_, _) => RemoveSelected();
        BtnPlClear.Click += (_, _) => _mpv?.Command("playlist-clear");
        BtnPlClose.Click += (_, _) => TogglePlaylist();
        PlMenuPlay.Click += (_, _) => PlaySelected();
        PlMenuRemove.Click += (_, _) => RemoveSelected();
        PlMenuReveal.Click += (_, _) => RevealSelected();
        PlMenuInfo.Click += (_, _) => { if (PlaylistBox.SelectedItem is PlaylistEntry e) ShowEntryInfo(e); };
        PlMenuRepair.Click += (_, _) =>
        {
            if (_repair.Running) _repair.Cancel();
            else if (PlaylistBox.SelectedItem is PlaylistEntry e) StartRepair(e.Path);
        };
        PlaylistBox.ContextMenuOpening += OnPlaylistMenuOpening;
        _plThumbs.Ready += (path, pos, kind, bmp) =>
        {
            if (!_shuttingDown) Dispatcher.InvokeAsync(() => OnPlaylistThumb(path, pos, kind, bmp));
        };
        PlaylistBox.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(PlaylistBox, e.OriginalSource as DependencyObject) is ListBoxItem)
                PlaySelected();
        };

        // 拖放都由这里处理（画面上也是：mpv 嵌入时窗口被禁用，系统把拖放交给上层的 WPF 窗口）
        DragOver += OnFileDragOver;
        Drop += OnFileDrop;
        PreviewKeyDown += OnPreviewKeyDown;

        SourceInitialized += (_, _) => _hwnd = new WindowInteropHelper(this).Handle;
        Activated += (_, _) => _activatedAt = DateTime.UtcNow;
        Deactivated += (_, _) => _deactivatedAt = DateTime.UtcNow;
        ContentRendered += OnContentRendered;
        LocationChanged += (_, _) => PositionBar();
        SizeChanged += (_, _) => PositionBar();
        VideoFrame.SizeChanged += (_, _) => PositionBar();
        DpiChanged += (_, _) => { PositionBar(); if (_fullscreen) Dispatcher.InvokeAsync(ApplyFullscreenBounds, DispatcherPriority.Loaded); };
        StateChanged += (_, _) =>
        {
            UpdateFrameMargins();
            if (WindowState == WindowState.Minimized) _bar.HideNow();
            else { _prevStateBeforeMinimize = WindowState; PositionBar(); }
        };
        UpdateFrameMargins();

        _uiTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UiTick();

        UpdateHdrButton();
    }

    /// <summary>第一次启动时窗口占主屏工作区的比例（宽和高都不超过）。</summary>
    private const double FirstRunScreenFraction = 0.75;
    /// <summary>窗口里视频区域以外的高度（标题栏 + 下边框），按它算出 16:9 的视频区域。</summary>
    private const double ChromeHeight = 36;

    private void RestorePlacement()
    {
        var work = SystemParameters.WorkArea;
        bool unset = S.WinX == -1 && S.WinY == -1;
        double w, h;
        if (unset)
        {
            // 第一次启动：按当前屏幕的大小（DIP，已含缩放比例）取 3/4，视频区域保持 16:9；以后用户改的大小会被记住
            w = work.Width * FirstRunScreenFraction;
            h = (w - 8) * 9 / 16 + ChromeHeight;
            if (h > work.Height * FirstRunScreenFraction)
            {
                h = work.Height * FirstRunScreenFraction;
                w = (h - ChromeHeight) * 16 / 9 + 8;
            }
            w = Math.Round(Math.Max(w, MinWidth));
            h = Math.Round(Math.Max(h, MinHeight));
        }
        else
        {
            w = Math.Clamp(S.WinW, MinWidth, Math.Max(MinWidth, work.Width));
            h = Math.Clamp(S.WinH, MinHeight, Math.Max(MinHeight, work.Height));
        }
        var saved = new Rect(S.WinX, S.WinY, w, h);
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
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
        // 先恢复上次的播放列表，再把这次要打开的文件加到后面并播放（而不是用这个文件替换整个列表）
        if (S.RestorePlaylist) RestorePlaylist(announce: _startupFiles.Length == 0);
        if (_startupFiles.Length > 0) OpenFromShell(_startupFiles);
        _ready = true;
        foreach (var pending in _pendingOpen) OpenFromShell(pending);
        _pendingOpen.Clear();
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
        // mpv 默认会把“按住左键并移动超过 3 像素”当作拖动窗口，并吞掉这次单击（MBTN_LEFT 不触发）。
        // 画面嵌在本程序里，拖动没有意义，关掉，单击暂停才可靠
        m.SetOption("window-dragging", "no");
        m.SetOption("input-builtin-dragging", "no");
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
        m.SetOption("mute", "no");
        m.SetOption("screenshot-directory", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        if (S.DebugLog) m.SetOption("log-file", Path.Combine(AppSettings.Dir, "mpv.log"));
        ApplyPlaybackOptions(m, initial: true);

        m.PropertyChanged += OnMpvProperty;
        m.StartFile += () =>
        {
            _lastPos = 0;
            string? starting = m.GetString("path");
            Ui(() => { _startingPath = starting; OnStartFile(); });
        };
        m.FileLoaded += () => Ui(OnFileLoaded);
        // 在事件线程上先取下结束时的位置，再交给 UI 线程（那时新文件的 time-pos 可能已经开始刷新了）
        m.EndFile += (r, err) => { double pos = _lastPos; Ui(() => OnEndFile(r, err, pos)); };
        m.VideoReconfig += () => Ui(OnVideoReconfig);
        m.AudioReconfig += () => Ui(UpdateFormatLabel);
        m.ClientMessage += a => Ui(() => OnClientMessage(a));
        m.Hook += name => { if (name == "on_load") OnLoadHook(m); };

        m.Initialize();
        m.AddHook("on_load");

        m.Observe("pause", MpvFormat.Flag);
        m.Observe("idle-active", MpvFormat.Flag);
        m.Observe("time-pos", MpvFormat.Double);
        m.Observe("duration", MpvFormat.Double);
        m.Observe("volume", MpvFormat.Double);
        m.Observe("aid", MpvFormat.String);
        m.Observe("sid", MpvFormat.String);
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
        // 解码方式：每个文件打开时由 on_load 钩子按“本视频的选择 ?? 全局设置”设置；
        // 播放中改了全局设置，当前文件没有单独选择时立即生效
        if (initial || _idle) set("hwdec", S.Hwdec);
        else m.SetProperty("file-local-options/hwdec", EffectiveHwdec());
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
        set("alang", AppSettings.NormalizeLanguageList(S.AudioLanguages));
        set("slang", AppSettings.NormalizeLanguageList(S.SubLanguages));
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
        // 用 HdrActive 而不是“高级颜色已启用”：24H2 起 SDR 显示器开着自动颜色管理（WCG）时高级颜色也是开的
        return info is { HdrActive: true } || info is { HasDxgi: true, DxgiIsHdr: true };
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

        // 开 HDR 前先看能不能开：显示器不支持 / 系统策略（例如笔记本用电池）不允许时，不去切 Windows 设置，
        // 也不输出 PQ，保持 SDR 输出，HDR 片源由 mpv 色调映射到 SDR。手动按 H 也一样，否则 PQ 信号会被当成 SDR 显示，发灰。
        // 读不到显示器信息（ColorInfoKnown = false）时不下结论，按原来的流程走。
        if (on && info is { ColorInfoKnown: true } && !(info.HasDxgi && info.DxgiIsHdr))
        {
            string? why = !info.HdrSupported ? "显示器不支持HDR，将映射到SDR输出"
                        : info.LimitedByPolicy ? "系统策略禁止启用HDR，将映射到SDR输出"
                        : null;
            if (why != null)
            {
                m.ShowText(why, 3000);
                return;
            }
        }

        if (info is { HdrSupported: true } && info.HdrEnabled != on)
        {
            bool before = info.HdrEnabled;
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
        // 自动开启时，如果系统 HDR 既没开、也没能切换（例如读不到显示器信息），就什么都不做
        if (!manual && on && !switched && !WindowsHdrIsOn()) return;

        _hdrOn = on;
        ApplyMpvHdrProps();
        UpdateHdrButton();
        m.ShowText(!manual ? (on ? "HDR 片源，已开启 HDR 输出" : "SDR 片源，已关闭 HDR 输出")
                   : on ? "HDR 输出：开" : "HDR 输出：关（HDR 片源色调映射到 SDR）");

        if (switched)
        {
            // 显示器切换模式（黑屏一下）需要一点时间：自动切换时先暂停，免得错过开头；
            // 稍后重新读取 EDID 峰值、重新应用 HDR 参数，再继续播放。
            // 有的显示器切换得慢，1.5 秒后还没进入 HDR 模式（读到的峰值不可信）：之后再读两次
            bool resume = !manual && !_idle && m.GetFlag("pause") == false;
            if (resume) m.SetProperty("pause", true);
            _displayPeak = 0;
            int tries = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (_, _) =>
            {
                tries++;
                ApplyMpvHdrProps();
                UpdateFormatLabel();
                if (tries == 1 && resume) _mpv?.SetProperty("pause", false);
                if (!_hdrOn || _displayPeak > 0 || S.TargetPeakNits > 0 || tries >= 3) t.Stop();
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
        if (!S.AutoManageHdr) return; // 不自动管理：只在手动按 H / 点 HDR 按钮时切换
        // 音乐文件的封面图也算“视频轨”，不能因为它把 HDR 关掉
        if (m.GetFlag("current-tracks/video/image") == true) return;
        bool hdrSource = gamma is "pq" or "hlg" || _dvProfile != null;
        if (hdrSource != _hdrOn) SetHdr(hdrSource, manual: false);
    }

    /// <summary>读不到可信峰值时用的值（HDR10 片源最常见的母版峰值）。只在 Windows 还没进入 HDR 模式时会用到。</summary>
    private const float FallbackPeakNits = 1000f;
    /// <summary>低于这个值肯定不是真实的 HDR 峰值（读取失败或 EDID 没写）。</summary>
    private const float MinPlausiblePeakNits = 100f;

    /// <summary>
    /// 色调映射的目标峰值（mpv target-peak）：高于它的高光会被压缩进显示器能显示的范围，而不是让显示器自己裁切。
    /// Windows 处于 HDR 模式时，DXGI 报的峰值就是显示器 EDID 里的值（跑过“Windows HDR 校准”的话是校准后的值），
    /// 直接用，哪怕只有 300–400 nit 的屏也照实用。Windows 还没切到 HDR 时，部分驱动报的是 SDR 亮度，
    /// 这时的值不缓存，先用兜底值，切换完成后再读。
    /// </summary>
    private float DisplayPeak() => DisplayPeakWithSource().Nits;

    private (float Nits, string Source) DisplayPeakWithSource()
    {
        if (S.TargetPeakNits > 0) return (S.TargetPeakNits, "手动设置");
        if (_displayPeak > 0) return (_displayPeak, "显示器上报");
        var info = DisplayHelper.Query(CurrentGdiName());
        // 以 DXGI 自己报的色彩空间为准：系统的 HDR 标志可能先变，同一份 DXGI 数据里的亮度却还是切换前的
        if (info is { HasDxgi: true, DxgiIsHdr: true } && info.MaxLuminance >= MinPlausiblePeakNits)
        {
            _displayPeak = info.MaxLuminance;
            return (_displayPeak, "显示器上报");
        }
        return (FallbackPeakNits, "读不到，暂用默认值");
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
        if (_originalWindowsHdr is not bool orig) return;
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
            if (value is double p && p > 0) _lastPos = p;
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
            case "aid":
            case "sid":
                SnapshotTracks();
                break;
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
                _timeDirty = true;
                break;
            case "volume":
                // 静音就是音量 0：拖到 0 / 按静音都显示静音图标，往右拖离开 0 就解除
                if (value is double v) { _bar.SetVolume(v); _bar.SetMuted(v <= 0); S.Volume = v; }
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
        _audioSel = _subSel = null;
        _audioTouched = _subTouched = false;
        _hdr10Plus = false;
        _dvProfile = null;
        _autoHdrChecked = false;
        ApplyToneMapping();
    }

    private async void OnFileLoaded()
    {
        var m = _mpv;
        if (m == null) return;
        bool reloaded = _reloading;
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
                "当前设置为“剥离 RPU”，无法播放。请在 全局设置 → HDR → 杜比视界 中改为“应用 RPU”。");
            return;
        }
        _currentPath = m.GetString("path");
        _currentDuration = m.GetDouble("duration") ?? 0;
        _lastFailedPath = null;
        if (_currentPath != null && _failedPaths.Remove(FullPath(_currentPath))) UpdatePlaylistFlags();
        ApplyFileState(m, resumePosition: !reloaded);
        UpdateFormatLabel();
        UpdateThumbnailSource();
        MaybeAutoHdr();
    }

    /// <summary>
    /// 打开文件时套用这个文件记住的状态：音频 / 字幕延迟（没记录过的文件归零，延迟是按文件的），
    /// 以及上次的播放进度。重新载入（F5）时位置已经由 start 选项定好，不再跳。
    /// </summary>
    private void ApplyFileState(Mpv m, bool resumePosition)
    {
        if (_currentPath == null) return;
        var st = _fileStates.Get(_currentPath);
        var notes = new List<string>();
        if (S.RememberDelays)
        {
            double ad = st?.AudioDelay ?? 0, sd = st?.SubDelay ?? 0;
            m.SetProperty("audio-delay", ad);
            m.SetProperty("sub-delay", sd);
            if (ad != 0) notes.Add($"音频延迟 {ad * 1000:+0;-0} ms");
            if (sd != 0) notes.Add($"字幕延迟 {sd * 1000:+0;-0} ms");
        }
        double pos = st?.Position ?? 0;
        if (S.RememberPosition && resumePosition && pos > 0 && (_currentDuration <= 0 || pos < _currentDuration - 10))
        {
            m.Command("seek", pos.ToString("0.###", Inv), "absolute+exact");
            notes.Insert(0, $"从上次的位置 {FormatTime(pos)} 继续（Home 键从头开始）");
        }
        if (S.RememberTracks) RestoreTracks(m, st);
        SnapshotTracks();
        if (notes.Count > 0) m.ShowText(string.Join("\n", notes), 3000);
    }

    // ---- 每个文件记住的音轨 / 字幕 ----

    private static string TrackStr(Dictionary<string, object?> t, string k) => t.GetValueOrDefault(k) as string ?? "";
    private static bool TrackFlag(Dictionary<string, object?> t, string k) => t.GetValueOrDefault(k) is true;
    private static long TrackId(Dictionary<string, object?> t) => t.GetValueOrDefault("id") is long l ? l : 0;
    private static long? TrackFfIndex(Dictionary<string, object?> t) => t.GetValueOrDefault("ff-index") is long l ? l : null;

    private List<Dictionary<string, object?>> Tracks() =>
        (_mpv?.GetNode("track-list") as List<object?>)?.OfType<Dictionary<string, object?>>().ToList() ?? new();

    /// <summary>
    /// 打开文件时选回记住的音轨 / 字幕。记住的轨找不到（外挂文件被删、文件被替换）时什么都不做，
    /// 保留 mpv 按首选语言规则选好的。外挂文件还在、只是这次没自动加载时，重新加载它。
    /// </summary>
    private void RestoreTracks(Mpv m, FileState? st)
    {
        if (st == null || (st.Audio == null && st.Sub == null)) return;
        var tracks = Tracks();
        if (tracks.Count == 0) return;
        RestoreTrack(m, tracks, "audio", "aid", "audio-add", st.Audio);
        RestoreTrack(m, tracks, "sub", "sid", "sub-add", st.Sub);
    }

    private static void RestoreTrack(Mpv m, List<Dictionary<string, object?>> tracks, string type, string prop, string addCmd, TrackRef? r)
    {
        if (r == null) return;
        if (r.Off) { m.SetProperty(prop, "no"); return; }
        var same = tracks.Where(t => TrackStr(t, "type") == type).ToList();
        Dictionary<string, object?>? hit;
        if (r.External != null)
        {
            hit = same.FirstOrDefault(t => TrackFlag(t, "external") && SamePath(TrackStr(t, "external-filename"), r.External));
            if (hit == null)
            {
                if (File.Exists(r.External)) m.Command(addCmd, r.External, "select");
                return;
            }
        }
        else
        {
            // 同一个流序号，并且编码、语言都没变（文件被替换成别的版本时不乱选）；
            // 退一步按编码 + 语言 + 标题找（例如重新封装后流的顺序变了）
            hit = same.FirstOrDefault(t => !TrackFlag(t, "external") && TrackFfIndex(t) == r.FfIndex &&
                                           TrackStr(t, "codec") == (r.Codec ?? "") && TrackStr(t, "lang") == (r.Lang ?? ""))
               ?? same.FirstOrDefault(t => !TrackFlag(t, "external") && TrackStr(t, "codec") == (r.Codec ?? "") &&
                                           TrackStr(t, "lang") == (r.Lang ?? "") && TrackStr(t, "title") == (r.Title ?? ""));
        }
        if (hit != null) m.SetProperty(prop, TrackId(hit).ToString(Inv));
    }

    /// <summary>记下当前选中的音轨 / 字幕（aid / sid 变化时调用）。文件卸载中轨道列表为空，这时不更新。</summary>
    private void SnapshotTracks()
    {
        if (_mpv == null || _currentPath == null || _idle) return;
        var tracks = Tracks();
        if (tracks.Count == 0) return;
        TrackRef? Snap(string type)
        {
            var t = tracks.FirstOrDefault(o => TrackStr(o, "type") == type && TrackFlag(o, "selected"));
            if (t == null) return null;
            if (TrackFlag(t, "external"))
                return new TrackRef { External = TrackStr(t, "external-filename") is { Length: > 0 } f ? FullPath(f) : null };
            return new TrackRef
            {
                FfIndex = TrackFfIndex(t),
                Codec = NullIfEmpty(TrackStr(t, "codec")),
                Lang = NullIfEmpty(TrackStr(t, "lang")),
                Title = NullIfEmpty(TrackStr(t, "title")),
            };
        }
        _audioSel = Snap("audio") ?? _audioSel;
        // 有字幕轨但一条都没选 = 字幕关掉了
        _subSel = Snap("sub") ?? (tracks.Any(t => TrackStr(t, "type") == "sub") ? new TrackRef { Off = true } : _subSel);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    /// <summary>文件结束 / 切换 / 退出时，记下这个文件的进度和延迟。</summary>
    private void CommitFileState(double position, bool finished)
    {
        var m = _mpv;
        string? path = _currentPath;
        _currentPath = null;
        if (m == null || path == null) return;
        // 看完了（或只剩最后 10 秒）、刚开头几秒：下次从头开始
        if (finished || position < 5 || (_currentDuration > 0 && position > _currentDuration - 10)) position = 0;
        var old = _fileStates.Get(path);
        var st = new FileState
        {
            Position = S.RememberPosition ? position : 0,
            AudioDelay = S.RememberDelays ? m.GetDouble("audio-delay") ?? 0 : old?.AudioDelay ?? 0,
            SubDelay = S.RememberDelays ? m.GetDouble("sub-delay") ?? 0 : old?.SubDelay ?? 0,
            Hwdec = old?.Hwdec,
            Audio = S.RememberTracks && _audioTouched && _audioSel != null ? _audioSel : old?.Audio,
            Sub = S.RememberTracks && _subTouched && _subSel != null ? _subSel : old?.Sub,
            LastPlayed = DateTime.UtcNow,
        };
        _fileStates.Set(path, st);
        // 播放列表的缩略图换成这次停下的位置（刚切走的文件优先生成）
        RefreshEntryThumb(path, urgent: true);
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

    private void OnEndFile(MpvEndReason reason, int error, double position)
    {
        // 放完（EOF）就下次从头开始；切换文件 / 停止 / 重新载入则记下当时的位置
        CommitFileState(position, finished: reason == MpvEndReason.Eof);
        if (reason != MpvEndReason.Error) return;
        if (_reloading) { _reloading = false; _mpv?.SetProperty("start", "none"); }
        // 打开失败时没有 file-loaded：用 start-file 时记下的路径，播放列表里标出来，右键菜单里可以修复它
        string? failed = _startingPath;
        bool local = failed != null && File.Exists(failed);
        if (local)
        {
            _lastFailedPath = FullPath(failed!);
            _failedPaths.Add(_lastFailedPath);
            UpdatePlaylistFlags();
        }
        _mpv?.ShowText("播放失败：" + Mpv.ErrorString(error) +
            (local ? "\n可以右键 → 修复视频，试试重新封装" : ""), 5000);
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
    /// 恢复上次的播放列表：只加入列表、不开始播放（免得一启动就切 HDR、出声音）。
    /// 双击视频文件启动时也恢复，那个文件随后加到列表里播放；announce 为 false 时不显示提示。
    /// </summary>
    private void RestorePlaylist(bool announce)
    {
        var m = _mpv;
        var saved = SavedPlaylist.Load();
        if (m == null || saved == null || saved.Files.Count == 0) return;
        int restored = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in saved.Files)
        {
            if (!File.Exists(f) || !seen.Add(FullPath(f))) continue; // 文件已删除 / 移动 / 所在的盘没接上；重复的只留一个
            m.Command("loadfile", f, "append"); // append：空闲时只加入列表，不会开始播放
            restored++;
        }
        if (restored > 0 && announce)
            m.ShowText($"已恢复上次的播放列表（{restored} 个文件），在播放列表（P）里双击要看的文件", 4000);
    }

    /// <summary>命令行参数里存在的文件 / 文件夹，转成完整路径（转给另一个实例时工作目录不同）。</summary>
    public static string[] ExistingPaths(IEnumerable<string> args) =>
        args.Where(a => File.Exists(a) || Directory.Exists(a)).Select(FullPath).ToArray();

    private static string FullPath(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }

    private static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(FullPath(a), FullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>播放列表里这个文件的位置，没有返回 -1。</summary>
    private int PlaylistIndexOf(string path)
    {
        if (_mpv?.GetNode("playlist") is not List<object?> list) return -1;
        for (int i = 0; i < list.Count; i++)
            if (list[i] is Dictionary<string, object?> e && SamePath(e.GetValueOrDefault("filename") as string, path))
                return i;
        return -1;
    }

    /// <summary>
    /// 从资源管理器打开（启动参数，或另一个实例转来的）：视频加到播放列表末尾（已经在列表里的不重复加），
    /// 然后播放第一个；它正是当前在播放的文件时，重新载入一次（回到原位置）。字幕 / 外挂音轨照常加载到当前视频。
    /// </summary>
    private void OpenFromShell(string[] paths)
    {
        var m = _mpv;
        if (m == null || paths.Length == 0) return;
        var media = new List<string>();
        foreach (var p in paths)
        {
            string ext = Path.GetExtension(p);
            if (SubExts.Contains(ext) || (AudioExts.Contains(ext) && !_idle)) AddFiles(new[] { p });
            else media.Add(p);
        }
        if (media.Count == 0) return;

        string? current = _idle ? null : m.GetString("path");
        if (SamePath(current, media[0]) && media.Count == 1)
        {
            ReloadCurrent();
            return;
        }
        int target = -1;
        foreach (var f in media)
        {
            int idx = PlaylistIndexOf(f);
            if (idx < 0)
            {
                m.Command("loadfile", f, "append");
                idx = PlaylistIndexOf(f);
            }
            if (target < 0) target = idx;
        }
        if (target >= 0) m.Command("playlist-play-index", target.ToString(Inv));
    }

    /// <summary>另一个 HdrPlayer 实例转来的文件（例如在资源管理器里又双击了一个视频）：切到前台并打开。</summary>
    public void OpenFromOtherInstance(string[] paths)
    {
        if (_shuttingDown) return;
        if (WindowState == WindowState.Minimized) WindowState = _fullscreen ? WindowState.Normal : _prevStateBeforeMinimize;
        Activate();
        if (!_ready) { _pendingOpen.Add(paths); return; }
        OpenFromShell(ExistingPaths(paths));
    }

    /// <summary>退出时保存播放列表（只保存文件列表；每个文件的进度单独记在 file-states.json 里）。</summary>
    private void SavePlaylistState() =>
        new SavedPlaylist { Files = _playlist.Select(e => e.Path).Where(p => p.Length > 0).ToList() }.Save();

    /// <summary>
    /// 单击画面：等双击时间过去、确认不是双击后再暂停 / 播放，免得双击全屏时画面先停一下。
    /// 窗口没激活时的那一下单击只用来激活窗口，不暂停。
    /// </summary>
    private void OnVideoClick()
    {
        if (_idle) return;
        var now = DateTime.UtcNow;
        if ((now - _lastDblClick).TotalMilliseconds < DoubleClickMs + 100) return; // 双击的第二下
        // 点在后台窗口上的那一下只用来激活窗口：窗口之前确实在后台待过（不是一闪而过的焦点变化），
        // 并且刚刚才激活（mpv 在松开按键时才发来单击，所以留 600ms）
        bool wasInBackground = _activatedAt - _deactivatedAt > TimeSpan.FromMilliseconds(500);
        if (wasInBackground && (now - _activatedAt).TotalMilliseconds < 600) return;
        if (_clickTimer == null)
        {
            _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DoubleClickMs) };
            _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); DoAction("pause"); };
        }
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    /// <summary>mpv 发来的 script-message：hp &lt;动作&gt; [参数]（按键绑定、右键菜单都走这里）。</summary>
    private void OnClientMessage(string[] args)
    {
        if (args.Length >= 2 && args[0] == "hp") DoAction(args[1], args.Length >= 3 ? args[2] : null);
    }

    // =====================================================================
    //  动作（键盘 / 鼠标 / 菜单共用）
    // =====================================================================

    private void DoAction(string action, string? arg = null)
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
            // 跳转时不用 mpv 自己的 OSD 进度条（osd-msg-bar，上面的分格是片子的章节标记），改为显示本程序的控制栏
            case "seek-back": Seek(-S.SeekStep, seekMode); break;
            case "seek-fwd": Seek(S.SeekStep, seekMode); break;
            case "seek-back-big": Seek(-S.SeekStepLarge, seekMode); break;
            case "seek-fwd-big": Seek(S.SeekStepLarge, seekMode); break;
            case "vol-up": m.CommandString($"no-osd add volume {S.VolumeStep.ToString(Inv)}"); m.ShowText("音量 ${volume}"); break;
            case "vol-down": m.CommandString($"no-osd add volume {(-S.VolumeStep).ToString(Inv)}"); m.ShowText("音量 ${volume}"); break;
            case "mute": ToggleMute(); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "click": OnVideoClick(); break;
            case "dblclick":
                _lastDblClick = DateTime.UtcNow;
                _clickTimer?.Stop();
                ToggleFullscreen();
                break;
            case "reload": ReloadCurrent(); break;
            case "restart":
                if (!_idle) { m.CommandString("no-osd seek 0 absolute+exact"); RevealBarForKeyboard(); }
                break;
            case "exit-fullscreen": if (_fullscreen) ToggleFullscreen(); break;
            case "menu": ShowContextMenu(); break;
            case "aid":
                if (arg == null) break;
                m.SetProperty("aid", arg);
                _audioTouched = true;
                m.ShowText("音轨：${aid} ${current-tracks/audio/lang:} ${current-tracks/audio/title:}");
                break;
            case "sid":
                if (arg == null) break;
                m.SetProperty("sid", arg);
                _subTouched = true;
                m.ShowText(arg == "no" ? "字幕：关" : "字幕：${sid} ${current-tracks/sub/lang:} ${current-tracks/sub/title:}");
                break;
            case "hwdec":
                if (arg != null) SetFileHwdec(arg == "global" ? null : arg);
                break;
            case "audio-delay-dec": AdjustAudioDelay(-S.SubDelayStepMs); break;
            case "audio-delay-inc": AdjustAudioDelay(S.SubDelayStepMs); break;
            case "audio-delay-reset": m.SetProperty("audio-delay", 0.0); m.ShowText("音频延迟：0 ms"); break;
            case "sub-delay-reset": m.SetProperty("sub-delay", 0.0); m.ShowText("字幕延迟：0 ms"); break;
            case "reveal": RevealFile(m.GetString("path")); break;
            case "load-sub": LoadSubtitle(); break;
            case "sub-sync": StartSubSync(); break;
            case "repair": StartRepair(RepairTarget()); break;
            case "repair-cancel": _repair.Cancel(); break;
            case "load-audio": LoadAudioTrack(); break;
            case "settings": OpenSettings(); break;
            case "quit": Close(); break;
            case "hdr": SetHdr(!_hdrOn, manual: true); break;
            case "sub-delay-dec": AdjustSubDelay(-S.SubDelayStepMs); break;
            case "sub-delay-inc": AdjustSubDelay(S.SubDelayStepMs); break;
            case "sub-toggle": m.CommandString("no-osd cycle sub-visibility"); m.ShowText("字幕显示：${sub-visibility}"); break;
            case "sub-cycle": _subTouched = true; m.CommandString("no-osd cycle sub"); m.ShowText("字幕：${sid} ${current-tracks/sub/title:}"); break;
            case "audio-cycle": _audioTouched = true; m.CommandString("no-osd cycle audio"); m.ShowText("音轨：${aid} ${current-tracks/audio/title:}"); break;
            case "info": ShowInfo(); break;
            case "playlist": TogglePlaylist(); break;
            case "prev": m.Command("playlist-prev"); break;
            case "next": m.Command("playlist-next"); break;
            case "open": OpenFiles(); break;
            case "frame-step": m.CommandString("frame-step"); RevealBarForKeyboard(); break;
            case "frame-back": m.CommandString("frame-back-step"); RevealBarForKeyboard(); break;
        }
    }

    /// <summary>
    /// 静音 = 音量拉到 0（图标随之变成静音）；再点一次解除，音量一律回到 5%。
    /// 拖音量条也一样：拖到 0 就是静音，往右拖离开 0 就解除。
    /// </summary>
    private void ToggleMute()
    {
        var m = _mpv;
        if (m == null) return;
        m.SetProperty("mute", false);
        if ((m.GetDouble("volume") ?? 0) > 0)
        {
            m.SetProperty("volume", 0.0);
            m.ShowText("静音");
        }
        else
        {
            m.SetProperty("volume", UnmuteVolume);
            m.ShowText($"音量 {UnmuteVolume.ToString(Inv)}");
        }
    }

    private void AdjustAudioDelay(int ms)
    {
        var m = _mpv;
        if (m == null) return;
        m.CommandString($"no-osd add audio-delay {(ms / 1000.0).ToString(Inv)}");
        double d = m.GetDouble("audio-delay") ?? 0;
        // mpv 的 audio-delay 为正表示声音推迟播放
        m.ShowText($"音频延迟：{d * 1000:+0;-0;0} ms（{(d < 0 ? "提前" : d > 0 ? "延后" : "无")}）");
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
            (ModifierKeys.None, Key.Home) => "restart",
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

    private void AddFiles(IEnumerable<string> paths)
    {
        var m = _mpv;
        if (m == null) return;
        foreach (var p in paths)
        {
            string ext = Path.GetExtension(p);
            if (SubExts.Contains(ext))
            {
                m.Command("sub-add", p, "select");
                _subTouched = true;
                m.ShowText("已加载字幕：" + Path.GetFileName(p));
            }
            else if (AudioExts.Contains(ext) && !_idle)
            {
                m.Command("audio-add", p, "select");
                _audioTouched = true;
                m.ShowText("已加载音轨：" + Path.GetFileName(p));
            }
            else
            {
                m.Command("loadfile", p, "append-play");
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
        foreach (var f in PickFiles(SubFilter, "加载字幕", true)) { _mpv?.Command("sub-add", f, "select"); _subTouched = true; }
    }

    private void LoadAudioTrack()
    {
        foreach (var f in PickFiles(AudioFilter, "加载外部音轨", false)) { _mpv?.Command("audio-add", f, "select"); _audioTouched = true; }
    }

    private void RefreshPlaylist()
    {
        var items = new List<(string Path, string Name, bool Current)>();
        if (_mpv?.GetNode("playlist") is List<object?> nodes)
        {
            foreach (var n in nodes)
            {
                if (n is not Dictionary<string, object?> d) continue;
                string path = d.GetValueOrDefault("filename") as string ?? "";
                string name = d.GetValueOrDefault("title") as string ?? Path.GetFileName(path);
                // 只看 current：切换文件的过程中 mpv 会短暂地让旧文件还是 playing、新文件已经是 current，
                // 以前两个都算，播放列表就会同时有两项显示为正在播放
                items.Add((path, name, d.GetValueOrDefault("current") is true));
            }
        }

        // 文件没变（只是换了正在播放的项）：原地更新，不重建列表，滚动位置和多选都保留
        if (items.Count == _playlist.Count && items.Select(i => i.Path).SequenceEqual(_playlist.Select(e => e.Path)))
        {
            for (int i = 0; i < items.Count; i++) _playlist[i].Current = items[i].Current;
        }
        else
        {
            var selected = PlaylistBox.SelectedItems.OfType<PlaylistEntry>().Select(e => e.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int firstSel = PlaylistBox.SelectedIndex;
            var list = items.Select((it, i) => new PlaylistEntry(i, it.Name, it.Path) { Current = it.Current }).ToList();
            _playlist = list;
            PlaylistBox.ItemsSource = list;
            var keep = list.Where(e => selected.Contains(e.Path)).ToList();
            foreach (var e in keep) PlaylistBox.SelectedItems.Add(e);
            // 删掉了选中的项：选中原来位置上的下一项，方便连续按 Delete
            if (keep.Count == 0 && firstSel >= 0 && list.Count > 0) PlaylistBox.SelectedIndex = Math.Min(firstSel, list.Count - 1);
            UpdatePlaylistFlags();
            foreach (var e in list) ApplyThumb(e);
        }
        _bar.BtnPrev.IsEnabled = _bar.BtnNext.IsEnabled = items.Count > 1;
    }

    /// <summary>按 _failedPaths 和缩略图的结果更新“打不开”的标记。</summary>
    private void UpdatePlaylistFlags()
    {
        foreach (var e in _playlist)
        {
            string key = FullPath(e.Path);
            e.Failed = _failedPaths.Contains(key) ||
                       (_thumbInfo.TryGetValue(key, out var info) && info.Kind == PlaylistThumbs.Kind.Failed);
        }
    }

    // ---- 播放列表缩略图 ----

    /// <summary>缩略图应该截哪个位置：记住的进度（0 = 没有，取片长 10% 处）。</summary>
    private double ThumbPos(string path) => _fileStates.Get(path)?.Position ?? 0;

    /// <summary>
    /// 给列表项套上缓存的缩略图；没有或位置变了就请求生成（生成好之前先显示旧图）。
    /// 播放列表没打开时不生成，打开时再补（免得一启动就在后台读一堆文件）。
    /// </summary>
    private void ApplyThumb(PlaylistEntry e, bool urgent = false)
    {
        if (e.Path.Length == 0 || e.Path.Contains("://")) return;
        string key = FullPath(e.Path);
        double pos = ThumbPos(key);
        _thumbInfo.TryGetValue(key, out var info);
        if (info != null)
        {
            e.Thumbnail = info.Image;
            e.IsAudio = info.Kind == PlaylistThumbs.Kind.NoVideo;
        }
        if (info != null && info.Pos == pos) return;
        if (PlaylistPanel.Visibility != Visibility.Visible) return;
        _thumbInfo[key] = new ThumbInfo { Pos = pos, Image = info?.Image, Kind = info?.Kind ?? PlaylistThumbs.Kind.None };
        _plThumbs.Request(key, pos, urgent);
    }

    /// <summary>这个文件的进度变了（切走 / 停止时）：更新列表里它的缩略图。</summary>
    private void RefreshEntryThumb(string path, bool urgent)
    {
        foreach (var e in _playlist)
            if (SamePath(e.Path, path)) ApplyThumb(e, urgent);
    }

    private void OnPlaylistThumb(string path, double pos, PlaylistThumbs.Kind kind, System.Windows.Media.Imaging.BitmapSource? bmp)
    {
        if (!_thumbInfo.TryGetValue(path, out var info) || info.Pos != pos) return; // 已经有更新的请求了
        info.Kind = kind;
        // 这次没截到（超时等）就先留着旧图
        if (kind == PlaylistThumbs.Kind.Image) info.Image = bmp;
        else if (kind != PlaylistThumbs.Kind.None) info.Image = null;
        foreach (var e in _playlist)
        {
            if (!SamePath(e.Path, path)) continue;
            e.Thumbnail = info.Image;
            e.IsAudio = kind == PlaylistThumbs.Kind.NoVideo;
        }
        if (kind == PlaylistThumbs.Kind.Failed) UpdatePlaylistFlags();
    }

    // ---- 选中项的操作（Ctrl / Shift 可以多选）----

    private List<PlaylistEntry> SelectedEntries() =>
        PlaylistBox.SelectedItems.OfType<PlaylistEntry>().OrderBy(e => e.Index).ToList();

    /// <summary>右键菜单：选中多项时只有“移除”；一项都没选（点在空白处）不弹。</summary>
    private void OnPlaylistMenuOpening(object sender, ContextMenuEventArgs e)
    {
        int n = PlaylistBox.SelectedItems.Count;
        if (n == 0) { e.Handled = true; return; }
        bool multi = n > 1;
        var single = multi ? Visibility.Collapsed : Visibility.Visible;
        PlMenuPlay.Visibility = PlMenuReveal.Visibility = PlMenuSep.Visibility = PlMenuRepair.Visibility = PlMenuInfo.Visibility = single;
        PlMenuRemove.Header = multi ? $"移除选中的 {n} 项" : "移除";
        string? path = (PlaylistBox.SelectedItem as PlaylistEntry)?.Path;
        PlMenuRepair.Header = _repair.Running ? "取消修复视频" : "修复视频（重新封装）…";
        PlMenuRepair.IsEnabled = _repair.Running || (path != null && File.Exists(path));
    }

    private void PlaySelected()
    {
        if (PlaylistBox.SelectedItem is PlaylistEntry e)
            _mpv?.Command("playlist-play-index", e.Index.ToString(Inv));
    }

    private void RemoveSelected()
    {
        var m = _mpv;
        if (m == null) return;
        // 从后往前删，前面的序号不会变
        foreach (var e in SelectedEntries().OrderByDescending(e => e.Index))
            m.Command("playlist-remove", e.Index.ToString(Inv));
    }

    private void RevealSelected()
    {
        if (PlaylistBox.SelectedItem is PlaylistEntry e) RevealFile(e.Path);
    }

    /// <summary>在资源管理器中打开文件所在的文件夹并选中它。</summary>
    private static void RevealFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"");
    }

    private void TogglePlaylist()
    {
        if (_fullscreen) return;
        bool show = PlaylistPanel.Visibility != Visibility.Visible;
        PlaylistPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        S.PlaylistVisible = show;
        if (show) foreach (var e in _playlist) ApplyThumb(e);
    }

    // =====================================================================
    //  每个视频单独的解码方式
    // =====================================================================

    private static string HwdecName(string value) =>
        SettingsWindow.HwdecChoices.FirstOrDefault(c => c.Value == value).Name ?? value;

    /// <summary>当前文件单独选的解码方式；null = 跟随全局设置（或没在播放）。</summary>
    private string? FileHwdec()
    {
        string? path = _currentPath ?? (_idle ? null : _mpv?.GetString("path"));
        return path == null ? null : _fileStates.Get(path)?.Hwdec;
    }

    /// <summary>当前文件实际使用的解码方式：本视频的选择优先，否则用全局设置。</summary>
    private string EffectiveHwdec() => FileHwdec() ?? S.Hwdec;

    /// <summary>
    /// mpv 的 on_load 钩子（事件线程，mpv 等它返回才继续打开文件）：在解码器创建之前，
    /// 用 file-local-options 设置这个文件的解码方式。file-local 的值在文件结束时自动失效，不影响下一个文件。
    /// </summary>
    private void OnLoadHook(Mpv m)
    {
        string? path = m.GetString("path");
        string? perFile = path == null ? null : _fileStates.Get(path)?.Hwdec;
        m.SetProperty("file-local-options/hwdec", perFile ?? S.Hwdec);
    }

    /// <summary>右键菜单里改当前视频的解码方式（null = 跟随全局设置），立即生效并记住。</summary>
    private void SetFileHwdec(string? value)
    {
        var m = _mpv;
        string? path = _currentPath ?? (_idle ? null : m?.GetString("path"));
        if (m == null || path == null) return;
        var st = _fileStates.Get(path) ?? new FileState { LastPlayed = DateTime.UtcNow };
        st.Hwdec = value;
        _fileStates.Set(path, st);
        m.SetProperty("file-local-options/hwdec", value ?? S.Hwdec);
        m.ShowText(value == null
            ? $"本视频的解码方式：跟随全局设置（{HwdecName(S.Hwdec)}）"
            : $"本视频的解码方式：{HwdecName(value)}");
    }

    // =====================================================================
    //  自动同步字幕（ffsubsync）
    // =====================================================================

    private static readonly HashSet<string> TextSubCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text" };
    private static readonly HashSet<string> SyncableSubExts = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".ass", ".ssa", ".vtt", ".sub" };

    /// <summary>
    /// 用 ffsubsync 把当前字幕对齐到当前音轨的人声，生成“原名.synced.后缀”并切换过去（再选一次菜单项取消）。
    /// 当前字幕是外挂文件就同步那个文件；是内嵌文字字幕就先提取出来；没有选字幕就让用户选一个文件。
    /// </summary>
    private async void StartSubSync()
    {
        var m = _mpv;
        if (m == null || _idle) return;
        if (_subSync.Running) { _subSync.Cancel(); return; }
        if (SubSync.FindExe() == null)
        {
            await Dialogs.ShowAsync(this, "自动同步字幕",
                "没有找到 ffsubsync。\n\n发布版（安装版 / 便携版）自带，在程序目录的 tools\\ffsubsync 里。" +
                "自己编译的话可以 pip install ffsubsync，并把 ffmpeg / ffprobe 放进 PATH。");
            return;
        }
        string? video = m.GetString("path");
        if (video == null || !File.Exists(video)) { m.ShowText("只能同步本地文件的字幕"); return; }

        var tracks = (m.GetNode("track-list") as List<object?>)?.OfType<Dictionary<string, object?>>().ToList() ?? new();
        static string Str(Dictionary<string, object?> t, string k) => t.GetValueOrDefault(k) as string ?? "";
        static long Id(Dictionary<string, object?> t) => t.GetValueOrDefault("id") is long l ? l : 0;
        static bool Is(Dictionary<string, object?> t, string k) => t.GetValueOrDefault(k) is true;
        // mpv 的内嵌轨按流的顺序编号，所以“同类内嵌轨里排第几”就是 ffmpeg 的 0:a:N / 0:s:N
        int RelIndex(Dictionary<string, object?> t) =>
            tracks.Count(o => Str(o, "type") == Str(t, "type") && !Is(o, "external") && Id(o) < Id(t));

        // 参照音轨：当前选中的（外挂音轨就用那个音频文件）
        var audio = tracks.FirstOrDefault(t => Str(t, "type") == "audio" && Is(t, "selected"));
        if (audio == null) { m.ShowText("当前没有音轨，无法按人声同步字幕", 3000); return; }
        string reference = video, audioStream;
        if (Is(audio, "external") && File.Exists(Str(audio, "external-filename")))
            (reference, audioStream) = (Str(audio, "external-filename"), "0:a:0");
        else
            audioStream = $"0:a:{RelIndex(audio)}";

        // 要同步的字幕
        var sub = tracks.FirstOrDefault(t => Str(t, "type") == "sub" && Is(t, "selected"));
        string? subFile = null, embedded = null, lang = sub == null ? null : Str(sub, "lang");
        string ext;
        if (sub == null)
        {
            var picked = PickFiles(SubFilter, "选择要同步的字幕", false);
            if (picked.Length == 0) return;
            subFile = picked[0];
            ext = Path.GetExtension(subFile);
        }
        else if (Is(sub, "external"))
        {
            subFile = Str(sub, "external-filename");
            if (!File.Exists(subFile)) { m.ShowText("当前字幕不是本地文件，无法同步", 3000); return; }
            ext = Path.GetExtension(subFile);
        }
        else
        {
            string codec = Str(sub, "codec");
            if (!TextSubCodecs.Contains(codec)) { m.ShowText($"当前是图形字幕（{codec}），只能同步文字字幕", 3000); return; }
            embedded = $"0:s:{RelIndex(sub)}";
            ext = codec is "ass" or "ssa" ? ".ass" : ".srt";
        }
        if (!SyncableSubExts.Contains(ext)) { m.ShowText($"不支持同步 {ext} 字幕（支持 srt / ass / ssa / vtt）", 3000); return; }

        // 输出：字幕旁边的“原名.synced.后缀”（内嵌字幕放在视频旁边）；目录不可写时放到 %LOCALAPPDATA%\HdrPlayer\synced
        string baseName = subFile != null
            ? Path.GetFileNameWithoutExtension(subFile)
            : Path.GetFileNameWithoutExtension(video) + (string.IsNullOrEmpty(lang) ? "" : "." + lang);
        if (baseName.EndsWith(".synced", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^".synced".Length];
        string dir = Path.GetDirectoryName(subFile ?? video) ?? "";
        if (!DirectoryWritable(dir))
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HdrPlayer", "synced");
            Directory.CreateDirectory(dir);
        }
        string output = Path.Combine(dir, baseName + ".synced" + ext.ToLowerInvariant());

        string status = "正在同步字幕…";
        var started = DateTime.UtcNow;
        void ShowProgress() => m.ShowText($"{status}　{(DateTime.UtcNow - started).TotalSeconds:0} 秒\n（右键 → 字幕延迟 → 取消自动同步）", 1500);
        _subSyncOsd?.Stop();
        _subSyncOsd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _subSyncOsd.Tick += (_, _) => ShowProgress();
        _subSyncOsd.Start();
        ShowProgress();

        var res = await _subSync.RunAsync(video, reference, audioStream, subFile, embedded, output,
            msg => Dispatcher.InvokeAsync(() => { status = msg; ShowProgress(); }));
        _subSyncOsd.Stop();
        if (_shuttingDown || _mpv == null) return;
        if (!res.Ok) { m.ShowText(res.Message, 5000); return; }
        if (m.GetString("path") != video)
        {
            m.ShowText("字幕已同步，保存为 " + Path.GetFileName(output), 4000);
            return;
        }

        // 同名文件之前已经加载过（再次同步覆盖了它）：先移除旧的轨，避免出现两条一样的
        if (m.GetNode("track-list") is List<object?> now)
            foreach (var t in now.OfType<Dictionary<string, object?>>())
                if (Str(t, "type") == "sub" && Is(t, "external") &&
                    string.Equals(Str(t, "external-filename"), output, StringComparison.OrdinalIgnoreCase))
                    m.Command("sub-remove", Id(t).ToString(Inv));
        m.Command("sub-add", output, "select", "自动同步", lang ?? "");
        _subTouched = true;
        // 偏移已经写进字幕文件，之前手动调的字幕延迟要清零，否则会叠加
        m.SetProperty("sub-delay", 0.0);
        string scale = Math.Abs(res.Scale - 1) > 0.0005 ? $"，帧率校正 ×{res.Scale.ToString("0.0000", Inv)}" : "";
        m.ShowText($"字幕已同步：整体偏移 {res.Offset.ToString("+0.00;-0.00;0.00", Inv)} 秒{scale}\n已保存为 {Path.GetFileName(output)}", 5000);
    }

    // =====================================================================
    //  修复视频（ffmpeg 重新封装）
    // =====================================================================

    /// <summary>
    /// 画面右键菜单里“修复视频”针对的文件：正在播放的本地文件；没在播放时是刚才打不开的那个文件
    /// （格式无法识别时 mpv 不会载入它，画面上的菜单本来没有“本视频”）。
    /// </summary>
    private string? RepairTarget()
    {
        string? path = _idle ? null : _mpv?.GetString("path");
        if (path != null && File.Exists(path)) return FullPath(path);
        return _idle && _lastFailedPath != null && File.Exists(_lastFailedPath) ? _lastFailedPath : null;
    }

    /// <summary>
    /// 不重新编码地重新封装成“原名.repaired.mkv”（原文件不动），完成后在播放列表里替换掉原文件；
    /// 原文件正在播放（或刚才打不开）时直接播放修复好的，接着原来的进度、延迟和音轨 / 字幕。
    /// </summary>
    private async void StartRepair(string? source)
    {
        var m = _mpv;
        if (m == null) return;
        if (_repair.Running) { _repair.Cancel(); return; }
        if (source == null || !File.Exists(source)) { m.ShowText("只能修复本地文件"); return; }
        source = FullPath(source);
        if (VideoRepair.FindFfmpeg() == null || VideoRepair.FindFfprobe() == null)
        {
            await Dialogs.ShowAsync(this, "修复视频",
                "没有找到 ffmpeg / ffprobe。\n\n发布版（安装版 / 便携版）自带，在程序目录的 tools\\ffmpeg 里。" +
                "自己编译的话可以把 ffmpeg.exe / ffprobe.exe 放进 PATH。");
            return;
        }

        // 输出：原文件旁边的“原名.repaired.mkv”（已存在就加序号）；目录不可写时让用户选位置
        string name = Path.GetFileNameWithoutExtension(source);
        int cut = name.LastIndexOf(".repaired", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) name = name[..cut];
        string dir = Path.GetDirectoryName(source) ?? "";
        string output;
        if (DirectoryWritable(dir))
        {
            output = Path.Combine(dir, name + ".repaired.mkv");
            for (int i = 2; File.Exists(output) || File.Exists(output + ".part"); i++)
                output = Path.Combine(dir, $"{name}.repaired-{i}.mkv");
        }
        else
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "原文件所在的文件夹不能写入，选择修复后的文件保存到哪里",
                FileName = name + ".repaired.mkv",
                Filter = "MKV 视频|*.mkv",
                DefaultExt = ".mkv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            };
            if (dlg.ShowDialog(this) != true) return;
            output = dlg.FileName;
            if (SamePath(output, source)) { m.ShowText("不能覆盖原文件", 3000); return; }
            try { File.Delete(output); } catch { } // 用户在对话框里确认过覆盖
        }

        long size = 0;
        try { size = new FileInfo(source).Length; } catch { }
        bool go = await Dialogs.ConfirmAsync(this, "修复视频",
            $"把 {Path.GetFileName(source)} 重新封装成 MKV（不重新编码，画质不变），另存为：\n{output}\n\n" +
            $"原文件不会改动。需要大约 {VideoRepair.FormatSize(size)} 的空间，耗时主要取决于硬盘速度。\n\n" +
            "能修好：拖不动进度条、时长显示不对、时间戳错乱导致的音画不同步或卡住、电视录像（TS）里的坏包、扩展名和实际格式不符。\n" +
            "修不好：画面本身花屏 / 马赛克；录制中断、缺少索引（moov）的 MP4 / MOV。",
            "开始修复");
        if (!go || _shuttingDown || _mpv == null) return;

        double fraction = 0;
        var started = DateTime.UtcNow;
        string shortName = Path.GetFileName(source);
        void ShowProgress() =>
            m.ShowText($"正在修复 {shortName}　{fraction * 100:0}%　{(DateTime.UtcNow - started).TotalSeconds:0} 秒\n（右键 → 取消修复视频）", 1500);
        _repairOsd?.Stop();
        _repairOsd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _repairOsd.Tick += (_, _) => ShowProgress();
        _repairOsd.Start();
        ShowProgress();

        var res = await _repair.RunAsync(source, output, f => fraction = f);
        _repairOsd.Stop();
        if (_shuttingDown || _mpv == null) return;
        if (!res.Ok)
        {
            m.ShowText("", 1);
            if (res.Message == "已取消修复。") m.ShowText(res.Message, 3000);
            else await Dialogs.ShowAsync(this, "修复视频", res.Message);
            return;
        }
        ReplaceWithRepaired(source, output);
        m.ShowText($"修复完成，已保存为 {Path.GetFileName(output)}（原文件没有改动）" +
            (res.DroppedStreams ? "\n有些字幕 / 附件 / 数据流放不进 MKV，已省略" : ""), 5000);
    }

    /// <summary>修复完成：播放列表里用修复好的文件替换原文件，记住的进度等状态也带过去。</summary>
    private void ReplaceWithRepaired(string source, string output)
    {
        var m = _mpv;
        if (m == null) return;
        bool isCurrent = !_idle && SamePath(m.GetString("path"), source);
        bool wasFailed = _idle && SamePath(_lastFailedPath, source);
        // 正在播放：先把当前的进度记下来（之后它结束时就不会再记一次）
        if (isCurrent) CommitFileState(_timePos, finished: false);
        if (_fileStates.Get(source) is { } st)
        {
            _fileStates.Set(output, new FileState
            {
                Position = st.Position, AudioDelay = st.AudioDelay, SubDelay = st.SubDelay, Hwdec = st.Hwdec,
                Audio = st.Audio, Sub = st.Sub, LastPlayed = DateTime.UtcNow,
            });
        }
        _failedPaths.Remove(source);
        if (wasFailed) _lastFailedPath = null;

        int idx = PlaylistIndexOf(source);
        m.Command("loadfile", output, "append");
        int last = (int)(m.GetInt("playlist-count") ?? 1) - 1;
        int target = last;
        if (idx >= 0 && idx < last)
        {
            // 挪到原文件的位置上（原文件往后挪一位），再删掉原文件
            m.Command("playlist-move", last.ToString(Inv), idx.ToString(Inv));
            target = idx;
        }
        if (isCurrent || wasFailed) m.Command("playlist-play-index", target.ToString(Inv));
        if (idx >= 0) m.Command("playlist-remove", (target == idx ? idx + 1 : idx).ToString(Inv));
    }

    private static bool DirectoryWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".hdrplayer-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // =====================================================================
    //  右键菜单
    // =====================================================================

    /// <summary>
    /// 菜单项：Cmd 是点击时执行的 mpv 命令（一般是 script-message hp …，回到 DoAction 处理）。
    /// Checked 为 null 表示不是可勾选的项；true / false 的项前面留出对勾的位置，选中的显示对勾。
    /// </summary>
    private sealed record MenuEntry(string Title, string? Cmd = null, string? Shortcut = null,
        bool? Checked = null, bool Enabled = true, List<MenuEntry>? Sub = null)
    {
        public bool IsSeparator => Title.Length == 0 && Cmd == null && Sub == null;
    }

    private static readonly MenuEntry Sep = new("");

    private static string Hp(string action, string? arg = null) => arg == null ? $"script-message hp {action}" : $"script-message hp {action} {arg}";

    /// <summary>视频画面 / 控制栏的右键菜单内容（每次弹出时按当前状态重新生成）。</summary>
    private List<MenuEntry> BuildContextMenu()
    {
        var m = _mpv;
        bool playing = !_idle;
        string? path = playing ? m?.GetString("path") : null;
        var items = new List<MenuEntry>
        {
            new("视频参数…", Hp("info"), "I"),
            new("在资源管理器中显示", Hp("reveal"), Enabled: path != null && File.Exists(path)),
            Sep,
            new("打开文件…", Hp("open"), "Ctrl+O"),
            new("加载字幕…", Hp("load-sub"), Enabled: playing),
            new("加载外部音轨…", Hp("load-audio"), Enabled: playing),
            new("重新载入当前文件", Hp("reload"), "F5", Enabled: playing),
        };
        // 修复视频：格式无法识别时文件根本没载入，这时针对刚才打不开的那个文件
        string? repairTarget = RepairTarget();
        if (_repair.Running)
            items.Add(new MenuEntry("取消修复视频", Hp("repair-cancel")));
        else if (repairTarget != null && !SamePath(repairTarget, path))
            items.Add(new MenuEntry($"修复无法打开的文件：{Path.GetFileName(repairTarget)}…", Hp("repair")));
        else
            items.Add(new MenuEntry("修复视频（重新封装）…", Hp("repair"), Enabled: repairTarget != null));
        items.Add(Sep);

        var audio = new List<MenuEntry>();
        var subs = new List<MenuEntry> { new("关闭字幕", Hp("sid", "no"), Checked: m?.GetString("sid") is "no" or null) };
        if (playing && m?.GetNode("track-list") is List<object?> tracks)
        {
            foreach (var t in tracks.OfType<Dictionary<string, object?>>())
            {
                string type = t.GetValueOrDefault("type") as string ?? "";
                if (type != "audio" && type != "sub") continue;
                long id = t.GetValueOrDefault("id") is long l ? l : 0;
                var entry = new MenuEntry(TrackText(t), Hp(type == "audio" ? "aid" : "sid", id.ToString(Inv)),
                    Checked: t.GetValueOrDefault("selected") is true);
                (type == "audio" ? audio : subs).Add(entry);
            }
        }
        items.Add(new MenuEntry("音轨", Sub: audio, Enabled: audio.Count > 0));
        items.Add(new MenuEntry("字幕", Sub: subs, Enabled: playing));

        // 音频 / 字幕延迟：按文件记住（见 file-states.json），所以放在这里而不是全局设置里
        int step = S.SubDelayStepMs;
        double ad = (m?.GetDouble("audio-delay") ?? 0) * 1000, sd = (m?.GetDouble("sub-delay") ?? 0) * 1000;
        items.Add(new MenuEntry("音频延迟", Enabled: playing, Sub: new()
        {
            new($"当前：{ad:+0;-0;0} ms", Enabled: false),
            new($"提前 {step} ms", Hp("audio-delay-dec")),
            new($"延后 {step} ms", Hp("audio-delay-inc")),
            new("重置", Hp("audio-delay-reset")),
        }));
        items.Add(new MenuEntry("字幕延迟", Enabled: playing, Sub: new()
        {
            new($"当前：{sd:+0;-0;0} ms", Enabled: false),
            new($"提前 {step} ms", Hp("sub-delay-dec"), "Z"),
            new($"延后 {step} ms", Hp("sub-delay-inc"), "X"),
            new("重置", Hp("sub-delay-reset")),
            Sep,
            _subSync.Running
                ? new MenuEntry("取消自动同步", Hp("sub-sync"))
                : new MenuEntry("自动同步字幕（ffsubsync）…", Hp("sub-sync"), Enabled: path != null && File.Exists(path)),
        }));
        items.Add(Sep);

        items.Add(new MenuEntry("HDR 输出", Hp("hdr"), "H", Checked: _hdrOn));
        // 解码方式：只对当前这个视频生效并按文件记住，优先于全局设置
        string? fileHwdec = FileHwdec();
        var hwdec = new List<MenuEntry>
        {
            new($"跟随全局设置（{HwdecName(S.Hwdec)}）", Hp("hwdec", "global"), Checked: playing && fileHwdec == null),
            Sep,
        };
        hwdec.AddRange(SettingsWindow.HwdecChoices.Select(c => new MenuEntry(c.Name, Hp("hwdec", c.Value), Checked: fileHwdec == c.Value)));
        items.Add(new MenuEntry("解码方式（本视频）", Sub: hwdec, Enabled: playing));
        items.Add(Sep);
        items.Add(new MenuEntry("播放列表", Hp("playlist"), "P", Checked: PlaylistPanel.Visibility == Visibility.Visible, Enabled: !_fullscreen));
        items.Add(new MenuEntry("全屏", Hp("fullscreen"), "F / 双击", Checked: _fullscreen));
        items.Add(new MenuEntry("全局设置…", Hp("settings")));
        items.Add(Sep);
        items.Add(new MenuEntry("退出", Hp("quit")));
        return items;
    }

    /// <summary>
    /// 弹出右键菜单（WPF 菜单，样式和播放列表的菜单一致）。
    /// 画面上的右键由 VideoHost 在松开时直接通知（它和 WPF 同一个线程，菜单能正常拿到鼠标和焦点）。
    /// 以前的两个版本都依赖 mpv 的 MBTN_RIGHT 绑定，但嵌入时 mpv 的窗口被禁用、根本收不到鼠标，
    /// 所以无论 WPF 菜单还是 mpv 的原生菜单（它也挂在那个被禁用的窗口上）都弹不出来。
    /// </summary>
    private void ShowContextMenu()
    {
        if (_mpv == null || _shuttingDown) return;
        var items = BuildContextMenu();
        void Open()
        {
            if (_shuttingDown) return;
            var menu = new ContextMenu();
            foreach (var mi in ToWpfMenu(items)) menu.Items.Add(mi);
            menu.Closed += (_, _) => _menuOpen = false;
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.MousePoint;
            Activate();
            _menuOpen = true;
            Video.HideCursor = false;
            menu.IsOpen = true;
        }
        if (!Native.IsRightButtonDown()) { Open(); return; }
        // 保险：如果是从 mpv 的 MBTN_RIGHT 绑定来的（按下时就触发），等右键松开再弹，否则松开的那一下会把菜单关掉
        var start = DateTime.UtcNow;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        t.Tick += (_, _) =>
        {
            if (Native.IsRightButtonDown() && (DateTime.UtcNow - start).TotalSeconds < 3) return;
            t.Stop();
            Open();
        };
        t.Start();
    }

    private List<object> ToWpfMenu(List<MenuEntry> items)
    {
        // 这一级菜单里有打勾的项时，所有项都留出对勾的位置，文字对齐；一项都没勾就都不留
        bool reserveTick = items.Any(e => e.Checked == true);
        return items.Select(e => ToWpfMenuItem(e, reserveTick)).ToList();
    }

    private object ToWpfMenuItem(MenuEntry e, bool reserveTick)
    {
        if (e.IsSeparator) return (object)new Separator();
        // MenuItem 会把 “_” 当作访问键，转义一下
        var mi = new MenuItem
        {
            Header = e.Title.Replace("_", "__"), InputGestureText = e.Shortcut ?? "", IsEnabled = e.Enabled,
        };
        // WPF-UI 的菜单只有 IsCheckable 时才画勾选框（而且每项都是一个框），这里自己在图标位置画对勾
        if (e.Checked == true)
        {
            var tick = new SymbolIcon { Symbol = SymbolRegular.Checkmark24, FontSize = 16, Width = 16 };
            tick.SetResourceReference(Control.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            mi.Icon = tick;
        }
        else if (reserveTick)
        {
            mi.Icon = new Border { Width = 16 };
        }
        if (e.Sub != null) foreach (var sub in ToWpfMenu(e.Sub)) mi.Items.Add(sub);
        if (e.Cmd != null) { string cmd = e.Cmd; mi.Click += (_, _) => _mpv?.CommandString(cmd); }
        return mi;
    }

    private static string TrackText(Dictionary<string, object?> t)
    {
        var parts = new List<string>();
        if (t.GetValueOrDefault("id") is long id) parts.Add("#" + id);
        if (t.GetValueOrDefault("lang") is string lang) parts.Add($"[{lang}]");
        if (t.GetValueOrDefault("title") is string title) parts.Add(title);
        if (t.GetValueOrDefault("codec") is string codec) parts.Add(codec);
        if (t.GetValueOrDefault("demux-channel-count") is long ch) parts.Add(ch + "ch");
        if (t.GetValueOrDefault("external") is true) parts.Add("(外部)");
        return string.Join(" ", parts);
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

    /// <summary>全屏时把窗口精确地放到所在显示器的整块屏幕上（物理像素）。</summary>
    private void ApplyFullscreenBounds()
    {
        if (!_fullscreen || _hwnd == IntPtr.Zero) return;
        var (_, mon) = Native.MonitorOf(_hwnd);
        if (mon.Width <= 0 || mon.Height <= 0) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, mon.Left, mon.Top, mon.Width, mon.Height,
            Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }

    /// <summary>Windows 11 在窗口外圈画的 1 像素边框：全屏时去掉（旧系统不支持，忽略）。</summary>
    private void SetDwmBorder(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        uint color = on ? Native.DWMWA_COLOR_DEFAULT : Native.DWMWA_COLOR_NONE;
        try { Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref color, sizeof(uint)); } catch { }
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
            // 上面按 DIP 设置，非整数缩放时换算回像素会舍入，右 / 下边可能差 1 像素露出桌面：再按物理像素精确设一次。
            // 布局完成后再设一次，防止 WPF 处理窗口状态切换时又用自己舍入后的尺寸覆盖
            ApplyFullscreenBounds();
            Dispatcher.InvokeAsync(ApplyFullscreenBounds, DispatcherPriority.Loaded);
            SetDwmBorder(false);
        }
        else
        {
            _fullscreen = false;
            AppTitleBar.Visibility = Visibility.Visible;
            PlaylistPanel.Visibility = _playlistWasVisible ? Visibility.Visible : Visibility.Collapsed;
            SetResizeBorder(true);
            SetDwmBorder(true);
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
    /// <summary>键盘跳转：用本程序的控制栏显示进度，静止 2 秒后照常淡出。</summary>
    private void Seek(double seconds, string mode)
    {
        _mpv?.CommandString($"no-osd seek {seconds.ToString(Inv)} {mode}");
        RevealBarForKeyboard();
    }

    private void RevealBarForKeyboard()
    {
        if (_idle || _shuttingDown) return;
        _lastKeyActivity = DateTime.UtcNow;
        UpdateBarVisibility();
    }

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

        bool mouseKeep = _idle || overBar || _bar.IsSeekDragging || _menuOpen ||
                         (DateTime.UtcNow - _lastActivity).TotalMilliseconds < BarHideMs;
        bool keep = mouseKeep || (DateTime.UtcNow - _lastKeyActivity).TotalMilliseconds < BarHideMs;
        if (keep)
        {
            if (!_bar.IsRevealed) PositionBar();
            _bar.Reveal();
        }
        else
        {
            _bar.Conceal();
        }
        // 控制栏淡出时画面上的鼠标指针也隐藏（键盘跳转让控制栏出现时，指针仍然保持隐藏）
        Video.HideCursor = !mouseKeep;
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
        bool ok = dlg.ShowDialog() == true;
        // 设置里的“检查更新”下载好了新版本：正常退出（保存进度、播放列表），退出时由 App 启动安装程序
        if (Updater.PendingInstaller != null)
        {
            Close();
            return;
        }
        if (!ok) return;

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
                string hdr = !d.HdrSupported ? "不支持" : d.HdrEnabled ? "开" : "关";
                if (d.HdrEnabled && !d.HdrActive) hdr += d.LimitedByPolicy ? "（被系统策略限制，未生效）" : "（未生效）";
                if (d.ActiveColorMode >= 0) hdr += $"  当前模式 {d.ActiveColorModeName}";
                Line("Windows HDR", hdr);
                if (d.HasDxgi)
                {
                    Line("位深", $"{d.DxgiBitsPerColor} bit  色彩空间 {(d.DxgiIsHdr ? "HDR10 (PQ/BT.2020)" : "sRGB")}");
                    Line("EDID 亮度", $"峰值 {d.MaxLuminance:0} nit  全屏 {d.MaxFullFrameLuminance:0} nit  最低 {d.MinLuminance:0.####} nit");
                    if (_hdrOn)
                    {
                        var (nits, src) = DisplayPeakWithSource();
                        Line("色调映射目标", $"{nits:0} nit（{src}）");
                    }
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
            if (!_idle) CommitFileState(_timePos, finished: false);
            SavePlaylistState();

            _uiTimer.Stop();
            _subSyncOsd?.Stop();
            _subSync.KillNow();
            _repairOsd?.Stop();
            _repair.KillNow();
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
                try { _plThumbs.Dispose(); } catch { }
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
