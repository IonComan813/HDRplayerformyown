using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HdrPlayer;

/// <summary>版本信息：版本号、构建号（GitHub Actions 的 run_number）、更新地址，都在编译时写进程序集。</summary>
internal static class AppInfo
{
    public const string RepoUrl = "https://github.com/IonComan813/HDRplayerformyown";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>构建号，越大越新；自己编译的是 0。</summary>
    public static int Build { get; } = int.TryParse(Metadata("BuildNumber"), out int b) ? b : 0;

    /// <summary>R2 存储桶的公开访问地址（r2.dev 子域名或自定义域名）；没配置时为 null，不能检查更新。</summary>
    public static string? UpdateBaseUrl { get; } =
        Metadata("UpdateBaseUrl") is { Length: > 0 } u && Uri.TryCreate(u.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps ? uri.ToString().TrimEnd('/') : null;

    private static string? Metadata(string key) =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}

/// <summary>releasefile/latest.json：每次构建后由 GitHub Actions 和安装程序一起上传。</summary>
internal sealed class UpdateInfo
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("build")] public int Build { get; set; }
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("commit")] public string? Commit { get; set; }
}

/// <summary>
/// 检查更新、下载安装程序、退出后静默安装并重启。
/// 只在用户点“检查更新”时才联网；下载到临时目录 %TEMP%\HdrPlayer-update，校验 SHA-256 后再用，
/// 装完重新启动的 HdrPlayer 会把它删掉（只留一个很小的安装日志，下次更新时覆盖）。
/// </summary>
internal static class Updater
{
    private const string AppId = "{6F0B8F5E-2C1D-4C7A-9B3E-5D2A8E4F1C90}"; // 和 installer/HdrPlayer.iss 的 AppId 一致

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"HdrPlayer/{AppInfo.Build}");
        c.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return c;
    }

    private static string UpdatesDir => Path.Combine(Path.GetTempPath(), "HdrPlayer-update");

    /// <summary>下载好、等程序退出后安装的安装程序。</summary>
    public static string? PendingInstaller { get; set; }

    /// <summary>
    /// 当前运行的是不是安装版（Inno Setup 记录的安装目录就是程序所在目录）。
    /// 便携版不自动更新：安装程序会装到另一个目录，便携目录里的旧版本不会变。
    /// </summary>
    public static bool IsInstalledCopy()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}_is1");
            if (k?.GetValue("InstallLocation") is not string loc || loc.Length == 0) return false;
            static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');
            return string.Equals(Norm(loc), Norm(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task<UpdateInfo> FetchLatestAsync(CancellationToken ct)
    {
        string url = $"{AppInfo.UpdateBaseUrl}/releasefile/latest.json?t={DateTime.UtcNow.Ticks}";
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var info = await JsonSerializer.DeserializeAsync<UpdateInfo>(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct)
                   ?? throw new InvalidDataException("更新信息为空");
        if (info.Build <= 0 || info.File.Length == 0 || info.File.Contains('/') || info.File.Contains('\\') || info.Sha256.Length != 64)
            throw new InvalidDataException("更新信息格式不对");
        return info;
    }

    public static bool IsNewer(UpdateInfo info) => info.Build > AppInfo.Build;

    /// <summary>下载安装程序并校验大小和 SHA-256，返回本地路径。progress 报告 0~1。</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        // 旧的下载一律清掉
        foreach (var old in Directory.EnumerateFiles(UpdatesDir, "HdrPlayer-setup-*"))
            try { File.Delete(old); } catch { }

        string target = Path.Combine(UpdatesDir, $"HdrPlayer-setup-b{info.Build}.exe");
        string part = target + ".part";
        // 构建号做查询参数，避免拿到 CDN 缓存的旧文件
        string url = $"{AppInfo.UpdateBaseUrl}/releasefile/{Uri.EscapeDataString(info.File)}?b={info.Build}";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress.Report(Math.Min(1, (double)done / total));
            }
        }

        var fi = new FileInfo(part);
        if (info.Size > 0 && fi.Length != info.Size)
            throw new InvalidDataException($"下载的文件大小不对（{fi.Length} / {info.Size} 字节），可能还没上传完，稍后再试");
        string hash;
        await using (var fs = File.OpenRead(part))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(part); } catch { }
            throw new InvalidDataException("下载的文件校验失败（SHA-256 不一致），可能刚好在更新，稍后再试");
        }
        File.Move(part, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// 启动时调用：删掉下载过的安装程序。刚更新完时安装程序还在收尾（它启动了新版 HdrPlayer 之后才退出），
    /// 文件还被占用，所以在后台隔一会儿重试，最多两分钟左右。
    /// </summary>
    public static void CleanupDownloadsInBackground()
    {
        Task.Run(async () =>
        {
            int[] delays = { 0, 3, 10, 30, 60 };
            foreach (int d in delays)
            {
                if (d > 0) await Task.Delay(TimeSpan.FromSeconds(d));
                if (!Directory.Exists(UpdatesDir)) return;
                bool remaining = false;
                foreach (var f in Directory.EnumerateFiles(UpdatesDir, "HdrPlayer-setup-*"))
                {
                    try { File.Delete(f); }
                    catch { remaining = true; }
                }
                if (!remaining) return;
            }
        });
    }

    /// <summary>
    /// 程序退出时调用：静默运行安装程序（/UPDATE=1：先等 HdrPlayer 完全退出，装完后自动重新启动它）。
    /// 安装日志写在下载目录里，出问题时可以查。
    /// </summary>
    public static void LaunchPendingInstaller()
    {
        string? exe = PendingInstaller;
        PendingInstaller = null;
        if (exe == null || !File.Exists(exe)) return;
        try
        {
            string log = Path.Combine(UpdatesDir, "install.log");
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCANCEL /UPDATE=1 \"/LOG={log}\"",
            });
        }
        catch { }
    }
}
