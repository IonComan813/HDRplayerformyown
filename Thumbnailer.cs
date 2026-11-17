using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HdrPlayer;

/// <summary>
/// 进度条悬停预览：用第二个不出画面的 mpv 实例（vo=null）打开同一个文件，跳到指定时间，
/// 用 screenshot-raw 取出缩小后的画面。所有 mpv 调用都在一个后台线程上进行，不影响正在播放的实例。
/// HDR 片源由 mpv 的软件转换（zimg）转成 sRGB，高光会被裁切，但作为缩略图足够。
/// </summary>
internal sealed class Thumbnailer : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>缩略图的最大像素尺寸（mpv 里先缩小再截图，转换开销很小）。</summary>
    public const int MaxW = 384, MaxH = 216;
    private const int CacheLimit = 300;

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _loaded = new(false), _restart = new(false);
    private readonly Thread _worker;
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _stop;

    // 请求（UI 线程写，后台线程读）
    private string? _file;
    private double? _pending;

    // 只在后台线程访问
    private Mpv? _mpv;
    private string? _loadedFile;
    private bool _usable;
    /// <summary>缩略图按整秒取：悬停位置四舍五入到最近的一秒，精确跳到那一帧。</summary>
    private const double ThumbStep = 1;
    private readonly Dictionary<int, BitmapSource> _cache = new();
    private readonly Queue<int> _cacheOrder = new();

    /// <summary>(请求的时间, 实际截图的时间, 图像) —— 在后台线程触发，图像已 Freeze。</summary>
    public event Action<double, double, BitmapSource>? ThumbnailReady;

    public Thumbnailer()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "thumbnailer", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>切换文件（null = 停用）。真正打开会推迟到第一次请求缩略图时。</summary>
    public void SetFile(string? path)
    {
        lock (_lock) { _file = path; _pending = null; }
        _wake.Set();
    }

    /// <summary>请求某个时间点的缩略图；只处理最新的请求，过时的直接丢弃。</summary>
    public void Request(double seconds)
    {
        lock (_lock) { if (_file == null) return; _pending = seconds; }
        _wake.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop) break;
            try { Step(); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("thumbnailer: " + ex); }
        }
    }

    private void Step()
    {
        string? file;
        double? t;
        lock (_lock) { file = _file; t = _pending; _pending = null; }

        if (file == null)
        {
            if (_loadedFile != null) { _mpv?.Command("stop"); _loadedFile = null; ClearCache(); }
            return;
        }
        if (t == null) return;

        if (!string.Equals(file, _loadedFile, StringComparison.OrdinalIgnoreCase)) Load(file);
        if (!_usable) return;

        int key = (int)Math.Round(t.Value / ThumbStep);
        double at = key * ThumbStep;
        if (_cache.TryGetValue(key, out var cached))
        {
            ThumbnailReady?.Invoke(t.Value, at, cached);
            return;
        }

        var m = _mpv!;
        _restart.Reset();
        // 精确跳转：从前一个关键帧解码到目标帧。只按关键帧跳会差好几秒（电影的关键帧间隔通常 1~5 秒）
        m.Command("seek", at.ToString("0.###", Inv), "absolute+exact");
        if (!_restart.Wait(3000, _cts.Token)) return;

        var bmp = Grab(m);
        if (bmp == null) return;
        _cache[key] = bmp;
        _cacheOrder.Enqueue(key);
        while (_cacheOrder.Count > CacheLimit) _cache.Remove(_cacheOrder.Dequeue());
        ThumbnailReady?.Invoke(t.Value, at, bmp);
    }

    private void Load(string file)
    {
        var m = EnsureMpv();
        ClearCache();
        _loadedFile = file;
        _usable = false;
        _loaded.Reset();
        _restart.Reset();
        m.Command("loadfile", file, "replace");
        if (!_loaded.Wait(8000, _cts.Token)) return;
        // 等打开后的第一帧出来，免得它的 playback-restart 被当成后面 seek 的结果
        _restart.Wait(3000, _cts.Token);

        // 纯音频 / 音乐封面没有可预览的画面
        bool hasVideo = !string.IsNullOrEmpty(m.GetString("video-codec")) && m.GetFlag("current-tracks/video/image") != true;
        double duration = m.GetDouble("duration") ?? 0;
        if (!hasVideo || duration <= 0) return;
        _usable = true;
    }

    private Mpv EnsureMpv()
    {
        if (_mpv != null) return _mpv;
        var m = new Mpv();
        m.SetOption("vo", "null");
        m.SetOption("ao", "null");
        m.SetOption("aid", "no");
        m.SetOption("sid", "no");
        m.SetOption("audio-display", "no");
        m.SetOption("idle", "yes");
        m.SetOption("pause", "yes");
        m.SetOption("keep-open", "always");
        m.SetOption("force-window", "no");
        m.SetOption("terminal", "no");
        m.SetOption("osd-level", "0");
        m.SetOption("load-scripts", "no");
        m.SetOption("ytdl", "no");
        m.SetOption("input-default-bindings", "no");
        m.SetOption("cache", "no");
        m.SetOption("hr-seek-framedrop", "yes");
        // 硬解后拷回内存（截图要读像素）；显卡不支持的格式会自动改用软解
        m.SetOption("hwdec", "auto-copy");
        m.SetOption("vd-lavc-threads", "4");
        // 只对非参考帧跳过环路滤波：精确跳转要连续解码一串帧，参考帧质量不能降，否则误差会累积成马赛克
        m.SetOption("vd-lavc-skiploopfilter", "nonref");
        m.SetOption("vd-lavc-fast", "yes");
        // 先缩小再截图：保持宽高比，最大 384×216
        m.SetOption("vf", $"lavfi=[scale={MaxW}:{MaxH}:force_original_aspect_ratio=decrease:force_divisible_by=2]");
        m.FileLoaded += () => _loaded.Set();
        m.PlaybackRestart += () => _restart.Set();
        m.Initialize();
        return _mpv = m;
    }

    private static BitmapSource? Grab(Mpv m)
    {
        // "video"：只要视频画面，不含字幕和 OSD；默认格式 bgr0 = WPF 的 Bgr32
        if (m.CommandWithResult("screenshot-raw", "video") is not Dictionary<string, object?> r) return null;
        if (r.GetValueOrDefault("w") is not long w || r.GetValueOrDefault("h") is not long h ||
            r.GetValueOrDefault("stride") is not long stride || r.GetValueOrDefault("data") is not byte[] data)
            return null;
        if (w <= 0 || h <= 0 || data.Length < stride * h) return null;
        var bmp = BitmapSource.Create((int)w, (int)h, 96, 96, PixelFormats.Bgr32, null, data, (int)stride);
        bmp.Freeze();
        return bmp;
    }

    private void ClearCache()
    {
        _cache.Clear();
        _cacheOrder.Clear();
    }

    /// <summary>不要在 UI 线程上调用（mpv 销毁可能稍慢）。</summary>
    public void Dispose()
    {
        _stop = true;
        _cts.Cancel();
        _wake.Set();
        // 等后台线程退出后再销毁 mpv，避免和正在进行的调用冲突（mpv 调用本身不会长时间阻塞）
        if (!_worker.Join(5000)) return;
        try { _mpv?.Dispose(); } catch { }
        _mpv = null;
    }
}
