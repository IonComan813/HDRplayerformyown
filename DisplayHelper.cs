using System.Runtime.InteropServices;

namespace HdrPlayer;

/// <summary>显示器的 HDR 能力与当前状态（来自 DisplayConfig + DXGI，数据源是显示器 EDID）。</summary>
internal sealed class MonitorHdrInfo
{
    public string GdiName = "";
    public string FriendlyName = "";
    // “高级颜色”在 Windows 11 24H2 起包括 HDR 和 WCG（SDR 显示器上的自动颜色管理 ACM），不再等于 HDR，
    // 判断 HDR 要看下面的 Hdr* 字段。
    public bool AdvancedColorSupported;
    public bool AdvancedColorEnabled;
    public bool AdvancedColorForceDisabled;
    public uint BitsPerColorChannel;

    /// <summary>是否用的是 24H2 起的新接口（GET_ADVANCED_COLOR_INFO_2）；旧系统为 false，用旧接口推断。</summary>
    public bool HasColorInfo2;
    /// <summary>新旧接口有一个读成功了：下面的 HdrSupported / LimitedByPolicy 是可信的（false 才真的表示“不支持”）。</summary>
    public bool ColorInfoKnown;
    /// <summary>显示器支持 HDR（不包括只支持 WCG / ACM 的 SDR 显示器）。</summary>
    public bool HdrSupported;
    /// <summary>Windows 设置里的“使用 HDR”开关是开的。</summary>
    public bool HdrEnabled;
    /// <summary>当前实际处于 HDR 模式（开关开着但被策略限制，例如笔记本用电池时，可能不是 HDR）。</summary>
    public bool HdrActive;
    /// <summary>被系统策略限制（例如笔记本用电池时不允许 HDR）。新接口：advancedColorLimitedByPolicy；旧接口：advancedColorForceDisabled。</summary>
    public bool LimitedByPolicy;
    public bool WideColorEnforced;
    /// <summary>新接口的当前颜色模式：0 = SDR，1 = WCG，2 = HDR；-1 = 不知道（旧系统）。</summary>
    public int ActiveColorMode = -1;

    public string ActiveColorModeName => ActiveColorMode switch { 0 => "SDR", 1 => "WCG（自动颜色管理）", 2 => "HDR", _ => "" };

    public bool HasDxgi;
    public uint DxgiBitsPerColor;
    public int DxgiColorSpace;          // 12 = RGB_FULL_G2084_NONE_P2020 (HDR10)
    public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    public float[] Red = new float[2], Green = new float[2], Blue = new float[2], White = new float[2];

    public bool DxgiIsHdr => DxgiColorSpace == 12;
}

/// <summary>
/// 读取显示器 HDR 参数（DXGI IDXGIOutput6::GetDesc1），以及开关 Windows HDR
/// （DisplayConfigSetDeviceInfo / DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE）。
/// </summary>
internal static unsafe class DisplayHelper
{
    // ---------------- DisplayConfig ----------------
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint TYPE_GET_SOURCE_NAME = 1;
    private const uint TYPE_GET_TARGET_NAME = 2;
    private const uint TYPE_GET_ADVANCED_COLOR_INFO = 9;
    private const uint TYPE_SET_ADVANCED_COLOR_STATE = 10;
    // Windows 11 24H2（26100）起：HDR 和 WCG（自动颜色管理）分开查询 / 设置
    private const uint TYPE_GET_ADVANCED_COLOR_INFO_2 = 15;
    private const uint TYPE_SET_HDR_STATE = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SOURCE_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TARGET_DEVICE_NAME
    {
        public DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_ADVANCED_COLOR_INFO
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    /// <summary>
    /// 24H2 起的新结构。value 的位：0 advancedColorSupported，1 advancedColorActive，2 保留，
    /// 3 advancedColorLimitedByPolicy，4 highDynamicRangeSupported，5 highDynamicRangeUserEnabled，
    /// 6 wideColorSupported，7 wideColorUserEnabled。activeColorMode：0 SDR，1 WCG，2 HDR。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GET_ADVANCED_COLOR_INFO_2
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
        public uint activeColorMode;
    }

    /// <summary>旧的 SET_ADVANCED_COLOR_STATE 和新的 SET_HDR_STATE 布局相同：value 第 0 位是开 / 关。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SET_COLOR_STATE
    {
        public DEVICE_INFO_HEADER header;
        public uint value;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPath, out uint numMode);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPath, [Out] PATH_INFO[] paths,
        ref uint numMode, [Out] MODE_INFO[] modes, IntPtr topologyId);

    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME r);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME r);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref GET_ADVANCED_COLOR_INFO r);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref GET_ADVANCED_COLOR_INFO_2 r);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref SET_COLOR_STATE r);

    private const int ERROR_NOT_SUPPORTED = 50;
    private const int ERROR_INVALID_PARAMETER = 87;

    private record struct PathMatch(LUID TargetAdapter, uint TargetId);

    private static PathMatch? FindPath(string gdiName)
    {
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return null;
        var paths = new PATH_INFO[np];
        var modes = new MODE_INFO[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return null;

        for (int i = 0; i < np; i++)
        {
            var src = new SOURCE_DEVICE_NAME
            {
                header = new DEVICE_INFO_HEADER
                {
                    type = TYPE_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<SOURCE_DEVICE_NAME>(),
                    adapterId = paths[i].sourceInfo.adapterId,
                    id = paths[i].sourceInfo.id,
                },
                viewGdiDeviceName = "",
            };
            if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
            if (string.Equals(src.viewGdiDeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
                return new PathMatch(paths[i].targetInfo.adapterId, paths[i].targetInfo.id);
        }
        return null;
    }

    /// <summary>查询指定 GDI 显示器（如 \\.\DISPLAY1）的 HDR 信息。</summary>
    public static MonitorHdrInfo? Query(string gdiName)
    {
        var info = new MonitorHdrInfo { GdiName = gdiName };
        try
        {
            var path = FindPath(gdiName);
            if (path is { } p)
            {
                var name = new TARGET_DEVICE_NAME
                {
                    header = new DEVICE_INFO_HEADER
                    {
                        type = TYPE_GET_TARGET_NAME, size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>(),
                        adapterId = p.TargetAdapter, id = p.TargetId,
                    },
                    monitorFriendlyDeviceName = "", monitorDevicePath = "",
                };
                if (DisplayConfigGetDeviceInfo(ref name) == 0) info.FriendlyName = name.monitorFriendlyDeviceName;

                if (!ReadColorInfo2(p, info)) ReadColorInfoLegacy(p, info);
            }
        }
        catch { /* 旧系统或驱动不支持时忽略 */ }

        try { QueryDxgi(gdiName, info); } catch { }
        return info;
    }

    /// <summary>新接口（Windows 11 24H2 起）。旧系统上返回 false（ERROR_INVALID_PARAMETER / NOT_SUPPORTED）。</summary>
    private static bool ReadColorInfo2(PathMatch p, MonitorHdrInfo info)
    {
        var ac = new GET_ADVANCED_COLOR_INFO_2
        {
            header = new DEVICE_INFO_HEADER
            {
                type = TYPE_GET_ADVANCED_COLOR_INFO_2, size = (uint)Marshal.SizeOf<GET_ADVANCED_COLOR_INFO_2>(),
                adapterId = p.TargetAdapter, id = p.TargetId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref ac) != 0) return false;
        uint v = ac.value;
        info.HasColorInfo2 = true;
        info.ColorInfoKnown = true;
        info.AdvancedColorSupported = (v & 0x01) != 0;
        info.AdvancedColorEnabled = (v & 0x02) != 0;
        info.LimitedByPolicy = (v & 0x08) != 0;
        info.HdrSupported = (v & 0x10) != 0;
        info.HdrEnabled = (v & 0x20) != 0;
        info.BitsPerColorChannel = ac.bitsPerColorChannel;
        info.ActiveColorMode = (int)ac.activeColorMode;
        info.HdrActive = ac.activeColorMode == 2;
        return true;
    }

    /// <summary>
    /// 旧接口（24H2 之前）。advancedColorEnabled 在 SDR 显示器开着自动颜色管理（ACM）时也是 1，
    /// 这时 wideColorEnforced 为 1：所以 HDR = 高级颜色 且 不是强制 WCG。
    /// </summary>
    private static void ReadColorInfoLegacy(PathMatch p, MonitorHdrInfo info)
    {
        var ac = new GET_ADVANCED_COLOR_INFO
        {
            header = new DEVICE_INFO_HEADER
            {
                type = TYPE_GET_ADVANCED_COLOR_INFO, size = (uint)Marshal.SizeOf<GET_ADVANCED_COLOR_INFO>(),
                adapterId = p.TargetAdapter, id = p.TargetId,
            },
        };
        if (DisplayConfigGetDeviceInfo(ref ac) != 0) return;
        info.ColorInfoKnown = true;
        info.AdvancedColorSupported = (ac.value & 0x1) != 0;
        info.AdvancedColorEnabled = (ac.value & 0x2) != 0;
        info.WideColorEnforced = (ac.value & 0x4) != 0;
        info.AdvancedColorForceDisabled = (ac.value & 0x8) != 0;
        info.BitsPerColorChannel = ac.bitsPerColorChannel;
        info.HdrSupported = info.AdvancedColorSupported && !info.WideColorEnforced;
        info.HdrEnabled = info.AdvancedColorEnabled && !info.WideColorEnforced;
        info.LimitedByPolicy = info.AdvancedColorForceDisabled;
        info.HdrActive = info.HdrEnabled && !info.AdvancedColorForceDisabled;
    }

    /// <summary>
    /// 开关 Windows 的 HDR（“使用 HDR”开关）。24H2 起用 SET_HDR_STATE（只动 HDR，不碰自动颜色管理）；
    /// 旧系统用 SET_ADVANCED_COLOR_STATE（旧系统上高级颜色就是 HDR）。
    /// </summary>
    public static bool SetWindowsHdr(string gdiName, bool enable, out string error)
    {
        error = "";
        var path = FindPath(gdiName);
        if (path is not { } p) { error = "找不到对应的显示路径"; return false; }

        var probe = new MonitorHdrInfo();
        int r;
        if (ReadColorInfo2(p, probe))
        {
            r = SetState(p, TYPE_SET_HDR_STATE, enable);
            // 万一新查询接口能用、新设置接口却不认（预览版系统等），退回旧接口
            if (r is ERROR_INVALID_PARAMETER or ERROR_NOT_SUPPORTED) r = SetState(p, TYPE_SET_ADVANCED_COLOR_STATE, enable);
        }
        else
        {
            r = SetState(p, TYPE_SET_ADVANCED_COLOR_STATE, enable);
        }
        if (r != 0) { error = "DisplayConfigSetDeviceInfo 错误码 " + r; return false; }
        return true;
    }

    private static int SetState(PathMatch p, uint type, bool enable)
    {
        var st = new SET_COLOR_STATE
        {
            header = new DEVICE_INFO_HEADER
            {
                type = type, size = (uint)Marshal.SizeOf<SET_COLOR_STATE>(),
                adapterId = p.TargetAdapter, id = p.TargetId,
            },
            value = enable ? 1u : 0u,
        };
        return DisplayConfigSetDeviceInfo(ref st);
    }

    // ---------------- DXGI ----------------
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IID_IDXGIOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC1
    {
        public fixed char DeviceName[32];
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
        public uint BitsPerColor;
        public int ColorSpace;
        public fixed float RedPrimary[2];
        public fixed float GreenPrimary[2];
        public fixed float BluePrimary[2];
        public fixed float WhitePoint[2];
        public float MinLuminance;
        public float MaxLuminance;
        public float MaxFullFrameLuminance;
    }

    private static IntPtr* Vtbl(IntPtr obj) => *(IntPtr**)obj;

    private static void Release(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        var f = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(obj)[2];
        f(obj);
    }

    private static void QueryDxgi(string gdiName, MonitorHdrInfo info)
    {
        // 每次新建 factory，保证拿到切换 HDR 之后的最新数据
        if (CreateDXGIFactory1(IID_IDXGIFactory1, out IntPtr factory) < 0) return;
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(factory)[7];
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                if (enumAdapters(factory, a, &adapter) < 0) break;
                try
                {
                    var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vtbl(adapter)[7];
                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        if (enumOutputs(adapter, o, &output) < 0) break;
                        IntPtr output6 = IntPtr.Zero;
                        try
                        {
                            var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(output)[0];
                            Guid iid = IID_IDXGIOutput6;
                            if (qi(output, &iid, &output6) < 0) continue;

                            // IDXGIOutput6::GetDesc1 位于 vtable 第 27 项
                            var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC1*, int>)Vtbl(output6)[27];
                            DXGI_OUTPUT_DESC1 d;
                            if (getDesc1(output6, &d) < 0) continue;

                            string dev = new string(d.DeviceName);
                            if (!string.Equals(dev, gdiName, StringComparison.OrdinalIgnoreCase)) continue;

                            info.HasDxgi = true;
                            info.DxgiBitsPerColor = d.BitsPerColor;
                            info.DxgiColorSpace = d.ColorSpace;
                            info.MinLuminance = d.MinLuminance;
                            info.MaxLuminance = d.MaxLuminance;
                            info.MaxFullFrameLuminance = d.MaxFullFrameLuminance;
                            info.Red = new[] { d.RedPrimary[0], d.RedPrimary[1] };
                            info.Green = new[] { d.GreenPrimary[0], d.GreenPrimary[1] };
                            info.Blue = new[] { d.BluePrimary[0], d.BluePrimary[1] };
                            info.White = new[] { d.WhitePoint[0], d.WhitePoint[1] };
                            return;
                        }
                        finally
                        {
                            Release(output6);
                            Release(output);
                        }
                    }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
    }
}
