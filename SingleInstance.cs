using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace HdrPlayer;

/// <summary>
/// 只运行一个播放器窗口。第一个启动的实例持有互斥体并开一个命名管道；之后再启动（例如在资源管理器里
/// 又双击了一个视频）的实例把要打开的文件通过管道交给第一个实例，然后自己退出。
/// 互斥体和管道都只在当前用户、当前登录会话内有效；管道用 CurrentUserOnly，其他用户连不上也冒充不了。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly string Suffix = $"{UserId()}.{Process.GetCurrentProcess().SessionId}";

    private static string UserId()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { return Environment.UserName; }
    }
    private static readonly string MutexName = @"Local\HdrPlayer.SingleInstance";
    private static readonly string PipeName = "HdrPlayer.Open." + Suffix;

    private readonly Mutex _mutex;
    private bool _owned;
    private CancellationTokenSource? _cts;

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out _owned);
    }

    /// <summary>本进程是第一个实例（持有互斥体）。</summary>
    public bool IsPrimary => _owned;

    /// <summary>
    /// 把文件交给已经在运行的实例。返回 false 表示没连上（例如那个实例正在退出）。
    /// 连上后允许它把窗口切到前台：Windows 只让刚被用户启动的进程抢前台，所以要由这边授权。
    /// </summary>
    public bool Forward(string[] paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            try
            {
                if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint pid))
                    AllowSetForegroundWindow(pid);
            }
            catch { /* 只影响窗口能否切到前台 */ }
            byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paths));
            client.Write(data, 0, data.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>之前的实例正在退出时，等它放开互斥体后由本进程接手。</summary>
    public bool WaitForPrimary(int timeoutMs)
    {
        if (_owned) return true;
        try { _owned = _mutex.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { _owned = true; } // 之前的实例崩溃了
        return _owned;
    }

    /// <summary>开始接收其它实例转过来的文件；onOpen 在后台线程上调用。</summary>
    public void StartServer(Action<string[]> onOpen)
    {
        if (!_owned || _cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string json = await reader.ReadToEndAsync(ct);
                    var paths = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
                    onOpen(paths);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 单次连接出错（对方中途退出、内容不是合法 JSON 等）不影响后面的连接
                    try { await Task.Delay(200, ct); } catch { break; }
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _owned = false;
        }
        _mutex.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
