using System.Globalization;
using System.Runtime.InteropServices;

namespace HdrPlayer;

internal enum MpvFormat
{
    None = 0, String = 1, OsdString = 2, Flag = 3, Int64 = 4, Double = 5,
    Node = 6, NodeArray = 7, NodeMap = 8, ByteArray = 9,
}

internal enum MpvEventId
{
    None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4,
    CommandReply = 5, StartFile = 6, EndFile = 7, FileLoaded = 8, ClientMessage = 16,
    VideoReconfig = 17, AudioReconfig = 18, Seek = 20, PlaybackRestart = 21,
    PropertyChange = 22, QueueOverflow = 24, Hook = 25,
}

/// <summary>mpv_event_end_file.reason</summary>
internal enum MpvEndReason { Eof = 0, Stop = 2, Quit = 3, Error = 4, Redirect = 5 }

/// <summary>libmpv 的 P/Invoke 声明（client.h）。</summary>
internal static class MpvNative
{
    private const string Lib = "libmpv-2.dll";
    private const CallingConvention CC = CallingConvention.Cdecl;

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEvent
    {
        public MpvEventId event_id;
        public int error;
        public ulong reply_userdata;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventProperty
    {
        public IntPtr name;
        public MpvFormat format;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventEndFile
    {
        public MpvEndReason reason;
        public int error;
        public long playlist_entry_id;
        public long playlist_insert_id;
        public int playlist_insert_num_entries;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvEventClientMessage
    {
        public int num_args;
        public IntPtr args;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct MpvNode
    {
        [FieldOffset(0)] public IntPtr ptr;
        [FieldOffset(0)] public long i64;
        [FieldOffset(0)] public double dbl;
        [FieldOffset(0)] public int flag;
        [FieldOffset(8)] public MpvFormat format;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvNodeList
    {
        public int num;
        public IntPtr values;
        public IntPtr keys;
    }

    [DllImport(Lib, CallingConvention = CC)] public static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CC)] public static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CC)] public static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CC)] public static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CC)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib, CallingConvention = CC)] public static extern IntPtr mpv_error_string(int error);
    [DllImport(Lib, CallingConvention = CC)] public static extern void mpv_free(IntPtr data);
    [DllImport(Lib, CallingConvention = CC)] public static extern void mpv_free_node_contents(IntPtr node);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern IntPtr mpv_get_property_osd_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_get_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, IntPtr data);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_set_property(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format, IntPtr data);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_command_ret(IntPtr ctx, IntPtr[] args, IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    public struct MpvByteArray
    {
        public IntPtr data;
        public UIntPtr size;
    }

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_command_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    [DllImport(Lib, CallingConvention = CC)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong userdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, MpvFormat format);
}

/// <summary>
/// libmpv 的薄封装。事件在后台线程触发，订阅方负责切回 UI 线程。
/// </summary>
internal sealed class Mpv : IDisposable
{
    private IntPtr _ctx;
    private Thread? _eventThread;
    private volatile bool _stopping;

    public event Action<string, object?>? PropertyChanged;
    public event Action? StartFile;
    public event Action? FileLoaded;
    public event Action<MpvEndReason, int>? EndFile;
    public event Action<string[]>? ClientMessage;
    public event Action? VideoReconfig;
    public event Action? AudioReconfig;
    public event Action? PlaybackRestart;
    public event Action? Shutdown;

    public bool IsAlive => _ctx != IntPtr.Zero && !_stopping;

    public Mpv()
    {
        _ctx = MpvNative.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv_create 失败");
    }

    public static string ErrorString(int err) =>
        Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(err)) ?? err.ToString(CultureInfo.InvariantCulture);

    /// <summary>初始化前设置选项。返回 mpv 错误码（&lt;0 表示失败）。</summary>
    public int SetOption(string name, string value) =>
        _ctx == IntPtr.Zero ? -1 : MpvNative.mpv_set_option_string(_ctx, name, value);

    public void Initialize()
    {
        int r = MpvNative.mpv_initialize(_ctx);
        if (r < 0) throw new InvalidOperationException("mpv_initialize 失败: " + ErrorString(r));
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    public int SetProperty(string name, string value) =>
        IsAlive ? MpvNative.mpv_set_property_string(_ctx, name, value) : -1;

    public int SetProperty(string name, double value) =>
        SetProperty(name, value.ToString(CultureInfo.InvariantCulture));

    public int SetProperty(string name, bool value) => SetProperty(name, value ? "yes" : "no");

    /// <summary>
    /// 以 MPV_FORMAT_NODE 设置属性（如 menu-data，它不接受字符串）。value 可以是
    /// string / bool / int / long / double / IList&lt;object?&gt; / IDictionary&lt;string, object?&gt; 的任意嵌套。
    /// mpv 会复制一份数据，调用结束后这里分配的内存全部释放。
    /// </summary>
    public int SetPropertyNode(string name, object? value)
    {
        if (!IsAlive) return -1;
        var allocs = new List<IntPtr>();
        try
        {
            IntPtr node = Alloc(allocs, NodeSize);
            WriteNode(node, value, allocs);
            return MpvNative.mpv_set_property(_ctx, name, MpvFormat.Node, node);
        }
        finally
        {
            foreach (var p in allocs) Marshal.FreeHGlobal(p);
        }
    }

    // mpv_node：8 字节 union + 4 字节 format（按 8 对齐共 16 字节）
    // mpv_node_list：int num（补齐到 8）+ mpv_node* values + char** keys，共 24 字节
    private const int NodeSize = 16, NodeListSize = 24;

    private static IntPtr Alloc(List<IntPtr> allocs, int size)
    {
        IntPtr p = Marshal.AllocHGlobal(size);
        allocs.Add(p);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }

    private static IntPtr Utf8(List<IntPtr> allocs, string s)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
        IntPtr p = Alloc(allocs, bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }

    private static void WriteNode(IntPtr dst, object? value, List<IntPtr> allocs)
    {
        MpvFormat format;
        switch (value)
        {
            case null:
                format = MpvFormat.None;
                break;
            case string s:
                Marshal.WriteIntPtr(dst, Utf8(allocs, s));
                format = MpvFormat.String;
                break;
            case bool b:
                Marshal.WriteInt32(dst, b ? 1 : 0);
                format = MpvFormat.Flag;
                break;
            case int or long:
                Marshal.WriteInt64(dst, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                format = MpvFormat.Int64;
                break;
            case double d:
                Marshal.WriteInt64(dst, BitConverter.DoubleToInt64Bits(d));
                format = MpvFormat.Double;
                break;
            case IDictionary<string, object?> map:
            {
                IntPtr list = Alloc(allocs, NodeListSize);
                IntPtr values = Alloc(allocs, Math.Max(1, map.Count) * NodeSize);
                IntPtr keys = Alloc(allocs, Math.Max(1, map.Count) * IntPtr.Size);
                int i = 0;
                foreach (var kv in map)
                {
                    Marshal.WriteIntPtr(keys, i * IntPtr.Size, Utf8(allocs, kv.Key));
                    WriteNode(values + i * NodeSize, kv.Value, allocs);
                    i++;
                }
                Marshal.WriteInt32(list, 0, map.Count);
                Marshal.WriteIntPtr(list, 8, values);
                Marshal.WriteIntPtr(list, 16, keys);
                Marshal.WriteIntPtr(dst, list);
                format = MpvFormat.NodeMap;
                break;
            }
            case System.Collections.IList arr:
            {
                IntPtr list = Alloc(allocs, NodeListSize);
                IntPtr values = Alloc(allocs, Math.Max(1, arr.Count) * NodeSize);
                for (int i = 0; i < arr.Count; i++) WriteNode(values + i * NodeSize, arr[i], allocs);
                Marshal.WriteInt32(list, 0, arr.Count);
                Marshal.WriteIntPtr(list, 8, values);
                Marshal.WriteIntPtr(list, 16, IntPtr.Zero);
                Marshal.WriteIntPtr(dst, list);
                format = MpvFormat.NodeArray;
                break;
            }
            default:
                throw new ArgumentException("不支持的 mpv node 类型：" + value.GetType());
        }
        Marshal.WriteInt32(dst, 8, (int)format);
    }

    public string? GetString(string name)
    {
        if (!IsAlive) return null;
        IntPtr p = MpvNative.mpv_get_property_string(_ctx, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public string? GetOsdString(string name)
    {
        if (!IsAlive) return null;
        IntPtr p = MpvNative.mpv_get_property_osd_string(_ctx, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public double? GetDouble(string name)
    {
        if (!IsAlive) return null;
        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            int r = MpvNative.mpv_get_property(_ctx, name, MpvFormat.Double, buf);
            if (r < 0) return null;
            return BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buf));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public long? GetInt(string name)
    {
        if (!IsAlive) return null;
        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            int r = MpvNative.mpv_get_property(_ctx, name, MpvFormat.Int64, buf);
            if (r < 0) return null;
            return Marshal.ReadInt64(buf);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public bool? GetFlag(string name)
    {
        if (!IsAlive) return null;
        IntPtr buf = Marshal.AllocHGlobal(4);
        try
        {
            int r = MpvNative.mpv_get_property(_ctx, name, MpvFormat.Flag, buf);
            if (r < 0) return null;
            return Marshal.ReadInt32(buf) != 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>以 MPV_FORMAT_NODE 读取属性，转换为 string/long/double/bool/List/Dictionary。</summary>
    public object? GetNode(string name)
    {
        if (!IsAlive) return null;
        IntPtr buf = Marshal.AllocHGlobal(16);
        try
        {
            int r = MpvNative.mpv_get_property(_ctx, name, MpvFormat.Node, buf);
            if (r < 0) return null;
            try { return NodeToObject(Marshal.PtrToStructure<MpvNative.MpvNode>(buf)); }
            finally { MpvNative.mpv_free_node_contents(buf); }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static object? NodeToObject(MpvNative.MpvNode n)
    {
        switch (n.format)
        {
            case MpvFormat.String: return Marshal.PtrToStringUTF8(n.ptr);
            case MpvFormat.Flag: return n.flag != 0;
            case MpvFormat.Int64: return n.i64;
            case MpvFormat.Double: return n.dbl;
            case MpvFormat.NodeArray:
            {
                var l = Marshal.PtrToStructure<MpvNative.MpvNodeList>(n.ptr);
                var list = new List<object?>(l.num);
                for (int i = 0; i < l.num; i++)
                    list.Add(NodeToObject(Marshal.PtrToStructure<MpvNative.MpvNode>(l.values + i * 16)));
                return list;
            }
            case MpvFormat.NodeMap:
            {
                var l = Marshal.PtrToStructure<MpvNative.MpvNodeList>(n.ptr);
                var map = new Dictionary<string, object?>(l.num);
                for (int i = 0; i < l.num; i++)
                {
                    string key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(l.keys, i * IntPtr.Size)) ?? "";
                    map[key] = NodeToObject(Marshal.PtrToStructure<MpvNative.MpvNode>(l.values + i * 16));
                }
                return map;
            }
            case MpvFormat.ByteArray:
            {
                var ba = Marshal.PtrToStructure<MpvNative.MpvByteArray>(n.ptr);
                var bytes = new byte[(int)ba.size];
                if (bytes.Length > 0) Marshal.Copy(ba.data, bytes, 0, bytes.Length);
                return bytes;
            }
            default: return null;
        }
    }

    /// <summary>数组形式的命令，适合带文件路径的参数（无需转义）。</summary>
    public int Command(params string[] args)
    {
        if (!IsAlive) return -1;
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            return MpvNative.mpv_command(_ctx, ptrs);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>带返回值的命令（如 screenshot-raw），结果转换方式同 <see cref="GetNode"/>。失败返回 null。</summary>
    public object? CommandWithResult(params string[] args)
    {
        if (!IsAlive) return null;
        var ptrs = new IntPtr[args.Length + 1];
        IntPtr node = Marshal.AllocHGlobal(16);
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            if (MpvNative.mpv_command_ret(_ctx, ptrs, node) < 0) return null;
            try { return NodeToObject(Marshal.PtrToStructure<MpvNative.MpvNode>(node)); }
            finally { MpvNative.mpv_free_node_contents(node); }
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            Marshal.FreeHGlobal(node);
        }
    }

    /// <summary>字符串形式的命令，支持 osd-msg 等前缀和属性展开。</summary>
    public int CommandString(string cmd) => IsAlive ? MpvNative.mpv_command_string(_ctx, cmd) : -1;

    public void ShowText(string text, int ms = 1500)
    {
        // show-text 会展开 ${property}，这里只需要转义引号和反斜杠；换行写成 mpv 引号字符串里的 \n 转义
        string esc = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        CommandString($"show-text \"{esc}\" {ms}");
    }

    public void Observe(string name, MpvFormat format) =>
        MpvNative.mpv_observe_property(_ctx, 0, name, format);

    private void EventLoop()
    {
        while (!_stopping)
        {
            IntPtr evp = MpvNative.mpv_wait_event(_ctx, -1);
            if (_stopping) break;
            var ev = Marshal.PtrToStructure<MpvNative.MpvEvent>(evp);
            try
            {
                switch (ev.event_id)
                {
                    case MpvEventId.None:
                        break;
                    case MpvEventId.Shutdown:
                        Shutdown?.Invoke();
                        return;
                    case MpvEventId.StartFile:
                        StartFile?.Invoke();
                        break;
                    case MpvEventId.FileLoaded:
                        FileLoaded?.Invoke();
                        break;
                    case MpvEventId.EndFile:
                    {
                        var ef = Marshal.PtrToStructure<MpvNative.MpvEventEndFile>(ev.data);
                        EndFile?.Invoke(ef.reason, ef.error);
                        break;
                    }
                    case MpvEventId.VideoReconfig:
                        VideoReconfig?.Invoke();
                        break;
                    case MpvEventId.AudioReconfig:
                        AudioReconfig?.Invoke();
                        break;
                    case MpvEventId.PlaybackRestart:
                        PlaybackRestart?.Invoke();
                        break;
                    case MpvEventId.ClientMessage:
                    {
                        var cm = Marshal.PtrToStructure<MpvNative.MpvEventClientMessage>(ev.data);
                        var args = new string[cm.num_args];
                        for (int i = 0; i < cm.num_args; i++)
                            args[i] = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(cm.args, i * IntPtr.Size)) ?? "";
                        ClientMessage?.Invoke(args);
                        break;
                    }
                    case MpvEventId.PropertyChange:
                    {
                        var pe = Marshal.PtrToStructure<MpvNative.MpvEventProperty>(ev.data);
                        string name = Marshal.PtrToStringUTF8(pe.name) ?? "";
                        object? value = pe.format switch
                        {
                            MpvFormat.Flag => Marshal.ReadInt32(pe.data) != 0,
                            MpvFormat.Int64 => Marshal.ReadInt64(pe.data),
                            MpvFormat.Double => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(pe.data)),
                            MpvFormat.String or MpvFormat.OsdString =>
                                Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(pe.data)),
                            _ => null, // MPV_FORMAT_NONE = 属性当前不可用
                        };
                        PropertyChanged?.Invoke(name, value);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("mpv event handler error: " + ex);
            }
        }
    }

    /// <summary>
    /// 销毁 mpv。不要在 UI 线程上调用：mpv 的子窗口在自己的线程里，
    /// 销毁时可能向父窗口发同步消息，UI 线程阻塞会死锁。
    /// </summary>
    public void Dispose()
    {
        if (_ctx == IntPtr.Zero) return;
        _stopping = true;
        MpvNative.mpv_wakeup(_ctx);
        _eventThread?.Join(2000);
        MpvNative.mpv_terminate_destroy(_ctx);
        _ctx = IntPtr.Zero;
    }
}
