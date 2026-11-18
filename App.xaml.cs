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
        if (owner is { IsVisible: true })
        {
            box.Owner = owner;
            box.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        try { await box.ShowDialogAsync(); } catch (TaskCanceledException) { }
    }
}
