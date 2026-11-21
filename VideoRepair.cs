using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace HdrPlayer;

/// <summary>
/// 轻量视频修复：用 ffmpeg 不重新编码地重新封装成 MKV（-c copy），画质不变，速度主要取决于硬盘。
/// 能修好：缺索引（拖不动进度条、时长不对）、时间戳错乱（音画不同步、卡住）、TS 录像里的坏包、扩展名和实际格式不符。
/// 修不好：码流本身损坏（花屏、马赛克），以及录制中断、缺少 moov 的 MP4 / MOV（ffmpeg 根本打不开）。
/// 发布包里的 ffmpeg 在 tools\ffmpeg\（和自动同步字幕共用）；自己编译时用 PATH 里的。
/// </summary>
internal sealed class VideoRepair
{
    public sealed record Result(bool Ok, string Message, string? OutputPath = null, bool DroppedStreams = false);

    private sealed record ProbeStream(int Index, string Type, string Codec, bool AttachedPic);

    /// <summary>MKV 能原样放进去的字幕格式。</summary>
    private static readonly HashSet<string> MkvSubCodecs = new(StringComparer.OrdinalIgnoreCase)
        { "subrip", "srt", "ass", "ssa", "webvtt", "text", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle" };
    /// <summary>MP4 的文字字幕：MKV 不支持，转成 SRT（文字字幕转换不涉及画面，很快）。</summary>
    private static readonly HashSet<string> ToSrtCodecs = new(StringComparer.OrdinalIgnoreCase) { "mov_text", "tx3g" };

    private CancellationTokenSource? _cts;
    private Process? _proc;
    private string? _partFile;

    public bool Running => _cts != null;
    /// <summary>正在修复的文件（没在修复时为 null）。</summary>
    public string? Source { get; private set; }

    private static string ToolsDir => Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg");

    public static string? FindFfmpeg() => FindTool("ffmpeg.exe");
    public static string? FindFfprobe() => FindTool("ffprobe.exe");

    /// <summary>优先用自带的（tools\ffmpeg），其次 PATH 里的。</summary>
    private static string? FindTool(string name)
    {
        string bundled = Path.Combine(ToolsDir, name);
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string p = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>退出程序时调用：结束 ffmpeg，删掉没写完的文件。</summary>
    public void KillNow()
    {
        var p = _proc;
        try
        {
            if (p != null)
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(2000);
            }
        }
        catch { }
        if (_partFile != null) try { File.Delete(_partFile); } catch { }
    }

    /// <summary>
    /// 把 input 重新封装到 output（MKV）。先写到 output.part，成功后才改名，失败 / 取消时删掉。
    /// progress 收到 0~1 的进度（不知道时长时按已写入的大小估算），在后台线程调用。
    /// </summary>
    public async Task<Result> RunAsync(string input, string output, Action<double> progress)
    {
        string? ffmpeg = FindFfmpeg(), ffprobe = FindFfprobe();
        if (ffmpeg == null || ffprobe == null) return new(false, "没有找到 ffmpeg / ffprobe。");
        if (_cts != null) return new(false, "已经在修复了。");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Source = input;
        string part = output + ".part";
        _partFile = part;
        try
        {
            // ---- 先用 ffprobe 看看能不能读：ffprobe 都打不开的文件，重新封装也救不了 ----
            var (pc, pout, perr) = await RunProcess(ffprobe, new[]
            {
                "-v", "error", "-hide_banner", "-analyzeduration", "100M", "-probesize", "100M",
                "-show_entries", "format=duration:stream=index,codec_type,codec_name:stream_disposition=attached_pic",
                "-of", "json", input,
            }, null, ct);
            if (ct.IsCancellationRequested) return new(false, "已取消修复。");
            var (streams, duration) = ParseProbe(pout);
            if (pc != 0 || streams.Count == 0) return new(false, ProbeError(perr));
            if (!streams.Any(s => s.Type is "video" or "audio"))
                return new(false, "这个文件里没有找到可用的视频 / 音频流，无法修复。");

            // ---- 空间：重新封装后的大小和原文件差不多 ----
            long inSize = new FileInfo(input).Length;
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(output))!);
                if (drive.AvailableFreeSpace < inSize + 64L * 1024 * 1024)
                    return new(false, $"{drive.Name} 的剩余空间不够：需要大约 {FormatSize(inSize)}，只剩 {FormatSize(drive.AvailableFreeSpace)}。");
            }
            catch { /* 网络路径等拿不到剩余空间，直接试 */ }

            void OnProgress(string line)
            {
                // -progress 输出 key=value；out_time_us 是已写到的时间点（微秒），total_size 是已写入的字节数
                int eq = line.IndexOf('=');
                if (eq <= 0) return;
                string k = line[..eq], v = line[(eq + 1)..];
                if (duration > 0 && k == "out_time_us" && long.TryParse(v, out long us) && us >= 0)
                    progress(Math.Clamp(us / 1e6 / duration, 0, 1));
                else if (duration <= 0 && k == "total_size" && long.TryParse(v, out long bytes) && bytes >= 0 && inSize > 0)
                    progress(Math.Clamp((double)bytes / inSize, 0, 1));
            }

            // ---- 第一次：所有能放进 MKV 的流都保留；失败了再只保留视频 + 音频试一次 ----
            bool dropped = false;
            var keep = new List<(ProbeStream S, string? Codec)>();
            foreach (var s in streams)
            {
                switch (s.Type)
                {
                    case "video" or "audio" when s.Codec is not ("" or "none" or "unknown"):
                    case "attachment":
                        keep.Add((s, null));
                        break;
                    case "subtitle" when MkvSubCodecs.Contains(s.Codec):
                        keep.Add((s, null));
                        break;
                    case "subtitle" when ToSrtCodecs.Contains(s.Codec):
                        keep.Add((s, "srt"));
                        break;
                    default:
                        // 数据流（时间码、GoPro 遥测等）、MKV 不支持的字幕：省略
                        if (s.Type != "data") dropped = true;
                        break;
                }
            }

            string err = "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt == 1)
                {
                    var minimal = keep.Where(k => k.S.Type == "audio" || (k.S.Type == "video" && !k.S.AttachedPic)).ToList();
                    if (minimal.Count == keep.Count || minimal.Count == 0) break; // 已经是最少的了，没必要再试
                    keep = minimal;
                    dropped = true;
                }
                progress(0);
                try { File.Delete(part); } catch { }
                var (code, _, e) = await RunProcess(ffmpeg, BuildArgs(input, part, keep), OnProgress, ct);
                if (ct.IsCancellationRequested) return new(false, "已取消修复。");
                if (code == 0 && File.Exists(part) && new FileInfo(part).Length > 0)
                {
                    File.Move(part, output, overwrite: false);
                    return new(true, "", output, dropped);
                }
                err = e;
            }
            return new(false, "重新封装失败：" + LastError(err));
        }
        catch (Exception ex)
        {
            return new(false, "修复出错：" + ex.Message);
        }
        finally
        {
            try { File.Delete(part); } catch { }
            _partFile = null;
            Source = null;
            _cts.Dispose();
            _cts = null;
        }
    }

    private static List<string> BuildArgs(string input, string output, List<(ProbeStream S, string? Codec)> keep)
    {
        var a = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-v", "error",
            // 输入：缺时间戳的包补上、丢掉标记为损坏的包、解码错误不中断；探测久一点，坏 TS 也能找全流
            "-fflags", "+genpts+discardcorrupt", "-err_detect", "ignore_err",
            "-analyzeduration", "100M", "-probesize", "100M",
            "-i", input,
        };
        foreach (var (s, _) in keep) { a.Add("-map"); a.Add("0:" + s.Index.ToString(CultureInfo.InvariantCulture)); }
        a.Add("-c"); a.Add("copy");
        for (int i = 0; i < keep.Count; i++)
            if (keep[i].Codec is { } c) { a.Add($"-c:{i}"); a.Add(c); }
        a.AddRange(new[]
        {
            "-map_metadata", "0", "-map_chapters", "0",
            // 时间戳从 0 开始、不出现负值；坏文件里音视频包可能间隔很远，放宽混流队列
            "-avoid_negative_ts", "make_zero", "-max_muxing_queue_size", "4096",
            "-progress", "pipe:1", "-nostats",
            "-f", "matroska", output,
        });
        return a;
    }

    private static (List<ProbeStream> Streams, double Duration) ParseProbe(string json)
    {
        var list = new List<ProbeStream>();
        double duration = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("streams", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.EnumerateArray())
                {
                    int index = s.TryGetProperty("index", out var ix) && ix.TryGetInt32(out int n) ? n : -1;
                    if (index < 0) continue;
                    string type = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                    string codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                    bool pic = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var ap) &&
                               ap.TryGetInt32(out int api) && api != 0;
                    list.Add(new ProbeStream(index, type, codec, pic));
                }
            }
            if (root.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var du) &&
                double.TryParse(du.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0)
                duration = v;
        }
        catch { }
        return (list, duration);
    }

    private static string ProbeError(string log)
    {
        if (log.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase))
            return "这个 MP4 / MOV 缺少索引（moov），通常是录制时被中断（断电、没电、强制关机、存储卡被拔出）造成的。\n\n" +
                   "这种文件 ffmpeg 读不了，重新封装修不好。可以试试 untrunc 之类的专用工具，" +
                   "它需要一个同一台设备、同样设置录的完好文件作参考。";
        if (log.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase))
            return "ffmpeg 也无法识别这个文件的格式：文件可能损坏得太严重，或者根本不是视频文件，无法修复。";
        return "ffmpeg 无法读取这个文件：" + LastError(log);
    }

    private async Task<(int Code, string Out, string Err)> RunProcess(string exe, IEnumerable<string> args,
        Action<string>? onStdout, CancellationToken ct)
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

        var sbOut = new StringBuilder();
        // 坏文件可能刷出成千上万行错误，只留最后一些
        var errLines = new Queue<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (onStdout != null) onStdout(e.Data);
            else lock (sbOut) sbOut.AppendLine(e.Data);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (errLines)
            {
                errLines.Enqueue(e.Data);
                while (errLines.Count > 200) errLines.Dequeue();
            }
        };
        p.Start();
        _proc = p;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); } catch { }
            return (-1, "", "");
        }
        finally
        {
            _proc = null;
        }
        string err;
        lock (errLines) err = string.Join("\n", errLines);
        lock (sbOut) return (p.ExitCode, sbOut.ToString(), err);
    }

    private static string LastError(string log)
    {
        var line = log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "未知错误";
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";
}
