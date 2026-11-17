using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace HdrPlayer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 以管理员身份被调用来写“默认关联配置”策略（见 DefaultApps），做完就退出，不加载 mpv
        if (e.Args.Length > 0 && (e.Args[0] == DefaultApps.ApplyArg || e.Args[0] == DefaultApps.RemoveArg))
        {
            Shutdown(DefaultApps.HandleCommandLine(e.Args));
            return;
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

        var main = new MainWindow(e.Args);
        MainWindow = main;
        main.Show();
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
