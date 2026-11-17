using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace HdrPlayer;

/// <summary>
/// 给 mpv 用的原生子窗口（mpv 通过 wid 在里面创建自己的渲染窗口）。
/// HDR 需要 mpv 自己的 D3D11 交换链直接输出 PQ/BT.2020，所以不能把画面画进 WPF，只能嵌原生窗口。
///
/// 鼠标输入在这里处理，而不是交给 mpv：mpv 在 Windows 上嵌入（wid）时会对自己的子窗口调用
/// EnableWindow(FALSE)（见 mpv video/out/w32_common.c），被禁用的子窗口收不到任何鼠标消息，
/// 系统把它们转给父窗口，也就是这个窗口。所以 mpv 的 MBTN_* 绑定在嵌入时根本不会触发，
/// 之前画面上右键没有菜单就是这个原因。这个窗口和 WPF 在同一个线程，可以直接弹 WPF 菜单。
/// </summary>
public sealed class VideoHost : HwndHost
{
    private const string ClassName = "HdrPlayerVideoHost";
    private static bool _classRegistered;
    // 窗口过程委托必须一直被引用，否则 GC 回收后原生代码再调用就会崩溃
    private static readonly Native.WndProc WndProcDelegate = StaticWndProc;
    private static readonly Dictionary<IntPtr, VideoHost> Instances = new();

    public IntPtr Hwnd { get; private set; }

    /// <summary>左键单击（松开时触发；双击的两次单击也会各触发一次，由使用方按时间区分）。</summary>
    public event Action? LeftClick;
    public event Action? LeftDoubleClick;
    /// <summary>右键松开：弹出菜单。</summary>
    public event Action? RightClick;
    public event Action? MiddleClick;
    /// <summary>滚轮，参数为 WHEEL_DELTA 的倍数（向上为正）。</summary>
    public event Action<int>? Wheel;

    private bool _hideCursor;
    /// <summary>为 true 时鼠标指针停在画面上不显示（和悬浮控制栏一起隐藏）。</summary>
    public bool HideCursor
    {
        get => _hideCursor;
        set
        {
            if (_hideCursor == value) return;
            _hideCursor = value;
            // WM_SETCURSOR 只在鼠标移动时才发，静止时隐藏 / 显示要立刻设置一次
            if (Hwnd != IntPtr.Zero && IsCursorOver())
                Native.SetCursor(value ? IntPtr.Zero : Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW));
        }
    }

    private bool IsCursorOver() =>
        Native.GetCursorPos(out var p) && Native.GetWindowRect(Hwnd, out var r) && r.Contains(p) &&
        Native.WindowFromPoint(p) == Hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        RegisterClass();
        Hwnd = Native.CreateWindowEx(0, ClassName, "",
            Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("创建视频窗口失败：" + Marshal.GetLastWin32Error());
        Instances[Hwnd] = this;
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Instances.Remove(hwnd.Handle);
        Native.DestroyWindow(hwnd.Handle);
        Hwnd = IntPtr.Zero;
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (Instances.TryGetValue(hwnd, out var host) && host.HandleMessage(msg, wParam, lParam, out IntPtr result))
                return result;
        }
        catch (Exception ex)
        {
            // 异常不能穿过原生窗口过程，交给 WPF 的全局异常处理
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("视频窗口消息处理出错", ex)));
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool HandleMessage(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        switch (msg)
        {
            case Native.WM_LBUTTONDOWN:
            case Native.WM_RBUTTONDOWN:
            case Native.WM_MBUTTONDOWN:
                // 按住时移出画面也能收到松开
                Native.SetCapture(Hwnd);
                return true;
            case Native.WM_LBUTTONUP:
                Native.ReleaseCapture();
                if (IsCursorOver()) LeftClick?.Invoke();
                return true;
            case Native.WM_LBUTTONDBLCLK:
                Native.SetCapture(Hwnd);
                LeftDoubleClick?.Invoke();
                return true;
            case Native.WM_RBUTTONUP:
                Native.ReleaseCapture();
                // 不交给 DefWindowProc，否则它还会再发一个 WM_CONTEXTMENU
                if (IsCursorOver()) RightClick?.Invoke();
                return true;
            case Native.WM_MBUTTONUP:
                Native.ReleaseCapture();
                if (IsCursorOver()) MiddleClick?.Invoke();
                return true;
            case Native.WM_MOUSEWHEEL:
                Wheel?.Invoke((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                return true;
            case Native.WM_SETCURSOR:
                if (_hideCursor && (lParam.ToInt64() & 0xFFFF) == Native.HTCLIENT)
                {
                    Native.SetCursor(IntPtr.Zero);
                    result = new IntPtr(1);
                    return true;
                }
                return false;
        }
        return false;
    }

    private static void RegisterClass()
    {
        if (_classRegistered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = Native.CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
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
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20;
    public const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    public const int BLACK_BRUSH = 4;
    public const uint CS_DBLCLKS = 0x0008;
    public const uint WM_SETCURSOR = 0x0020, WM_MOUSEWHEEL = 0x020A,
        WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    public const int HTCLIENT = 1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
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
