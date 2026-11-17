using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HdrPlayer;

/// <summary>
/// 用 ffsubsync（https://github.com/smacke/ffsubsync）按画面里的人声自动对齐字幕。
/// 发布包里带了用 PyInstaller 打包的 tools\ffsubsync\ffsubsync.exe 和 tools\ffmpeg\（ffmpeg / ffprobe）；
/// 自己编译时也可以 pip install ffsubsync，并把 ffmpeg 放进 PATH。
/// </summary>
internal sealed class SubSync
{
    public sealed record Result(bool Ok, string Message, string? OutputPath = null, double Offset = 0, double Scale = 1);

    private Process? _proc;
    private CancellationTokenSource? _cts;

    public bool Running => _proc != null;

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");

    /// <summary>ffsubsync 可执行文件：优先用自带的，其次 PATH 里的（pip 安装）。</summary>
    public static string? FindExe()
    {
        string bundled = Path.Combine(ToolsDir, "ffsubsync", "ffsubsync.exe");
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "ffsubsync.exe", "ffs.exe" })
            {
                try
                {
                    string p = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>自带的 ffmpeg 目录（含 ffmpeg.exe / ffprobe.exe）；没有就用 PATH 里的。</summary>
    private static string? FfmpegDir()
    {
        string dir = Path.Combine(ToolsDir, "ffmpeg");
        return File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe")) ? dir : null;
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// 把字幕对齐到 reference 里的音轨 audioStream（ffmpeg 记法，如 0:a:1；外挂音轨时 reference 就是那个音频文件）。
    /// subFile 为 null 时先从 video 里提取内嵌字幕 embeddedSubStream（如 0:s:1）。
    /// </summary>
    public async Task<Result> RunAsync(string video, string reference, string audioStream, string? subFile, string? embeddedSubStream,
        string outputPath, Action<string> progress)
    {
        string? exe = FindExe();
        if (exe == null) return new(false, "没有找到 ffsubsync。");
        if (_proc != null) return new(false, "已经在同步了。");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string? tempSub = null;
        try
        {
            if (subFile == null)
            {
                progress("正在提取内嵌字幕…");
                tempSub = Path.Combine(Path.GetTempPath(), $"hdrplayer-sub-{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
                var (code, log) = await RunProcess(exe, new[] { video, "--extract-subs-from-stream", embeddedSubStream!, "-o", tempSub }, ct);
                if (ct.IsCancellationRequested) return new(false, "已取消字幕同步");
                if (code != 0 || !File.Exists(tempSub)) return new(false, "提取内嵌字幕失败：" + LastError(log));
                subFile = tempSub;
            }

            progress("正在同步字幕（分析整部片子的人声，长片可能要一两分钟）…");
            var args = new List<string>
            {
                reference, "-i", subFile, "-o", outputPath,
                // 用当前选中的音轨做参照：不让它先去找片子里自带的字幕当参照（那条字幕本身可能就不同步）
                "--reference-stream", audioStream, "--vad", "webrtc",
            };
            var (rc, output) = await RunProcess(exe, args, ct);
            if (ct.IsCancellationRequested) return new(false, "已取消字幕同步");
            if (rc != 0 || !File.Exists(outputPath)) return new(false, "字幕同步失败：" + LastError(output));

            double offset = ParseNumber(output, @"offset seconds:\s*(-?[\d.]+)") ?? 0;
            double scale = ParseNumber(output, @"framerate scale factor:\s*(-?[\d.]+)") ?? 1;
            return new(true, "", outputPath, offset, scale);
        }
        catch (Exception ex)
        {
            return new(false, "字幕同步出错：" + ex.Message);
        }
        finally
        {
            if (tempSub != null) try { File.Delete(tempSub); } catch { }
            _cts.Dispose();
            _cts = null;
        }
    }

    private async Task<(int Code, string Output)> RunProcess(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        string? ff = FfmpegDir();
        if (ff != null)
        {
            psi.ArgumentList.Add("--ffmpeg-path");
            psi.ArgumentList.Add(ff);
        }
        // 日志用 rich 输出：不要颜色、不要按窗口宽度折行，方便解析
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";
        psi.Environment["COLUMNS"] = "400";

        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.Start();
        _proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "");
        }
        finally
        {
            _proc = null;
        }
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    /// <summary>退出程序时调用：结束正在运行的 ffsubsync（和它启动的 ffmpeg）。</summary>
    public void KillNow()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { }
    }

    private static double? ParseNumber(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    }

    /// <summary>从日志里挑出最后一条有用的错误信息（Python 异常 > ffsubsync 的 ERROR 日志 > 最后一行）。</summary>
    private static string LastError(string log)
    {
        var lines = log.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("[PYI-")).ToList();
        string? err = null;
        var exc = lines.LastOrDefault(l => Regex.IsMatch(l, @"^[A-Za-z_.]*(Error|Exception): "));
        if (exc != null) err = exc[(exc.IndexOf(": ", StringComparison.Ordinal) + 2)..];
        else
        {
            // rich 日志行：“[时间] ERROR    消息    文件.py:行号”
            var log1 = lines.LastOrDefault(l => Regex.IsMatch(l, @"\bERROR\b"));
            if (log1 != null)
                err = Regex.Replace(Regex.Replace(log1, @"^(\[[^\]]*\]\s*)?ERROR\s+", ""), @"\s+\S+\.py:\d+$", "");
        }
        err ??= lines.LastOrDefault() ?? "未知错误";
        if (err.Contains("Unable to detect speech", StringComparison.OrdinalIgnoreCase))
            err = "没有在这条音轨里检测到人声，换一条音轨再试";
        return err.Length > 300 ? err[..300] + "…" : err;
    }
}
