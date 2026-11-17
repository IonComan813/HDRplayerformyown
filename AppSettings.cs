using System.IO;
using System.Text.Json;

namespace HdrPlayer;

/// <summary>所有用户设置，保存在 %APPDATA%\HdrPlayer\settings.json。</summary>
internal sealed class AppSettings
{
    // ---- HDR ----
    // HDR 输出的开 / 关不保存：启动时跟随 Windows 当前的“使用 HDR”状态，空着启动不会去改系统设置。
    /// <summary>按片源自动切换 HDR 输出：HDR 片源开启、SDR 片源关闭（必要时同时切换 Windows HDR）。</summary>
    public bool AutoHdrForHdrVideo { get; set; } = true;
    /// <summary>开关 HDR 输出（手动或自动）时，同时切换 Windows 的“使用 HDR”。</summary>
    public bool AutoSwitchWindowsHdr { get; set; } = true;
    public bool RestoreWindowsHdrOnExit { get; set; } = true;
    /// <summary>0 = 自动读取显示器 EDID 上报的峰值亮度</summary>
    public int TargetPeakNits { get; set; } = 0;
    public string ToneMapping { get; set; } = "auto";
    public bool Hdr10PlusUseMetadata { get; set; } = true;
    public string HdrComputePeak { get; set; } = "auto";
    public string GamutMapping { get; set; } = "auto";
    /// <summary>杜比视界："rpu" = 应用 RPU 动态元数据（支持 Profile 5）；"strip" = 剥离 RPU，只播放基础层。</summary>
    public string DolbyVisionMode { get; set; } = "rpu";

    // ---- 解码 / 渲染 ----
    public string Hwdec { get; set; } = "d3d11va";
    /// <summary>软解线程数（vd-lavc-threads），0 = 自动：按 CPU 逻辑核心数，最多 16。</summary>
    public int DecoderThreads { get; set; } = 0;
    public string GpuApi { get; set; } = "d3d11";
    public string Scale { get; set; } = "ewa_lanczossharp";
    public string Dscale { get; set; } = "mitchell";
    public bool Deband { get; set; } = false;
    public string VideoSync { get; set; } = "audio";
    public bool Interpolation { get; set; } = false;
    public bool ExactSeek { get; set; } = true;

    // ---- 音频 ----
    public string AudioDevice { get; set; } = "auto";
    public bool DownmixToStereo { get; set; } = true;
    public bool NormalizeDownmix { get; set; } = false;
    public string AudioLanguages { get; set; } = "";
    public double Volume { get; set; } = 100;

    // ---- 字幕 ----
    public string SubFont { get; set; } = "Microsoft YaHei";
    public int SubFontSize { get; set; } = 44;
    public string SubAuto { get; set; } = "fuzzy";
    public string SubLanguages { get; set; } = "chi,zho,chs,zh,zh-Hans,eng,en";

    // ---- 操作 ----
    public double SeekStep { get; set; } = 5;
    public double SeekStepLarge { get; set; } = 30;
    public double VolumeStep { get; set; } = 5;
    public int SubDelayStepMs { get; set; } = 100;
    /// <summary>鼠标悬停在进度条上时显示该位置的缩略图。</summary>
    public bool SeekThumbnails { get; set; } = true;
    /// <summary>空着启动时恢复上次的播放列表（不自动播放，按播放从上次的位置继续）。</summary>
    public bool RestorePlaylist { get; set; } = true;
    public bool DebugLog { get; set; } = false;

    // ---- 窗口 ----
    public int WinX { get; set; } = -1;
    public int WinY { get; set; } = -1;
    public int WinW { get; set; } = 1600;
    public int WinH { get; set; } = 960;
    public bool WinMaximized { get; set; } = false;
    public bool PlaylistVisible { get; set; } = false;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HdrPlayer");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* 设置文件损坏时使用默认值 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}
