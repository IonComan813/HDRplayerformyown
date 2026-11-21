using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace HdrPlayer;

public partial class App : Application
{
    private SingleInstance? _single;

    protected override void OnExit(ExitEventArgs e)
    {
        // 先放开单实例互斥体：更新安装程序靠它判断 HdrPlayer 已经退出
        _single?.Dispose();
        Updater.LaunchPendingInstaller();
        base.OnExit(e);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 以管理员身份被调用来写“默认关联配置”策略（见 DefaultApps），做完就退出，不加载 mpv
        if (e.Args.Length > 0 && (e.Args[0] == DefaultApps.ApplyArg || e.Args[0] == DefaultApps.RemoveArg))
        {
            Shutdown(DefaultApps.HandleCommandLine(e.Args));
            return;
        }

        // 只开一个窗口：已经有 HdrPlayer 在运行时，把要打开的文件交给它，自己退出
        string[] files = HdrPlayer.MainWindow.ExistingPaths(e.Args);
        _single = new SingleInstance();
        if (!_single.IsPrimary)
        {
            if (_single.Forward(files))
            {
                Shutdown();
                return;
            }
            // 没连上：之前的实例可能正在退出，等它放开后由这里接手（等不到也照常启动）
            _single.WaitForPrimary(5000);
        }

        string dll = Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
        if (!File.Exists(dll) || !NativeLibrary.TryLoad(dll, out _))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await Dialogs.ShowAsync(null, "HdrPlayer",
                "没有找到 libmpv-2.dll，或无法加载。\n\n" +
                "请从 https://github.com/shinchiro/mpv-winbuild-cmake/releases 下载\n" +
                "mpv-dev-x86_64-v3-*.7z，解压出 libmpv-2.dll 放到本程序同一目录。");
            Shutdown();
            return;
        }

        // 上次“检查更新”下载的安装程序（装完以后就没用了）
        Updater.CleanupDownloadsInBackground();

        var main = new MainWindow(files);
        MainWindow = main;
        main.Show();
        _single.StartServer(paths => Dispatcher.BeginInvoke(() => main.OpenFromOtherInstance(paths)));
    }
}

/// <summary>Fluent 风格的消息框（WPF-UI MessageBox 的简单封装）。</summary>
internal static class Dialogs
{
    public static async Task ShowAsync(Window? owner, string title, string text)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = "确定",
        };
        Place(box, owner);
        try { await box.ShowDialogAsync(); } catch (TaskCanceledException) { }
    }

    /// <summary>带“确定 / 取消”两个按钮的对话框，点了主按钮返回 true。</summary>
    public static async Task<bool> ConfirmAsync(Window? owner, string title, string text, string okText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            PrimaryButtonText = okText,
            CloseButtonText = "取消",
        };
        Place(box, owner);
        try { return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary; }
        catch (TaskCanceledException) { return false; }
    }

    /// <summary>
    /// 居中到 owner 上。WPF-UI 的 MessageBox 在 Loaded 里按 Owner.Left / Top / Width / Height 自己居中，
    /// 但主窗口最大化时这几个值是“还原”后的位置和大小，弹窗就偏到一边去了（owner 最小化时它干脆居中到主屏幕）。
    /// 它的 Loaded 处理在构造时就挂上了，这里挂的排在它后面：等它按内容改好尺寸，再按 owner 实际的屏幕矩形
    /// （物理像素，最大化、多显示器、不同缩放比例都准）重新放到正中，并保证不超出那块屏幕的工作区。
    /// </summary>
    private static void Place(Wpf.Ui.Controls.MessageBox box, Window? owner)
    {
        if (owner is not { IsVisible: true }) return;
        box.Owner = owner;
        box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        box.Loaded += (_, _) =>
        {
            try { CenterOn(box, owner); } catch { /* 定位失败就保持 WPF-UI 原来的位置 */ }
        };
    }

    private static void CenterOn(Window box, Window owner)
    {
        IntPtr bh = new System.Windows.Interop.WindowInteropHelper(box).Handle;
        IntPtr oh = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        if (bh == IntPtr.Zero || oh == IntPtr.Zero) return;
        var work = Native.WorkAreaOf(oh);
        // 最小化的窗口没有可用的矩形，就放在它所在屏幕的正中
        if (owner.WindowState == WindowState.Minimized || !Native.GetWindowRect(oh, out var area)) area = work;
        // 弹窗尺寸：WPF-UI 刚按内容设好的 Width / Height（DIP）换算成这块屏幕上的物理像素
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(owner);
        int w = (int)Math.Round((double.IsNaN(box.Width) ? box.ActualWidth : box.Width) * dpi.DpiScaleX);
        int h = (int)Math.Round((double.IsNaN(box.Height) ? box.ActualHeight : box.Height) * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;
        int x = area.Left + (area.Width - w) / 2, y = area.Top + (area.Height - h) / 2;
        if (work.Width > 0 && work.Height > 0)
        {
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        }
        Native.SetWindowPos(bh, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
}
