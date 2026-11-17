using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace HdrPlayer;

/// <summary>
/// 把 HdrPlayer 设为视频文件的默认播放器。
///
/// Windows 10/11 不让程序直接写用户的默认程序选择（UserChoice 带系统校验，新版 Windows 11 还换成了
/// 带机器标识的 UserChoiceLatest），设置页的“设为默认值”在不少机器上也仍然要逐个后缀确认。
/// 这里用的是微软给企业管理员的正规机制：组策略“设置默认关联配置文件”
/// （HKLM\SOFTWARE\Policies\Microsoft\Windows\System\DefaultAssociationsConfiguration 指向一个 XML），
/// 由 Windows 自己在每次登录时应用 XML 里的“后缀 → ProgID”。
///
/// 代价：写 HKLM 需要管理员权限（弹一次 UAC）；注销并重新登录后才生效；策略存在期间，每次登录都会
/// 把这些后缀恢复成 HdrPlayer（所以设置里提供“取消”）。官方只支持专业版 / 企业版 / 教育版，家庭版可能无效。
/// </summary>
internal static class DefaultApps
{
    public const string AppName = "HdrPlayer";
    public const string ProgId = "HdrPlayer.Video";

    public const string ApplyArg = "--apply-default-assoc";
    public const string RemoveArg = "--remove-default-assoc";

    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\System";
    private const string PolicyValue = "DefaultAssociationsConfiguration";

    /// <summary>和安装程序（installer/gen_iss.py）默认勾选的一致；.ts 不在内（TypeScript 也用这个后缀）。</summary>
    public static readonly string[] DefaultExtensions =
    {
        ".mkv", ".mp4", ".m4v", ".mov", ".webm", ".avi", ".wmv", ".flv", ".m2ts", ".mts",
        ".mpg", ".mpeg", ".hevc", ".h265", ".265", ".264",
    };

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName);
    private static string XmlPath => Path.Combine(Dir, "DefaultAssociations.xml");
    /// <summary>设置之前策略指向的其它配置文件（例如公司下发的），取消时还原。</summary>
    private static string PreviousPath => Path.Combine(Dir, "previous-policy.txt");

    /// <summary>安装时勾选的后缀（HdrPlayer 的 Capabilities）；没有安装版的注册信息时用默认列表。</summary>
    public static string[] ChosenExtensions()
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\{AppName}\Capabilities\FileAssociations");
        var names = k?.GetValueNames().Where(n => n.StartsWith('.')).ToArray();
        return names is { Length: > 0 } ? names : DefaultExtensions;
    }

    /// <summary>策略当前是否指向我们的配置文件。</summary>
    public static bool PolicyActive()
    {
        using var k = Registry.LocalMachine.OpenSubKey(PolicyKey);
        return k?.GetValue(PolicyValue) is string p && string.Equals(Path.GetFullPath(p), XmlPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>家庭版（EditionID 以 Core 开头）官方不支持这个策略。</summary>
    public static bool IsHomeEdition()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return k?.GetValue("EditionID") is string ed && ed.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 当前用户下注册 HdrPlayer（不需要管理员，便携版也能用）：ProgID、打开方式、Capabilities。
    /// 和安装程序写的内容相同，exe 路径用当前运行的这个。
    /// </summary>
    public static void RegisterForCurrentUser(IEnumerable<string> exts)
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HdrPlayer.exe");
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        using (var p = classes.CreateSubKey(ProgId))
        {
            p.SetValue("", "视频文件 (HdrPlayer)");
            using (var icon = p.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var open = p.CreateSubKey(@"shell\open")) open.SetValue("FriendlyAppName", AppName);
            using (var cmd = p.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        using var caps = Registry.CurrentUser.CreateSubKey($@"Software\{AppName}\Capabilities");
        caps.SetValue("ApplicationName", AppName);
        caps.SetValue("ApplicationDescription", "HDR 视频播放器");
        using var fa = caps.CreateSubKey("FileAssociations");
        using var supported = classes.CreateSubKey($@"Applications\{Path.GetFileName(exe)}\SupportedTypes");
        foreach (var e in exts)
        {
            fa.SetValue(e, ProgId);
            supported.SetValue(e, "");
            using var owp = classes.CreateSubKey($@"{e}\OpenWithProgids");
            owp.SetValue(ProgId, "");
        }
        using var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
        reg.SetValue(AppName, $@"Software\{AppName}\Capabilities");
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>以管理员身份再启动自己去写策略。返回 null 表示成功，否则是错误说明（用户取消 UAC 也算）。</summary>
    public static string? RunElevated(string arg, IEnumerable<string>? exts = null)
    {
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HdrPlayer.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = exts == null ? arg : $"{arg} \"{string.Join(",", exts)}\"",
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return "无法启动";
            p.WaitForExit();
            return p.ExitCode == 0 ? null : $"写入失败（代码 {p.ExitCode}）";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "已取消（需要管理员权限）";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 命令行入口（由 RunElevated 或安装程序以管理员身份调用）。返回进程退出码。
    /// </summary>
    public static int HandleCommandLine(string[] args)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        try
        {
            if (args[0] == ApplyArg)
            {
                var exts = args.Length > 1
                    ? args[1].Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.StartsWith('.') ? e : "." + e).ToArray()
                    : DefaultExtensions;
                ApplyPolicy(exts);
            }
            else
            {
                RemovePolicy();
            }
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void ApplyPolicy(string[] exts)
    {
        Directory.CreateDirectory(Dir);
        using var k = Registry.LocalMachine.CreateSubKey(PolicyKey);

        // 策略已经指向别的配置文件（公司 / 其它工具下发的）时，保留里面其它后缀的设置，只覆盖我们的后缀
        var merged = new List<XElement>();
        if (k.GetValue(PolicyValue) is string prev &&
            !string.Equals(Path.GetFullPath(prev), XmlPath, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(PreviousPath, prev, new UTF8Encoding(false));
            if (File.Exists(prev))
            {
                try
                {
                    merged.AddRange(XDocument.Load(prev).Root?.Elements("Association")
                        .Where(a => !exts.Contains((string?)a.Attribute("Identifier") ?? "", StringComparer.OrdinalIgnoreCase))
                        ?? Enumerable.Empty<XElement>());
                }
                catch { }
            }
        }
        else if (File.Exists(XmlPath))
        {
            // 重新设置：保留以前合并进来的其它后缀
            try
            {
                merged.AddRange(XDocument.Load(XmlPath).Root?.Elements("Association")
                    .Where(a => (string?)a.Attribute("ProgId") != ProgId &&
                                !exts.Contains((string?)a.Attribute("Identifier") ?? "", StringComparer.OrdinalIgnoreCase))
                    ?? Enumerable.Empty<XElement>());
            }
            catch { }
        }

        foreach (var e in exts)
            merged.Add(new XElement("Association",
                new XAttribute("Identifier", e.ToLowerInvariant()),
                new XAttribute("ProgId", ProgId),
                new XAttribute("ApplicationName", AppName)));
        new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement("DefaultAssociations", merged)).Save(XmlPath);

        k.SetValue(PolicyValue, XmlPath, RegistryValueKind.String);
    }

    private static void RemovePolicy()
    {
        using var k = Registry.LocalMachine.OpenSubKey(PolicyKey, writable: true);
        if (k?.GetValue(PolicyValue) is string cur &&
            string.Equals(Path.GetFullPath(cur), XmlPath, StringComparison.OrdinalIgnoreCase))
        {
            string? prev = File.Exists(PreviousPath) ? File.ReadAllText(PreviousPath).Trim() : null;
            if (!string.IsNullOrEmpty(prev)) k.SetValue(PolicyValue, prev, RegistryValueKind.String);
            else k.DeleteValue(PolicyValue, throwOnMissingValue: false);
        }
        try { File.Delete(PreviousPath); } catch { }
        try { File.Delete(XmlPath); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
