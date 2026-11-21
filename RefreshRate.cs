using System.Globalization;
using System.Runtime.InteropServices;

namespace HdrPlayer;

/// <summary>
/// 按视频帧率切换显示器刷新率。
/// <list type="bullet">
/// <item>支持哪些刷新率：EnumDisplaySettingsEx 列出当前分辨率下的模式（系统按 EDID + 显卡驱动给出，
/// 包括在显卡控制面板里加的自定义分辨率），只有整数，59.94 写成 59；精确值用 DXGI 的模式列表补上，
/// 拿不到时按惯例把 23 / 29 / 47 / 59 / 71 / 119 / 143 / 239 当成 ×1000/1001 的 NTSC 刷新率。</item>
/// <item>选哪个：帧率的整数倍，刷新率不低于 47.952 Hz（24 / 25 / 30 帧至少 ×2）。恰好整数倍（误差 &lt; 0.05%）优先，
/// 其次是近似的（误差 &lt; 0.5%，例如 119.88 帧用 120 Hz；mpv 的“按显示器重采样”会把这点差别补上）；
/// 同一档里优先保持当前刷新率（免得多黑屏一次），其次选最低的（刷新率越高越耗电，看视频用不上）。
/// 都没有就用原来的刷新率。不设置自定义刷新率。</item>
/// <item>怎么切：ChangeDisplaySettingsEx + CDS_FULLSCREEN，只是临时改，不写进系统设置；
/// 退出时还原，万一程序崩溃，注销 / 重启后也会恢复成原来的。</item>
/// </list>
/// </summary>
internal static class RefreshRate
{
    /// <summary>一个可用的刷新率：DevHz 是 DEVMODE 里的整数（切换时用），Hz 是精确值。</summary>
    public readonly record struct Mode(int DevHz, double Hz)
    {
        public string Text => Math.Abs(Hz - Math.Round(Hz)) < 0.0005
            ? Math.Round(Hz).ToString(CultureInfo.InvariantCulture)
            : Hz.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>刷新率不低于 47.952 Hz（= 48 × 1000/1001；低帧率视频至少 ×2，免得切到 24 / 25 / 30 Hz 这种会闪、鼠标也卡的刷新率）。</summary>
    public const double MinHz = 47.9;
    /// <summary>误差小于它算“恰好整数倍”。23.976 × 3 = 71.928：恰好；24 × 3 = 72 对 71.928：差 0.1%，算近似。</summary>
    public const double ExactTolerance = 0.0005;
    /// <summary>误差小于它算“近似”：mpv 的 display-resample 默认最多改 1% 的速度，这里留一半余量。</summary>
    public const double NearTolerance = 0.005;

    /// <summary>
    /// 为 fps 帧的视频挑刷新率。返回 null = 没有合适的（调用方用原来的刷新率）。
    /// current 是现在的刷新率：同一档里优先保持它，免得没必要地切一次（黑屏一下）；其次选最低的。
    /// </summary>
    public static Mode? Pick(double fps, IReadOnlyList<Mode> modes, Mode? current)
    {
        if (fps < 5 || fps > 400 || double.IsNaN(fps)) return null;
        Mode? best = null;
        int bestTier = int.MaxValue;
        foreach (var m in modes)
        {
            if (m.Hz < MinHz) continue;
            int k = (int)Math.Round(m.Hz / fps);
            if (k < 1) continue;
            double err = Math.Abs(m.Hz - k * fps) / (k * fps);
            int tier = err < ExactTolerance ? 0 : err < NearTolerance ? 1 : -1;
            if (tier < 0) continue;
            bool better = best == null || tier < bestTier ||
                          (tier == bestTier && !IsSame(best.Value, current) &&
                           (IsSame(m, current) || m.Hz < best.Value.Hz));
            if (better) { best = m; bestTier = tier; }
        }
        return best;
    }

    private static bool IsSame(Mode a, Mode? b) => b is { } c && a.DevHz == c.DevHz;

    // ------------------------------------------------------------------ 列出 / 切换

    /// <summary>当前的刷新率和当前分辨率下所有可用的刷新率。读不到时返回 null。</summary>
    public static (Mode Current, List<Mode> All)? Query(string gdiName)
    {
        if (string.IsNullOrEmpty(gdiName)) return null;
        var cur = NewDevMode();
        if (!EnumDisplaySettingsEx(gdiName, ENUM_CURRENT_SETTINGS, ref cur, 0) || cur.dmDisplayFrequency <= 1) return null;

        var ints = new SortedSet<int>();
        var dm = NewDevMode();
        for (int i = 0; EnumDisplaySettingsEx(gdiName, i, ref dm, 0); i++)
        {
            // 只看和现在一样的分辨率、色深，不要隔行模式
            if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight &&
                dm.dmBitsPerPel == cur.dmBitsPerPel && (dm.dmDisplayFlags & DM_INTERLACED) == 0 && dm.dmDisplayFrequency > 1)
                ints.Add((int)dm.dmDisplayFrequency);
            dm = NewDevMode();
        }
        ints.Add((int)cur.dmDisplayFrequency);

        List<double> exact;
        try { exact = DisplayHelper.DxgiRefreshRates(gdiName, cur.dmPelsWidth, cur.dmPelsHeight); }
        catch { exact = new(); }

        // 当前刷新率的精确值：DisplayConfig 里的就是正在用的那个，最准
        double? curExact = DisplayHelper.CurrentRefreshRate(gdiName);
        var all = ints.Select(f => new Mode(f, f == cur.dmDisplayFrequency && curExact is { } c && Math.Abs(c - f) < 1.01 ? c : Exact(f, exact))).ToList();
        return (all.First(m => m.DevHz == cur.dmDisplayFrequency), all);
    }

    /// <summary>
    /// DEVMODE 里的整数刷新率 → 精确值。DXGI 的列表里找和它对应的（59.94 的整数是 59，60 的是 60）；
    /// 找不到就按惯例：23 / 29 / 47 / 59 / 71 / 119 / 143 / 239 是 (f + 1) × 1000/1001，其它就是整数本身。
    /// </summary>
    private static double Exact(int f, List<double> exact)
    {
        var floorMatch = exact.Where(r => (int)Math.Floor(r + 0.0005) == f).ToList();
        if (floorMatch.Count > 0) return floorMatch.OrderBy(r => Math.Abs(r - f)).First();
        var near = exact.Where(r => Math.Abs(r - f) < 1).OrderBy(r => Math.Abs(r - f)).ToList();
        if (near.Count > 0) return near[0];
        return f is 23 or 29 or 47 or 59 or 71 or 119 or 143 or 239 ? (f + 1) * 1000.0 / 1001.0 : f;
    }

    /// <summary>切换到 devHz（DEVMODE 的整数刷新率）。只是临时改，不写进系统设置。</summary>
    public static bool Set(string gdiName, int devHz, out string error)
    {
        error = "";
        var dm = NewDevMode();
        if (!EnumDisplaySettingsEx(gdiName, ENUM_CURRENT_SETTINGS, ref dm, 0)) { error = "读不到当前显示模式"; return false; }
        if (dm.dmDisplayFrequency == devHz) return true;
        dm.dmDisplayFrequency = (uint)devHz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (r == DISP_CHANGE_SUCCESSFUL) r = ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, CDS_FULLSCREEN, IntPtr.Zero);
        if (r == DISP_CHANGE_SUCCESSFUL) return true;
        error = r switch
        {
            DISP_CHANGE_BADMODE => "显示器不支持这个模式",
            DISP_CHANGE_FAILED => "显卡驱动拒绝了这次切换",
            DISP_CHANGE_RESTART => "需要重启才能切换",
            _ => "错误码 " + r.ToString(CultureInfo.InvariantCulture),
        };
        return false;
    }

    /// <summary>还原成 devHz；失败就退回系统设置里保存的模式（我们没改过它，就是原来的）。</summary>
    public static void Restore(string gdiName, int devHz)
    {
        if (Set(gdiName, devHz, out _)) return;
        try { ChangeDisplaySettingsEx(gdiName, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); } catch { }
    }

    // ------------------------------------------------------------------ Win32

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    private const uint DM_INTERLACED = 0x2;
    private const uint CDS_TEST = 0x2, CDS_FULLSCREEN = 0x4;
    private const int DISP_CHANGE_SUCCESSFUL = 0, DISP_CHANGE_RESTART = 1, DISP_CHANGE_FAILED = -1, DISP_CHANGE_BADMODE = -2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    private static DEVMODE NewDevMode() =>
        new() { dmDeviceName = "", dmFormName = "", dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, ref DEVMODE devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr lParam);
}
