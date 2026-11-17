using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace HdrPlayer;

/// <summary>
/// 给 mpv 用的原生子窗口（mpv 通过 wid 在里面创建自己的渲染窗口）。
/// HDR 需要 mpv 自己的 D3D11 交换链直接输出 PQ/BT.2020，所以不能把画面画进 WPF，只能嵌原生窗口。
/// </summary>
public sealed class VideoHost : HwndHost
{
    private const string ClassName = "HdrPlayerVideoHost";
    private static bool _classRegistered;

    public IntPtr Hwnd { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        RegisterClass();
        Hwnd = Native.CreateWindowEx(0, ClassName, "",
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("创建视频窗口失败：" + Marshal.GetLastWin32Error());
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Native.DestroyWindow(hwnd.Handle);
        Hwnd = IntPtr.Zero;
    }

    private static void RegisterClass()
    {
        if (_classRegistered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            // 窗口过程直接用 DefWindowProcW，不需要托管委托（也就不存在被 GC 回收的问题）
            lpfnWndProc = Native.GetProcAddress(Native.GetModuleHandle("user32.dll"), "DefWindowProcW"),
            hInstance = Native.GetModuleHandle(null),
            hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW),
            hbrBackground = Native.GetStockObject(Native.BLACK_BRUSH),
            lpszClassName = ClassName,
        };
        if (Native.RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410 /* 类已存在 */)
            throw new InvalidOperationException("注册窗口类失败：" + Marshal.GetLastWin32Error());
        _classRegistered = true;
    }
}

/// <summary>用到的 Win32 API。</summary>
internal static class Native
{
    public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    public const int BLACK_BRUSH = 4;
    public static readonly IntPtr IDC_ARROW = new(32512);
    public const uint GA_ROOT = 2;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] public static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int obj);

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, SM_SWAPBUTTON = 23;
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    /// <summary>逻辑上的鼠标右键是否按着（GetAsyncKeyState 看的是物理按键，左右键互换时要换过来）。</summary>
    public static bool IsRightButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_LBUTTON : VK_RBUTTON) & 0x8000) != 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    /// <summary>窗口所在显示器：GDI 设备名（如 \\.\DISPLAY1）和整块屏幕的物理像素矩形。</summary>
    public static (string Device, RECT Bounds) MonitorOf(IntPtr hwnd)
    {
        var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        return GetMonitorInfo(mon, ref mi) ? (mi.szDevice, mi.rcMonitor) : ("", default);
    }
}
