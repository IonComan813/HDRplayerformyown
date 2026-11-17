using System.IO;
using System.Text.Json;

namespace HdrPlayer;

/// <summary>
/// 上次的播放列表，保存在 %APPDATA%\HdrPlayer\playlist.json（和设置文件放在一起）。
/// 安装、覆盖安装、卸载都不会动这个目录，所以升级后也能恢复。
/// </summary>
internal sealed class SavedPlaylist
{
    public List<string> Files { get; set; } = new();
    /// <summary>退出时正在播放（或上次恢复后还没继续播放）的那一项，-1 = 没有。</summary>
    public int Current { get; set; } = -1;
    /// <summary>那一项的播放位置（秒）。</summary>
    public double Position { get; set; }

    private static string FilePath => Path.Combine(AppSettings.Dir, "playlist.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static SavedPlaylist? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SavedPlaylist>(File.ReadAllText(FilePath));
        }
        catch { /* 文件损坏就当没有 */ }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}
