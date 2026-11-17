namespace HdrPlayer;

/// <summary>
/// 读取一个没在播放的文件的视频参数：临时开一个不出画面、不出声的 mpv，
/// 打开文件并解码出第一帧（视频参数要解码后才有），读完就销毁。在后台线程调用。
/// </summary>
internal static class MediaProbe
{
    public static string Run(string path, Func<Mpv, string> build)
    {
        using var loaded = new ManualResetEventSlim(false);
        using var restart = new ManualResetEventSlim(false);
        string? error = null;

        var m = new Mpv();
        try
        {
            m.SetOption("vo", "null");
            m.SetOption("ao", "null");
            m.SetOption("idle", "yes");
            m.SetOption("pause", "yes");
            m.SetOption("keep-open", "always");
            m.SetOption("force-window", "no");
            m.SetOption("terminal", "no");
            m.SetOption("osd-level", "0");
            m.SetOption("load-scripts", "no");
            m.SetOption("ytdl", "no");
            m.SetOption("input-default-bindings", "no");
            m.SetOption("audio-display", "no");
            m.SetOption("hwdec", "auto-copy");
            m.FileLoaded += () => loaded.Set();
            m.PlaybackRestart += () => restart.Set();
            m.EndFile += (reason, err) =>
            {
                if (reason == MpvEndReason.Error) { error = Mpv.ErrorString(err); loaded.Set(); }
            };
            m.Initialize();

            m.Command("loadfile", path, "replace");
            if (!loaded.Wait(10000)) return $"打开超时：{path}";
            if (error != null) return $"无法打开：{path}\n{error}";
            // 等第一帧解码出来（纯音频文件没有画面，最多等几秒）；再稍等一下让音频参数也就绪
            restart.Wait(5000);
            Thread.Sleep(300);
            return build(m);
        }
        finally
        {
            m.Dispose();
        }
    }
}
