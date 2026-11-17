using System.Runtime.InteropServices;

namespace HdrPlayer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string dll = Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll");
        if (!File.Exists(dll) || !NativeLibrary.TryLoad(dll, out _))
        {
            MessageBox.Show(
                "没有找到 libmpv-2.dll，或无法加载。\n\n" +
                "请从 https://github.com/shinchiro/mpv-winbuild-cmake/releases 下载\n" +
                "mpv-dev-x86_64-v3-*.7z，解压出 libmpv-2.dll 放到本程序同一目录。",
                "HdrPlayer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm(args));
    }
}
