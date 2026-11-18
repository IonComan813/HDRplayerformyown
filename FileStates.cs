using System.IO;
using System.Text.Json;

namespace HdrPlayer;

/// <summary>
/// 记住的一条音轨 / 字幕轨。内嵌轨按流序号（ff-index）认，同时核对编码和语言，防止文件被替换后选错；
/// 外挂文件按路径认。Off = 字幕被关掉了。
/// </summary>
internal sealed class TrackRef
{
    public bool Off { get; set; }
    public string? External { get; set; }
    public long? FfIndex { get; set; }
    public string? Codec { get; set; }
    public string? Lang { get; set; }
    public string? Title { get; set; }
}

/// <summary>每个文件记住的状态：上次的播放进度、音频延迟、字幕延迟、解码方式、选中的音轨和字幕。</summary>
internal sealed class FileState
{
    /// <summary>上次的播放位置（秒），0 = 从头开始。</summary>
    public double Position { get; set; }
    /// <summary>音频延迟（秒，mpv 的 audio-delay）。</summary>
    public double AudioDelay { get; set; }
    /// <summary>字幕延迟（秒，mpv 的 sub-delay）。</summary>
    public double SubDelay { get; set; }
    /// <summary>只对这个文件生效的解码方式（mpv 的 hwdec 值），null = 跟随全局设置。</summary>
    public string? Hwdec { get; set; }
    /// <summary>选中的音轨，null = 没记录（按首选语言规则选）。</summary>
    public TrackRef? Audio { get; set; }
    /// <summary>选中的字幕（或关闭字幕），null = 没记录。</summary>
    public TrackRef? Sub { get; set; }
    /// <summary>最后一次播放的时间，记录太多时先淘汰最久没播的。</summary>
    public DateTime LastPlayed { get; set; }

    public bool IsEmpty => Position <= 0 && AudioDelay == 0 && SubDelay == 0 && Hwdec == null && Audio == null && Sub == null;
}

/// <summary>
/// 按文件路径保存 <see cref="FileState"/>，存在 %APPDATA%\HdrPlayer\file-states.json（和设置文件放在一起，升级 / 重装不受影响）。
/// 主要在 UI 线程上使用；mpv 的 on_load 钩子会在事件线程上读，所以加了锁。
/// </summary>
internal sealed class FileStateStore
{
    private const int Limit = 3000;
    private static string FilePath => Path.Combine(AppSettings.Dir, "file-states.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly Dictionary<string, FileState> _map;

    private FileStateStore(Dictionary<string, FileState> map) =>
        _map = new Dictionary<string, FileState>(map, StringComparer.OrdinalIgnoreCase);

    public static FileStateStore Load()
    {
        try
        {
            if (File.Exists(FilePath) &&
                JsonSerializer.Deserialize<Dictionary<string, FileState>>(File.ReadAllText(FilePath)) is { } map)
                return new FileStateStore(map);
        }
        catch { /* 文件损坏就当没有记录 */ }
        return new FileStateStore(new Dictionary<string, FileState>());
    }

    /// <summary>本地文件用完整路径作键（Windows 路径不分大小写）；网络地址原样使用。</summary>
    private static string Key(string path)
    {
        if (path.Contains("://")) return path;
        try { return Path.GetFullPath(path); } catch { return path; }
    }

    public FileState? Get(string path)
    {
        string key = Key(path);
        lock (_map) return _map.GetValueOrDefault(key);
    }

    public void Set(string path, FileState state)
    {
        string key = Key(path);
        lock (_map)
        {
            if (state.IsEmpty) _map.Remove(key);
            else _map[key] = state;
            if (_map.Count > Limit)
                foreach (var old in _map.OrderBy(kv => kv.Value.LastPlayed).Take(_map.Count - Limit).Select(kv => kv.Key).ToList())
                    _map.Remove(old);
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_map, JsonOpts));
        }
        catch { }
    }
}
