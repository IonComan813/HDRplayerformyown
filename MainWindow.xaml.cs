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

    private bool _current, _failed, _isAudio, _missing;
    private ImageSource? _thumbnail;

    /// <summary>正在播放（mpv 播放列表的 current 项，同一时间只有一项）。</summary>
    public bool Current { get => _current; set => Set(ref _current, value, nameof(Current)); }
    /// <summary>打不开（格式无法识别等），提示可以修复。</summary>
    public bool Failed { get => _failed; set => Set(ref _failed, value, nameof(Failed)); }
    /// <summary>文件不在了（软件获得焦点、加载播放列表时检查）：右上角显示红色感叹号。</summary>
    public bool Missing { get => _missing; set => Set(ref _missing, value, nameof(Missing)); }
    /// <summary>没有画面（纯音频），占位图换成音符。</summary>
    public bool IsAudio { get => _isAudio; set => Set(ref _isAudio, value, nameof(IsAudio)); }
    public ImageSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value, nameof(Thumbnail)); }
    /// <summary>缩略图右下方的“上次进度/片长”（00:00:00/00:00:00），正在播放时是“播放中”。</summary>
    public string Progress { get => _progress; set => Set(ref _progress, value, nameof(Progress)); }
    private string _progress = "";

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
    /// <summary>修复前的快速检查进行中（这时还不算 _repair.Running）。</summary>
    private bool _repairChecking;
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
    private string? _startingPath;
    /// <summary>各文件的片长（播放时 / 生成缩略图时得知），播放列表里显示“进度/片长”用。</summary>
    private readonly Dictionary<string, double> _durations = new(StringComparer.OrdinalIgnoreCase);
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
    /// <summary>右键菜单关闭的时间：关菜单的那一下点击不算单击画面（不暂停 / 播放）。</summary>
    private DateTime _menuClosedAt = DateTime.MinValue;

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
            if (_repair.Running || _repairChecking) _repair.Cancel();
            else if (PlaylistBox.SelectedItem is PlaylistEntry e) StartRepair(e.Path);
        };
        PlaylistBox.ContextMenuOpening += OnPlaylistMenuOpening;
        PlMenuOpenDir.Click += (_, _) => { if (PlaylistBox.SelectedItem is PlaylistEntry e) OpenOriginalFolder(e.Path); };
        BtnPlDeselect.Click += (_, _) => PlaylistBox.UnselectAll();
        _plThumbs.Ready += (path, pos, kind, bmp, duration) =>
        {
            if (!_shuttingDown) Dispatcher.InvokeAsync(() => OnPlaylistThumb(path, pos, kind, bmp, duration));
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
        Activated += (_, _) =>
        {
            _activatedAt = DateTime.UtcNow;
            // 回到播放器时（可能刚在资源管理器里删 / 移动了文件）检查播放列表里的文件还在不在
            CheckMissingFiles();
        };
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

    /// <summary>上次设置的音频输出方式（直通 / 声道 / 滤镜）：没变就不重设（重设会让音频链重新初始化，声音断一下）。</summary>
    private volatile string? _lastAudio;

    /// <summary>直通的格式：mpv 的 audio-spdif。DTS-HD / TrueHD 要 HDMI（8 声道 IEC 61937）。</summary>
    private const string PassthroughCodecs = "ac3,eac3,dts,dts-hd,truehd";

    /// <summary>这个文件用不用音频直通：单独设置过的优先，否则用全局设置。</summary>
    private bool EffectivePassthrough(string? path) =>
        (path == null ? null : _fileStates.Get(path)?.Passthrough) ?? S.AudioPassthrough;

    /// <summary>
    /// 设置音频输出：直通时 audio-spdif 列出要直通的格式，滤镜清空（码流不能做 PCM 处理），声道数自动
    /// （下混到立体声会挡住 TrueHD / DTS-HD 这类多声道码流；不支持直通的音轨按设备的声道数正常解码）；
    /// 不直通时按下混设置和防削波策略。path = null 时用全局设置。
    /// </summary>
    private void ApplyAudioChain(Func<string, string, int> set, string? path)
    {
        bool pt = EffectivePassthrough(path);
        string spdif = pt ? PassthroughCodecs : "";
        string channels = pt ? "auto" : S.DownmixToStereo ? "stereo" : "auto-safe";
        string af = pt ? "" : AudioFilters(EffectiveAntiClip(path));
        string sig = spdif + "|" + channels + "|" + af;
        if (sig == _lastAudio) return;
        set("audio-spdif", spdif);
        set("audio-channels", channels);
        if (set("af", af) >= 0) _lastAudio = sig;
    }

    /// <summary>
    /// 防削波的音频滤镜链。mpv 平时在所有音频滤镜之后才下混，那样限幅器看到的还是下混前的声道，
    /// 所以下混到立体声时先用 format 滤镜在这里就下混好，再接后面的处理：
    /// 仅限幅：alimiter 把超过满幅的峰值压到 -0.2 dBFS 以下（level=0：不做自动电平，不改变整体音量）；
    /// 拉平音量 + 限幅：先 dynaudnorm（150 ms 一帧、15 帧高斯平滑、峰值 0.9、最多放大 8 倍，
    /// 比默认参数反应快，延迟约 1 秒，不至于明显地忽大忽小），再 alimiter。
    /// </summary>
    private string AudioFilters(string antiClip)
    {
        var parts = new List<string>();
        if (S.DownmixToStereo) parts.Add("format=channels=stereo");
        if (antiClip == "dynaudnorm") parts.Add("lavfi=[dynaudnorm=f=150:g=15:p=0.9:m=8]");
        parts.Add("lavfi=[alimiter=limit=0.977:level=0]");
        return string.Join(",", parts);
    }

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
        // 视频同步每个文件单独决定（MaybeMatchRefresh）：刷新率和帧率匹配时按显示器重采样，否则按音频同步
        if (initial) { set("video-sync", "audio"); set("interpolation", "no"); }
        set("audio-device", string.IsNullOrEmpty(S.AudioDevice) ? "auto" : S.AudioDevice);
        // 5.1 / 7.1 → 立体声由 mpv（libswresample）按标准矩阵下混；不再用系数归一化（整轨都会变小声），
        // 防削波交给音频滤镜；音频直通时都不用（见 ApplyAudioChain）
        set("audio-normalize-downmix", "no");
        ApplyAudioChain(set, initial || _idle ? null : _currentPath);
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
        bool enabled = HdrBlockedReason() == null;
        // SDR 视频 / 纯音频：画面本来就是 SDR，一律显示灰色的“SDR”（即使 Windows HDR 还开着，
        // mpv 对 SDR 片源输出的也是 sRGB 画面）
        _bar.BtnHdr.Content = _hdrOn && enabled ? "HDR" : "SDR";
        // 不能切换时只把文字变灰：不用 IsEnabled = false（WPF-UI 的按钮禁用时会画一个灰色底框，像是被强调了），
        // 而是关掉鼠标命中，点不动、也没有悬停效果。能切换时 SDR 是白字，HDR 是黄字
        _bar.BtnHdr.Foreground = (Brush)FindResource(!enabled ? "TextFillColorDisabledBrush" : _hdrOn ? "HdrOnBrush" : "TextFillColorPrimaryBrush");
        _bar.BtnHdr.IsHitTestVisible = enabled;
    }

    /// <summary>
    /// 不能手动开关 HDR 输出的原因（null = 可以）：当前是 SDR 视频，或者是纯音频 / 音乐封面，HDR 输出没有意义。
    /// 右键菜单的“HDR 输出”、快捷键 H、控制栏的 HDR 按钮都看它。没在播放、文件还在打开、
    /// 还没解码出第一帧（不知道是不是 HDR）时都可以。按片源自动切换不受影响。
    /// </summary>
    private string? HdrBlockedReason()
    {
        var m = _mpv;
        if (m == null || _idle) return null;
        var tracks = Tracks();
        if (tracks.Count == 0) return null; // 还在打开
        bool hasVideo = tracks.Any(t => TrackStr(t, "type") == "video" && !TrackFlag(t, "image"));
        if (!hasVideo) return "纯音频文件，不需要 HDR 输出";
        string? gamma = m.GetString("video-params/gamma");
        if (gamma == null || gamma is "pq" or "hlg" || _dvProfile != null) return null;
        return "当前是 SDR 视频，不需要 HDR 输出";
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
                SwapTrackDelay(audio: name == "aid");
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
                UpdateAllProgress();
                UpdateHdrButton();
                break;
            case "duration":
                _duration = value is double d ? d : 0;
                _bar.SetDuration(_duration);
                if (_duration > 0 && _currentPath != null) _durations[FullPath(_currentPath)] = _duration;
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
        _refreshChecked = false;
        _audioKey = _subKey = null;
        // 先按音频同步，视频参数出来以后再按刷新率是否匹配决定（纯音频文件就一直是音频同步）
        _refreshMatched = false;
        SetSyncMode("audio");
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
        if (_currentPath != null)
        {
            if (_currentDuration > 0) _durations[FullPath(_currentPath)] = _currentDuration;
            if (_failedPaths.Remove(FullPath(_currentPath))) UpdatePlaylistFlags();
        }
        ApplyFileState(m, resumePosition: !reloaded);
        UpdateFormatLabel();
        UpdateThumbnailSource();
        UpdateHdrButton();
        MaybeAutoHdr();
    }

    /// <summary>
    /// 打开文件时套用这个文件记住的状态：上次的播放进度、音轨 / 字幕，以及选中的那条音轨 / 字幕的延迟
    /// （延迟按音轨 / 字幕记，没记录过的归零）。重新载入（F5）时位置已经由 start 选项定好，不再跳。
    /// </summary>
    private void ApplyFileState(Mpv m, bool resumePosition)
    {
        if (_currentPath == null) return;
        var st = _fileStates.Get(_currentPath);
        var notes = new List<string>();
        double pos = st?.Position ?? 0;
        if (S.RememberPosition && resumePosition && pos > 0 && (_currentDuration <= 0 || pos < _currentDuration - 10))
        {
            m.Command("seek", pos.ToString("0.###", Inv), "absolute+exact");
            notes.Add($"从上次的位置 {FormatTime(pos)} 继续（Home 键从头开始）");
        }
        if (S.RememberTracks) RestoreTracks(m, st);
        SnapshotTracks();

        // 延迟：选好音轨 / 字幕之后，按选中的那条套用
        _audioDelays = st?.AudioDelays != null ? new(st.AudioDelays) : new();
        _subDelays = st?.SubDelays != null ? new(st.SubDelays) : new();
        _audioKey = TrackKey("audio");
        _subKey = TrackKey("sub");
        if (S.RememberDelays)
        {
            // 旧版本按视频记的延迟：这个视频还没有按轨的记录时，算到现在选中的那条上
            if (_audioDelays.Count == 0 && st is { AudioDelay: not 0 } && _audioKey != null) _audioDelays[_audioKey] = st.AudioDelay;
            if (_subDelays.Count == 0 && st is { SubDelay: not 0 } && _subKey != null) _subDelays[_subKey] = st.SubDelay;
            double ad = _audioKey != null ? _audioDelays.GetValueOrDefault(_audioKey) : 0;
            double sd = _subKey != null ? _subDelays.GetValueOrDefault(_subKey) : 0;
            m.SetProperty("audio-delay", ad);
            m.SetProperty("sub-delay", sd);
            if (ad != 0) notes.Add($"音频延迟 {ad * 1000:+0;-0} ms（这条音轨记住的）");
            if (sd != 0) notes.Add($"字幕延迟 {sd * 1000:+0;-0} ms（这条字幕记住的）");
        }
        if (notes.Count > 0) m.ShowText(string.Join("\n", notes), 3000);
    }

    // ---- 按音轨 / 字幕记住的延迟 ----

    // 当前文件每条音轨 / 字幕的延迟（打开文件时从 file-states.json 读出，结束时写回），以及现在选中的是哪条
    private Dictionary<string, double> _audioDelays = new(), _subDelays = new();
    private string? _audioKey, _subKey;

    /// <summary>
    /// 选中的音轨 / 字幕的键：外挂文件按路径，内嵌轨按流序号 + 编码 + 语言（文件被换成别的版本时不会套错）。
    /// 没选（关闭字幕）返回 null。
    /// </summary>
    private string? TrackKey(string type)
    {
        var t = Tracks().FirstOrDefault(o => TrackStr(o, "type") == type && TrackFlag(o, "selected"));
        if (t == null) return null;
        if (TrackFlag(t, "external"))
            return "ext:" + FullPath(TrackStr(t, "external-filename")).ToUpperInvariant();
        return $"ff:{TrackFfIndex(t)}:{TrackStr(t, "codec")}:{TrackStr(t, "lang")}";
    }

    /// <summary>
    /// 换了音轨 / 字幕：先把刚才那条的延迟记下，再套用新那条记住的（没记过就是 0）。关闭字幕时不动延迟。
    /// </summary>
    private void SwapTrackDelay(bool audio)
    {
        var m = _mpv;
        if (m == null || _currentPath == null || _idle || !S.RememberDelays) return;
        if (Tracks().Count == 0) return; // 文件正在卸载
        string? key = TrackKey(audio ? "audio" : "sub");
        string? old = audio ? _audioKey : _subKey;
        if (key == old) return;
        string prop = audio ? "audio-delay" : "sub-delay";
        var dict = audio ? _audioDelays : _subDelays;
        double cur = m.GetDouble(prop) ?? 0;
        if (old != null) dict[old] = cur;
        if (audio) _audioKey = key; else _subKey = key;
        if (key == null) return;
        double next = dict.GetValueOrDefault(key);
        if (Math.Abs(next - cur) < 1e-6) return;
        m.SetProperty(prop, next);
        if (next != 0)
            m.ShowText($"{(audio ? "音频延迟" : "字幕延迟")} {next * 1000:+0;-0} ms（这条{(audio ? "音轨" : "字幕")}记住的）", 2500);
    }

    /// <summary>把当前选中的那条的延迟写进字典，只留非 0 的（结束时存盘用）。</summary>
    private Dictionary<string, double>? CollectDelays(bool audio)
    {
        var m = _mpv;
        var dict = audio ? _audioDelays : _subDelays;
        string? key = audio ? _audioKey : _subKey;
        if (m != null && key != null) dict[key] = m.GetDouble(audio ? "audio-delay" : "sub-delay") ?? 0;
        var kept = dict.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        return kept.Count > 0 ? kept : null;
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
            // 延迟按音轨 / 字幕记；旧的按视频记的值已经在打开时算到当时选中的那条上了，这里清掉
            AudioDelays = S.RememberDelays ? CollectDelays(audio: true) : old?.AudioDelays,
            SubDelays = S.RememberDelays ? CollectDelays(audio: false) : old?.SubDelays,
            AudioDelay = S.RememberDelays ? 0 : old?.AudioDelay ?? 0,
            SubDelay = S.RememberDelays ? 0 : old?.SubDelay ?? 0,
            Passthrough = old?.Passthrough,
            Hwdec = old?.Hwdec,
            VideoSync = old?.VideoSync,
            AntiClip = old?.AntiClip,
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
        UpdateHdrButton();
        UpdateFormatLabel();
        MaybeAutoHdr();
        MaybeMatchRefresh();
    }

    // =====================================================================
    //  自动匹配刷新率
    // =====================================================================

    // 每个文件第一次 video-reconfig 时检查一次；_refreshGdi / _refreshOriginal：改过刷新率的显示器和它原来的刷新率（退出时还原）
    private bool _refreshChecked;
    /// <summary>当前文件的刷新率和帧率匹配（这时固定按显示器重采样，右键菜单里的视频同步变灰）。</summary>
    private bool _refreshMatched;
    private string? _refreshGdi;
    private RefreshRate.Mode? _refreshOriginal;

    /// <summary>
    /// 开始播放时（不是打开播放器时）按视频帧率选刷新率，见 <see cref="RefreshRate"/>。切换到别的视频会再检查一次：
    /// 新视频没有匹配的刷新率，就恢复成原来的。同时决定视频同步方式：
    /// 刷新率和帧率匹配（恰好或足够近似的整数倍）时按显示器重采样，mpv 微调播放速度把那点差别补上，不重复 / 丢帧；
    /// 不匹配时用这个视频单独选的方式，没选过就用全局设置（按音频同步，或帧插值）。
    /// 关掉“自动匹配刷新率”时始终不改显示器刷新率，只看当前刷新率匹不匹配。音乐文件（只有封面）不动。
    /// </summary>
    private void MaybeMatchRefresh()
    {
        var m = _mpv;
        if (_refreshChecked || m == null || _idle) return;
        if (m.GetString("video-params/gamma") == null) return; // 还没解码出第一帧
        _refreshChecked = true;
        if (m.GetFlag("current-tracks/video/image") == true) { _refreshMatched = false; SetSyncMode("audio"); return; }

        string gdi = CurrentGdiName();
        // 播放器挪到了另一块屏幕上：先把之前改过的那块还原
        if (_refreshGdi != null && !string.Equals(_refreshGdi, gdi, StringComparison.OrdinalIgnoreCase)) RestoreRefresh(announce: false);
        double fps = m.GetDouble("container-fps") ?? 0;
        if (fps <= 0) fps = m.GetDouble("estimated-vf-fps") ?? 0;
        var query = RefreshRate.Query(gdi);
        if (query == null || fps <= 0) { ApplyVideoSync(false); return; }
        var (cur, all) = query.Value;
        bool currentMatches = RefreshRate.Pick(fps, new[] { cur }, cur) != null;
        if (!S.MatchRefreshRate) { ApplyVideoSync(currentMatches); return; }

        var original = _refreshGdi != null && _refreshOriginal is { } o ? all.FirstOrDefault(x => x.DevHz == o.DevHz, o) : cur;
        var pick = RefreshRate.Pick(fps, all, cur);
        var target = pick ?? original;
        if (target.DevHz == cur.DevHz)
        {
            if (pick == null && _refreshGdi != null) { _refreshGdi = null; _refreshOriginal = null; }
            ApplyVideoSync(pick != null);
            return;
        }

        // 显示器切换模式时会黑一下：正在播放就先暂停，免得错过开头（自动切 HDR 时已经暂停的话不重复）
        bool resume = m.GetFlag("pause") == false;
        if (resume) m.SetProperty("pause", true);
        if (!RefreshRate.Set(gdi, target.DevHz, out string err))
        {
            if (resume) m.SetProperty("pause", false);
            ApplyVideoSync(currentMatches);
            m.ShowText($"切换刷新率失败（{target.Text} Hz）：{err}", 3000);
            return;
        }
        if (target.DevHz == original.DevHz)
        {
            _refreshGdi = null;
            _refreshOriginal = null;
        }
        else if (_refreshGdi == null)
        {
            _refreshGdi = gdi;
            _refreshOriginal = cur;
        }
        SetDisplayFpsOverride(_refreshGdi != null ? target.Hz : 0);
        ApplyVideoSync(pick != null);

        string fpsText = fps.ToString("0.###", Inv);
        m.ShowText(pick != null
            ? $"刷新率 {cur.Text} → {target.Text} Hz（视频 {fpsText} 帧" +
              (Math.Abs(target.Hz / Math.Round(target.Hz / fps) - fps) / fps < RefreshRate.ExactTolerance ? "）" : "，近似）")
            : $"没有和 {fpsText} 帧匹配的刷新率，恢复为原来的 {target.Text} Hz", 3000);
        if (resume)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (_, _) => { t.Stop(); _mpv?.SetProperty("pause", false); };
            t.Start();
        }
    }

    /// <summary>
    /// matched：刷新率和帧率匹配，按显示器重采样（不插值）。否则用这个视频单独选的方式，没选过用全局设置。
    /// </summary>
    private void ApplyVideoSync(bool matched)
    {
        _refreshMatched = matched;
        SetSyncMode(matched ? "resample" : FileVideoSync() ?? S.NoMatchSync);
    }

    /// <summary>"resample" = 按显示器重采样；"interp" = 帧插值 + 按显示器重采样；其它 = 按音频同步。</summary>
    private void SetSyncMode(string mode)
    {
        var m = _mpv;
        if (m == null) return;
        m.SetProperty("video-sync", mode is "resample" or "interp" ? "display-resample" : "audio");
        m.SetProperty("interpolation", mode == "interp" ? "yes" : "no");
    }

    private static string SyncName(string value) =>
        SettingsWindow.NoMatchSyncChoices.FirstOrDefault(c => c.Value == value).Name ?? value;

    /// <summary>当前文件单独选的、刷新率匹配不上时的视频同步方式；null = 跟随全局设置。</summary>
    private string? FileVideoSync()
    {
        string? path = _currentPath ?? (_idle ? null : _mpv?.GetString("path"));
        return path == null ? null : _fileStates.Get(path)?.VideoSync;
    }

    private static string AntiClipName(string value) =>
        SettingsWindow.AntiClipChoices.FirstOrDefault(c => c.Value == value).Name ?? value;

    /// <summary>这个文件的防削波策略：单独选过的优先，否则用全局设置。</summary>
    private string EffectiveAntiClip(string? path) => (path == null ? null : _fileStates.Get(path)?.AntiClip) ?? S.AntiClip;

    /// <summary>当前文件单独选的防削波策略；null = 跟随全局设置。</summary>
    private string? FileAntiClip()
    {
        string? path = _currentPath ?? (_idle ? null : _mpv?.GetString("path"));
        return path == null ? null : _fileStates.Get(path)?.AntiClip;
    }

    /// <summary>右键菜单里给当前视频选防削波策略（null = 跟随全局设置），记住，立即生效。</summary>
    private void SetFileAntiClip(string? value)
    {
        var m = _mpv;
        string? path = _currentPath ?? (_idle ? null : m?.GetString("path"));
        if (m == null || path == null) return;
        var st = _fileStates.Get(path) ?? new FileState { LastPlayed = DateTime.UtcNow };
        st.AntiClip = value;
        _fileStates.Set(path, st);
        ApplyAudioChain(m.SetProperty, path);
        m.ShowText(value == null
            ? $"本视频的防削波：跟随全局设置（{AntiClipName(S.AntiClip)}）"
            : $"本视频的防削波：{AntiClipName(value)}");
    }

    /// <summary>
    /// 右键菜单的“音频直通”：直接开 / 关这个视频的直通，按文件记住。切换后和全局设置一样时就清掉单独的设置，
    /// 回到跟随全局设置（以后全局设置改了，它也跟着变）。
    /// </summary>
    private void ToggleFilePassthrough()
    {
        var m = _mpv;
        string? path = _currentPath ?? (_idle ? null : m?.GetString("path"));
        if (m == null || path == null) return;
        bool next = !EffectivePassthrough(path);
        var st = _fileStates.Get(path) ?? new FileState { LastPlayed = DateTime.UtcNow };
        st.Passthrough = next == S.AudioPassthrough ? null : next;
        _fileStates.Set(path, st);
        ApplyAudioChain(m.SetProperty, path);
        m.ShowText($"音频直通：{(next ? "开" : "关")}" +
                   (st.Passthrough == null ? "（和全局设置一致）" : "（本视频单独设置）"));
    }

    /// <summary>右键菜单里给当前视频选同步方式（null = 跟随全局设置），记住；刷新率匹配不上时立即生效。</summary>
    private void SetFileVideoSync(string? value)
    {
        var m = _mpv;
        string? path = _currentPath ?? (_idle ? null : m?.GetString("path"));
        if (m == null || path == null) return;
        var st = _fileStates.Get(path) ?? new FileState { LastPlayed = DateTime.UtcNow };
        st.VideoSync = value;
        _fileStates.Set(path, st);
        if (!_refreshMatched) ApplyVideoSync(false);
        m.ShowText(value == null
            ? $"本视频的视频同步：跟随全局设置（{SyncName(S.NoMatchSync)}）"
            : $"本视频的视频同步：{SyncName(value)}");
    }

    /// <summary>
    /// 刷新率改了以后直接告诉 mpv 新的精确值（0 = 让 mpv 自己检测）：嵌入的 mpv 窗口不一定收得到显示模式变化的通知，
    /// 按显示器重采样要用准确的刷新率。旧版 mpv 的选项名是 display-fps。
    /// </summary>
    private void SetDisplayFpsOverride(double hz)
    {
        var m = _mpv;
        if (m == null) return;
        string v = hz > 0 ? hz.ToString("0.######", Inv) : "0";
        if (m.SetProperty("display-fps-override", v) < 0) m.SetProperty("display-fps", v);
    }

    /// <summary>把改过的刷新率还原（退出、关掉设置时）。</summary>
    private void RestoreRefresh(bool announce)
    {
        if (_refreshGdi == null || _refreshOriginal is not { } o) return;
        RefreshRate.Restore(_refreshGdi, o.DevHz);
        _refreshGdi = null;
        _refreshOriginal = null;
        SetDisplayFpsOverride(0);
        if (announce) _mpv?.ShowText($"刷新率已还原为 {o.Text} Hz", 2500);
    }

    private void OnEndFile(MpvEndReason reason, int error, double position)
    {
        // 放完（EOF）就下次从头开始；切换文件 / 停止 / 重新载入则记下当时的位置
        CommitFileState(position, finished: reason == MpvEndReason.Eof);
        if (reason != MpvEndReason.Error) return;
        if (_reloading) { _reloading = false; _mpv?.SetProperty("start", "none"); }
        // 打开失败时没有 file-loaded：用 start-file 时记下的路径，播放列表里标出来，右键菜单里可以修复它
        string? failed = _startingPath;
        if (failed != null && IsLocalPath(failed) && !File.Exists(failed))
        {
            // 文件不在了：标上红色感叹号，不提示修复
            foreach (var e in _playlist) if (SamePath(e.Path, failed)) e.Missing = true;
            UpdatePlaylistFlags();
            _mpv?.ShowText("找不到此视频：" + Path.GetFileName(failed), 4000);
            return;
        }
        bool local = failed != null && File.Exists(failed);
        if (local)
        {
            _failedPaths.Add(FullPath(failed!));
            UpdatePlaylistFlags();
        }
        _mpv?.ShowText("播放失败：" + Mpv.ErrorString(error) +
            (local ? "\n可以在播放列表（P）里右键这个文件 → 修复视频，试试重新封装" : ""), 5000);
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
            // 找不到的文件（已删除 / 移动 / 所在的盘没接上）也留着，在列表里标出来；重复的只留一个
            if (!seen.Add(FullPath(f))) continue;
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
        // 菜单开着时点画面只是关菜单（VideoHost 已经不把只有松开的那一下当单击，这里再保险一下）
        if (_menuOpen || (now - _menuClosedAt).TotalMilliseconds < 400) return;
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
            case "passthrough": ToggleFilePassthrough(); break;
            case "anticlip":
                if (arg != null) SetFileAntiClip(arg == "global" ? null : arg);
                break;
            case "vsync":
                if (arg != null) SetFileVideoSync(arg == "global" ? null : arg);
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
            case "hdr":
                if (HdrBlockedReason() is { } why) m.ShowText(why);
                else SetHdr(!_hdrOn, manual: true);
                break;
            // 关闭字幕时调字幕延迟没有意义（也可能是烧在画面里的硬字幕）
            case "sub-delay-dec" or "sub-delay-inc" when m.GetString("sid") is "no" or null:
                m.ShowText("当前无字幕或为硬字幕");
                break;
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
            UpdateAllProgress();
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
            UpdateAllProgress();
            CheckMissingFiles();
        }
        _bar.BtnPrev.IsEnabled = _bar.BtnNext.IsEnabled = items.Count > 1;
    }

    /// <summary>按 _failedPaths 和缩略图的结果更新“打不开”的标记。</summary>
    private void UpdatePlaylistFlags()
    {
        foreach (var e in _playlist)
        {
            string key = FullPath(e.Path);
            // 找不到的文件不再提示“无法打开，可修复”，只显示红色感叹号
            e.Failed = !e.Missing && (_failedPaths.Contains(key) ||
                       (_thumbInfo.TryGetValue(key, out var info) && info.Kind == PlaylistThumbs.Kind.Failed));
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
        {
            if (!SamePath(e.Path, path)) continue;
            ApplyThumb(e, urgent);
            UpdateProgress(e);
        }
    }

    /// <summary>“上次进度/片长”：正在播放的显示“播放中”；进度和片长都不知道时不显示。</summary>
    private void UpdateProgress(PlaylistEntry e)
    {
        if (e.Current && !_idle) { e.Progress = "播放中"; return; }
        if (e.Path.Length == 0) { e.Progress = ""; return; }
        string key = FullPath(e.Path);
        double pos = ThumbPos(key);
        double dur = _durations.GetValueOrDefault(key);
        e.Progress = dur > 0 ? $"{Hms(Math.Min(pos, dur))}/{Hms(dur)}" : pos > 0 ? $"{Hms(pos)}/--:--:--" : "";
    }

    private void UpdateAllProgress()
    {
        foreach (var e in _playlist) UpdateProgress(e);
    }

    private static string Hms(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private void OnPlaylistThumb(string path, double pos, PlaylistThumbs.Kind kind, System.Windows.Media.Imaging.BitmapSource? bmp, double duration)
    {
        if (duration > 0)
        {
            _durations[path] = duration;
            foreach (var e in _playlist)
                if (SamePath(e.Path, path)) UpdateProgress(e);
        }
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

    /// <summary>
    /// 右键菜单：选中多项时只有“移除”；找不到的文件只有“移除”和“打开原先所在目录”；一项都没选（点在空白处）不弹。
    /// </summary>
    private void OnPlaylistMenuOpening(object sender, ContextMenuEventArgs e)
    {
        int n = PlaylistBox.SelectedItems.Count;
        if (n == 0) { e.Handled = true; return; }
        bool multi = n > 1;
        var entry = PlaylistBox.SelectedItem as PlaylistEntry;
        string? path = entry?.Path;
        // 右键时再确认一次（可能是刚刚才删掉 / 恢复的）
        if (!multi && entry != null && IsLocalPath(entry.Path)) entry.Missing = !File.Exists(entry.Path);
        bool missing = !multi && entry is { Missing: true };
        var normal = multi || missing ? Visibility.Collapsed : Visibility.Visible;
        PlMenuPlay.Visibility = PlMenuReveal.Visibility = PlMenuSep.Visibility = PlMenuRepair.Visibility = PlMenuInfo.Visibility = normal;
        PlMenuOpenDir.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
        PlMenuRemove.Header = multi ? $"移除选中的 {n} 项" : "移除";
        PlMenuRepair.Header = _repair.Running || _repairChecking ? "取消修复视频" : "修复视频（重新封装）…";
        PlMenuRepair.IsEnabled = _repair.Running || _repairChecking || (path != null && File.Exists(path));
    }

    private static bool IsLocalPath(string path) => path.Length > 0 && !path.Contains("://");

    private int _missingCheck;

    /// <summary>
    /// 检查播放列表里的本地文件还在不在（软件获得焦点、加载 / 改动播放列表时）。在后台线程查，
    /// 免得网络路径、睡眠的硬盘卡住界面；查完列表已经变了（又调用了一次）就丢掉结果。
    /// </summary>
    private async void CheckMissingFiles()
    {
        var paths = _playlist.Select(e => e.Path).Where(IsLocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0) return;
        int gen = ++_missingCheck;
        HashSet<string> missing;
        try
        {
            missing = await Task.Run(() => paths.Where(p => { try { return !File.Exists(p); } catch { return false; } })
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch { return; }
        if (gen != _missingCheck || _shuttingDown) return;
        foreach (var e in _playlist) e.Missing = IsLocalPath(e.Path) && missing.Contains(e.Path);
        UpdatePlaylistFlags();
    }

    /// <summary>
    /// 找不到的文件：打开它原来所在的文件夹。那个文件夹也没了就往上找到还在的那一级；整个盘都没接上就提示。
    /// </summary>
    private void OpenOriginalFolder(string path)
    {
        string? dir = null;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(path)); } catch { }
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(dir))
        {
            _mpv?.ShowText("原来所在的文件夹（和所在的盘）都找不到了", 3000);
            return;
        }
        // 盘符根目录 “D:\” 末尾的反斜杠会把后面的引号转义掉，写成 “D:\.”
        System.Diagnostics.Process.Start("explorer.exe", $"\"{(dir.EndsWith('\\') ? dir + "." : dir)}\"");
        string? wanted = Path.GetDirectoryName(path);
        if (!SamePath(dir, wanted)) _mpv?.ShowText($"原来的文件夹已经不在了，打开了还在的上一级：{dir}", 3000);
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
        // 音频直通 / 防削波：这个文件单独选过就用它的，否则用全局的。在音频链建立之前设好，不会断声
        ApplyAudioChain(m.SetProperty, path);
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

        // 原来那条字幕的延迟先记下（切换事件要等一会儿才到，那时延迟已经被下面清零了）
        if (_subKey != null) _subDelays[_subKey] = m.GetDouble("sub-delay") ?? 0;
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
        _subKey = TrackKey("sub");
        if (_subKey != null) _subDelays[_subKey] = 0;
        string scale = Math.Abs(res.Scale - 1) > 0.0005 ? $"，帧率校正 ×{res.Scale.ToString("0.0000", Inv)}" : "";
        m.ShowText($"字幕已同步：整体偏移 {res.Offset.ToString("+0.00;-0.00;0.00", Inv)} 秒{scale}\n已保存为 {Path.GetFileName(output)}", 5000);
    }

    // =====================================================================
    //  修复视频（ffmpeg 重新封装）
    // =====================================================================

    /// <summary>
    /// 画面右键菜单里“修复视频”针对的文件：正在播放的本地文件。打不开的文件（可能不止一个）
    /// 在播放列表里右键修复。
    /// </summary>
    private string? RepairTarget()
    {
        string? path = _idle ? null : _mpv?.GetString("path");
        return path != null && File.Exists(path) ? FullPath(path) : null;
    }

    /// <summary>
    /// 不重新编码地重新封装成“原名.repaired.mkv”（原文件不动），完成后在播放列表里替换掉原文件；
    /// 原文件正在播放（或者它打不开、现在也没在播放别的）时直接播放修复好的，接着原来的进度、延迟和音轨 / 字幕。
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
        if (_repairChecking) return;

        // 先快速检查一下是不是真的需要修复：看起来正常就先提示一下，用户仍然要修再继续
        _repairChecking = true;
        VideoRepair.Check check;
        m.ShowText($"正在检查 {Path.GetFileName(source)} 是否需要修复…\n（右键 → 取消修复视频）", 60000);
        try { check = await _repair.CheckAsync(source); }
        finally { _repairChecking = false; }
        m.ShowText("", 1);
        if (_shuttingDown || _mpv == null || check.Cancelled) return;
        if (!check.Readable)
        {
            await Dialogs.ShowAsync(this, "修复视频", check.Error);
            return;
        }
        var issues = check.Issues;
        // 正在播放的文件：mpv 自己也说跳不了，那就是缺索引
        if (!_idle && SamePath(m.GetString("path"), source) && m.GetFlag("seekable") == false)
            issues.Insert(0, "播放器无法在这个文件里跳转（拖不动进度条）");
        if (issues.Count == 0)
        {
            bool still = await Dialogs.ConfirmAsync(this, "这个文件看起来是正常的",
                $"检查了 {Path.GetFileName(source)}：文件头、时长、格式、开头 30 秒的时间戳和数据包、跳到结尾附近，都没有发现问题。\n\n" +
                "对正常的文件，修复不会带来任何改善，只会多出一份内容一样的 MKV。\n" +
                "这个检查只看了文件头、开头和结尾附近；如果播放时确实有问题（比如中间某段卡住、音画不同步），仍然可以修复试试。",
                "仍然修复");
            if (!still || _shuttingDown || _mpv == null) return;
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
        string found = issues.Count == 0 ? "" : "检查发现：\n" + string.Join("\n", issues.Select(i => "· " + i)) + "\n\n";
        bool go = await Dialogs.ConfirmAsync(this, "修复视频",
            found + $"把 {Path.GetFileName(source)} 重新封装成 MKV（不重新编码，画质不变），另存为：\n{output}\n\n" +
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
        bool wasFailed = _idle && _failedPaths.Contains(source);
        // 正在播放：先把当前的进度记下来（之后它结束时就不会再记一次）
        if (isCurrent) CommitFileState(_timePos, finished: false);
        if (_fileStates.Get(source) is { } st)
        {
            _fileStates.Set(output, new FileState
            {
                Position = st.Position, AudioDelay = st.AudioDelay, SubDelay = st.SubDelay, Hwdec = st.Hwdec,
                AudioDelays = st.AudioDelays, SubDelays = st.SubDelays, Passthrough = st.Passthrough,
                VideoSync = st.VideoSync, AntiClip = st.AntiClip, Audio = st.Audio, Sub = st.Sub, LastPlayed = DateTime.UtcNow,
            });
        }
        _failedPaths.Remove(source);

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
    /// Checked 为 true 的项是当前选中的，左边显示蓝色竖条（和全局设置里下拉菜单的选中标记一样），不占位置，文字不缩进。
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
        bool local = path != null && File.Exists(path);

        // ---- 文件 ----
        var items = new List<MenuEntry>
        {
            new("打开文件…", Hp("open"), "Ctrl+O"),
            new("重新载入当前文件", Hp("reload"), "F5", Enabled: playing),
            // 修复视频：只针对正在播放的文件；打不开的文件在播放列表里右键修复
            _repair.Running || _repairChecking
                ? new MenuEntry("取消修复视频", Hp("repair-cancel"))
                : new MenuEntry("修复视频（重新封装）…", Hp("repair"), Enabled: RepairTarget() != null),
            new("视频参数…", Hp("info"), "I"),
            new("在资源管理器中显示", Hp("reveal"), Enabled: local),
            Sep,
        };

        // ---- 音轨 / 字幕：子菜单里是 载入外部文件 / 可选的轨 / 延迟 ----
        var audioTracks = new List<MenuEntry>();
        var subTracks = new List<MenuEntry> { new("关闭字幕/硬字幕", Hp("sid", "no"), Checked: playing && m?.GetString("sid") is "no" or null) };
        if (playing && m?.GetNode("track-list") is List<object?> tracks)
        {
            foreach (var t in tracks.OfType<Dictionary<string, object?>>())
            {
                string type = t.GetValueOrDefault("type") as string ?? "";
                if (type != "audio" && type != "sub") continue;
                long id = t.GetValueOrDefault("id") is long l ? l : 0;
                var entry = new MenuEntry(TrackText(t), Hp(type == "audio" ? "aid" : "sid", id.ToString(Inv)),
                    Checked: t.GetValueOrDefault("selected") is true);
                (type == "audio" ? audioTracks : subTracks).Add(entry);
            }
        }
        // 音频 / 字幕延迟按文件记住（见 file-states.json），所以放在这里而不是全局设置里
        int step = S.SubDelayStepMs;
        double ad = (m?.GetDouble("audio-delay") ?? 0) * 1000, sd = (m?.GetDouble("sub-delay") ?? 0) * 1000;

        var audio = new List<MenuEntry> { new("载入外部音轨…", Hp("load-audio")), Sep };
        if (audioTracks.Count > 0) { audio.AddRange(audioTracks); audio.Add(Sep); }
        audio.Add(new MenuEntry("音频延迟", Sub: new()
        {
            new($"当前：{ad:+0;-0;0} ms", Enabled: false),
            new($"提前 {step} ms", Hp("audio-delay-dec")),
            new($"延后 {step} ms", Hp("audio-delay-inc")),
            new("重置", Hp("audio-delay-reset")),
        }));
        items.Add(new MenuEntry("音轨", Sub: audio, Enabled: playing));

        var subs = new List<MenuEntry> { new("载入外部字幕…", Hp("load-sub")), Sep };
        subs.AddRange(subTracks);
        subs.Add(Sep);
        // 关闭字幕时，字幕延迟没有意义，灰掉
        bool subOff = !playing || m?.GetString("sid") is "no" or null;
        subs.Add(new MenuEntry("字幕延迟", Enabled: !subOff, Sub: new()
        {
            new($"当前：{sd:+0;-0;0} ms", Enabled: false),
            new($"提前 {step} ms", Hp("sub-delay-dec"), "Z"),
            new($"延后 {step} ms", Hp("sub-delay-inc"), "X"),
            new("重置", Hp("sub-delay-reset")),
            Sep,
            _subSync.Running
                ? new MenuEntry("取消自动同步", Hp("sub-sync"))
                : new MenuEntry("自动同步字幕（ffsubsync）…", Hp("sub-sync"), Enabled: local),
        }));
        items.Add(new MenuEntry("字幕", Sub: subs, Enabled: playing));
        items.Add(Sep);

        // ---- 画面 / 声音的处理方式（后三项按文件记住，优先于全局设置）----
        // SDR 视频、纯音频时 HDR 输出没有意义，灰掉（快捷键 H、控制栏按钮也一样）
        bool hdrAllowed = HdrBlockedReason() == null;
        items.Add(new MenuEntry("HDR 输出", Hp("hdr"), "H", Checked: _hdrOn && hdrAllowed, Enabled: hdrAllowed));
        string? fileHwdec = FileHwdec();
        var hwdec = new List<MenuEntry>
        {
            new($"跟随全局设置（{HwdecName(S.Hwdec)}）", Hp("hwdec", "global"), Checked: playing && fileHwdec == null),
            Sep,
        };
        hwdec.AddRange(SettingsWindow.HwdecChoices.Select(c => new MenuEntry(c.Name, Hp("hwdec", c.Value), Checked: fileHwdec == c.Value)));
        items.Add(new MenuEntry("解码方式", Sub: hwdec, Enabled: playing));
        // 视频同步：刷新率和帧率匹配时固定按显示器重采样，灰掉；匹配不上时可以给这个视频单独选
        if (_refreshMatched && playing)
        {
            items.Add(new MenuEntry("视频同步（刷新率已匹配）", Enabled: false));
        }
        else
        {
            // 跟随全局设置：全局设置改了，这个视频也跟着改；单独选过的按文件记住，不受全局设置影响
            string? fileSync = FileVideoSync();
            var sync = new List<MenuEntry>
            {
                new($"跟随全局设置（{SyncName(S.NoMatchSync)}）", Hp("vsync", "global"), Checked: playing && fileSync == null),
                Sep,
            };
            sync.AddRange(SettingsWindow.NoMatchSyncChoices
                .Select(c => new MenuEntry(c.Name, Hp("vsync", c.Value), Checked: playing && fileSync == c.Value)));
            items.Add(new MenuEntry("视频同步", Sub: sync, Enabled: playing && _refreshChecked));
        }
        string? fileAntiClip = FileAntiClip();
        var antiClip = new List<MenuEntry>
        {
            new($"跟随全局设置（{AntiClipName(S.AntiClip)}）", Hp("anticlip", "global"), Checked: playing && fileAntiClip == null),
            Sep,
        };
        antiClip.AddRange(SettingsWindow.AntiClipChoices
            .Select(c => new MenuEntry(c.Name, Hp("anticlip", c.Value), Checked: playing && fileAntiClip == c.Value)));
        // 音频直通：直接点击开关（不展开），默认跟随全局设置，按视频记住；开着时防削波不起作用，灰掉
        bool passthrough = playing && EffectivePassthrough(path);
        items.Add(new MenuEntry("音频直通", Hp("passthrough"), Checked: passthrough, Enabled: playing));
        items.Add(new MenuEntry("防削波策略", Sub: antiClip, Enabled: playing && !passthrough));
        items.Add(Sep);

        // ---- 窗口 ----
        items.Add(new MenuEntry("播放列表", Hp("playlist"), "P", Checked: PlaylistPanel.Visibility == Visibility.Visible, Enabled: !_fullscreen));
        items.Add(new MenuEntry("全屏", Hp("fullscreen"), "F / 双击", Checked: _fullscreen));
        items.Add(new MenuEntry("全局设置…", Hp("settings")));
        items.Add(Sep);
        items.Add(new MenuEntry("退出软件", Hp("quit")));
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
            menu.Closed += (_, _) => { _menuOpen = false; _menuClosedAt = DateTime.UtcNow; };
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

    private List<object> ToWpfMenu(List<MenuEntry> items) => items.Select(ToWpfMenuItem).ToList();

    /// <summary>
    /// WPF-UI 菜单项模板（SubmenuItemTemplate）：高亮的圆角框里，标题从左边 8 px 开始。
    /// 选中标记照着全局设置里下拉菜单的样子画（WPF-UI ComboBoxItem 的 ActiveRectangle：3 × 16、圆角 2、
    /// 颜色 ComboBoxItemPillFillBrush），贴在高亮框的最左边，所以往左挪 8 px。
    /// </summary>
    private const double MenuHeaderInset = 8;

    private object ToWpfMenuItem(MenuEntry e)
    {
        if (e.IsSeparator) return (object)new Separator();
        var mi = new MenuItem { InputGestureText = e.Shortcut ?? "", IsEnabled = e.Enabled };
        if (e.Checked == true)
        {
            // 标题换成“文字 + 一个不占位置的竖条”：竖条放在宽度为 0 的 Canvas 里、往左画出去，
            // 选中项的文字和其它项对齐（以前的对勾会让整级菜单都缩进）
            var pill = new System.Windows.Shapes.Rectangle { Width = 3, Height = 16, RadiusX = 2, RadiusY = 2 };
            pill.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ComboBoxItemPillFillBrush");
            Canvas.SetLeft(pill, -MenuHeaderInset);
            Canvas.SetTop(pill, -8);
            var canvas = new Canvas { Width = 0, Height = 0, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            canvas.Children.Add(pill);
            var header = new Grid();
            header.Children.Add(new System.Windows.Controls.TextBlock { Text = e.Title });
            header.Children.Add(canvas);
            mi.Header = header;
        }
        else
        {
            // 字符串标题里 MenuItem 会把 “_” 当作访问键，转义一下
            mi.Header = e.Title.Replace("_", "__");
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
        bool refreshToggled = copy.MatchRefreshRate != S.MatchRefreshRate;
        if (copy.TargetPeakNits != S.TargetPeakNits) _displayPeak = 0;
        CopySettings(copy, S);
        S.Save();
        ApplyPlaybackOptions(m, initial: false);
        if (dvChanged) m.SetProperty("vf", DolbyVisionFilter());
        if (refreshToggled)
        {
            // 关掉：马上还原刷新率；之后（或者打开时）对当前视频重新判断一次刷新率和同步方式
            if (!S.MatchRefreshRate) RestoreRefresh(announce: true);
            _refreshChecked = false;
            MaybeMatchRefresh();
        }
        // 全局的“无匹配时的视频同步”改了：当前视频匹配不上刷新率、又跟随全局设置的，马上用新的
        else if (!_idle && _refreshChecked && !_refreshMatched) ApplyVideoSync(false);
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
                Line("视频同步", _refreshMatched ? "按显示器刷新率重采样（刷新率和帧率匹配）"
                    : P("video-sync") == "display-resample" && m.GetFlag("interpolation") == true ? "帧插值（刷新率和帧率不匹配）"
                    : P("video-sync") == "audio" ? "按音频同步（刷新率和帧率不匹配）"
                    : P("video-sync"));
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
                    bool pt = EffectivePassthrough(_currentPath);
                    string outFmt = P("audio-out-params/format");
                    Line("音频直通", !pt ? "关"
                        : outFmt.StartsWith("spdif", StringComparison.OrdinalIgnoreCase) ? $"开（{outFmt}，由功放 / 电视解码）"
                        : "开（这条音轨的格式或设备不支持，已改为正常解码）");
                    Line("防削波", pt ? "不起作用（音频直通）"
                        : EffectiveAntiClip(_currentPath) == "dynaudnorm" ? "拉平音量（dynaudnorm）+ 限幅" : "仅限幅（alimiter）");
                }
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
                        try { RestoreRefresh(announce: false); } catch { }
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
