using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace HdrPlayer;

/// <summary>
/// 播放列表的缩略图：上次退出 / 切走时那个位置的画面（没看过的文件取片长 10% 处）。
/// 用一个单独的、不出画面的 mpv 实例在后台线程上逐个生成，存成 JPEG 放在 %LOCALAPPDATA%\HdrPlayer\thumbs，
/// 下次启动直接读。文件名里带着路径 + 大小 + 修改时间的哈希和截图位置，文件被替换或位置变了就重新生成。
/// </summary>
internal sealed class PlaylistThumbs : IDisposable
{
    public enum Kind
    {
        /// <summary>拿到了画面。</summary>
        Image,
        /// <summary>纯音频 / 只有封面，没有画面。</summary>
        NoVideo,
        /// <summary>mpv 打不开这个文件（格式无法识别等）。</summary>
        Failed,
        /// <summary>没生成（文件不存在、超时、杜比视界 P5 的颜色不对等），显示占位图。</summary>
        None,
    }

    /// <summary>(路径, 请求的位置, 结果, 图像) —— 在后台线程触发，图像已 Freeze。</summary>
    public event Action<string, double, Kind, BitmapSource?>? Ready;

    /// <summary>缩略图的最大像素尺寸：播放列表里显示成 112 × 63（DIP），高 DPI 屏上也够清晰。</summary>
    public const int MaxW = 256, MaxH = 144;
    private const int DiskLimit = 1000;

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HdrPlayer", "thumbs");

    private readonly object _lock = new();
    private readonly LinkedList<(string Path, double Pos)> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _loaded = new(false), _restart = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _worker;
    private volatile bool _stop, _loadFailed;
    private Mpv? _mpv;
    private bool _cleaned;

    public PlaylistThumbs()
    {
        _worker = new Thread(Run) { IsBackground = true, Name = "playlist-thumbs", Priority = ThreadPriority.Lowest };
        _worker.Start();
    }

    /// <summary>
    /// 请求 path 在 pos 秒处的缩略图（pos &lt;= 0：没有记住的位置，取片长 10% 处）。
    /// 同一个文件只保留最新的请求；urgent 的排到最前面（刚切走的文件）。
    /// </summary>
    public void Request(string path, double pos, bool urgent = false)
    {
        lock (_lock)
        {
            for (var n = _queue.First; n != null; n = n.Next)
                if (string.Equals(n.Value.Path, path, StringComparison.OrdinalIgnoreCase)) { _queue.Remove(n); break; }
            if (urgent) _queue.AddFirst((path, pos));
            else _queue.AddLast((path, pos));
        }
        _wake.Set();
    }

    private void Run()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            while (!_stop)
            {
                (string Path, double Pos) job;
                lock (_lock)
                {
                    if (_queue.First == null) break;
                    job = _queue.First.Value;
                    _queue.RemoveFirst();
                }
                Kind kind = Kind.None;
                BitmapSource? bmp = null;
                try { (kind, bmp) = Make(job.Path, job.Pos); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("playlist-thumbs: " + ex); }
                if (_stop) return;
                Ready?.Invoke(job.Path, job.Pos, kind, bmp);
            }
            // 做完一批就关掉文件，免得占着文件句柄（资源管理器里删不掉 / 改不了名）
            try { _mpv?.Command("stop"); } catch { }
        }
    }

    private (Kind, BitmapSource?) Make(string path, double pos)
    {
        if (!File.Exists(path)) return (Kind.None, null);
        Directory.CreateDirectory(Dir);
        if (!_cleaned) { _cleaned = true; Cleanup(); }

        var fi = new FileInfo(path);
        string key = Hash($"{Path.GetFullPath(path).ToUpperInvariant()}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
        string tag = pos > 0 ? Math.Round(pos * 1000).ToString(CultureInfo.InvariantCulture) : "d";
        string jpg = Path.Combine(Dir, $"{key}_{tag}.jpg");
        string noVideo = Path.Combine(Dir, $"{key}.novideo");

        if (File.Exists(noVideo)) return (Kind.NoVideo, null);
        if (File.Exists(jpg))
        {
            var cached = LoadJpeg(jpg);
            if (cached != null)
            {
                try { File.SetLastWriteTimeUtc(jpg, DateTime.UtcNow); } catch { }
                return (Kind.Image, cached);
            }
        }
        // 同一个文件之前别的位置的截图：作废
        foreach (var old in Directory.EnumerateFiles(Dir, key + "_*.jpg"))
            try { File.Delete(old); } catch { }

        var m = EnsureMpv();
        _loadFailed = false;
        _loaded.Reset();
        _restart.Reset();
        m.Command("loadfile", path, "replace");
        if (!_loaded.Wait(10000, _cts.Token)) return (Kind.None, null);
        if (_loadFailed) return (Kind.Failed, null);
        _restart.Wait(5000, _cts.Token);

        bool hasVideo = !string.IsNullOrEmpty(m.GetString("video-codec")) && m.GetFlag("current-tracks/video/image") != true;
        if (!hasVideo)
        {
            try { File.WriteAllBytes(noVideo, Array.Empty<byte>()); } catch { }
            return (Kind.NoVideo, null);
        }
        // 杜比视界 P5 的画面要靠 RPU 重塑才有正确颜色，软件截图不做这一步，会偏绿 / 偏紫
        if (m.GetString("current-tracks/video/dolby-vision-profile") == "5") return (Kind.None, null);

        double duration = m.GetDouble("duration") ?? 0;
        double at = pos > 0 ? pos : duration * 0.1;
        if (duration > 0) at = Math.Min(at, Math.Max(0, duration - 1));
        if (at > 0.5)
        {
            _restart.Reset();
            m.Command("seek", at.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
            _restart.Wait(5000, _cts.Token);
        }
        var bmp = Thumbnailer.Grab(m);
        if (bmp == null) return (Kind.None, null);
        SaveJpeg(bmp, jpg);
        return (Kind.Image, bmp);
    }

    private Mpv EnsureMpv()
    {
        if (_mpv != null) return _mpv;
        var m = Thumbnailer.CreateHeadless(MaxW, MaxH);
        m.SetOption("vd-lavc-threads", "2");
        m.FileLoaded += () => _loaded.Set();
        m.PlaybackRestart += () => _restart.Set();
        m.EndFile += (reason, _) =>
        {
            if (reason != MpvEndReason.Error) return;
            _loadFailed = true;
            _loaded.Set();
        };
        m.Initialize();
        return _mpv = m;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)), 0, 10).ToLowerInvariant();

    private static BitmapSource? LoadJpeg(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = fs;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveJpeg(BitmapSource bmp, string file)
    {
        try
        {
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string tmp = file + ".tmp";
            using (var fs = File.Create(tmp)) enc.Save(fs);
            File.Move(tmp, file, overwrite: true);
        }
        catch { }
    }

    /// <summary>缓存太多时删掉最久没用过的。</summary>
    private static void Cleanup()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles();
            if (files.Length <= DiskLimit) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - DiskLimit))
                try { f.Delete(); } catch { }
        }
        catch { }
    }

    /// <summary>不要在 UI 线程上调用。</summary>
    public void Dispose()
    {
        _stop = true;
        _cts.Cancel();
        _wake.Set();
        if (!_worker.Join(5000)) return;
        try { _mpv?.Dispose(); } catch { }
        _mpv = null;
    }
}
