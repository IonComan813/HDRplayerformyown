using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HdrPlayer;

/// <summary>
/// 悬浮控制栏：一个无边框、透明、不抢焦点的小窗口，压在视频画面底部。
/// （mpv 画面是原生子窗口，WPF 内容没法直接叠在它上面，所以控制栏只能是单独的顶层窗口。）
/// </summary>
public partial class ControlBar : Window
{
    private bool _seekDragging, _settingSeek, _settingVolume;

    /// <summary>(位置秒数, 是否最终位置) —— 拖动过程中按关键帧跳，松手 / 单击时精确跳。</summary>
    public event Action<double, bool>? SeekRequested;
    public event Action<double>? VolumeRequested;
    /// <summary>鼠标悬停在进度条上某个时间点，需要它的缩略图。</summary>
    public event Action<double>? ThumbnailRequested;
    /// <summary>控制栏万一拿到了键盘输入，转给主窗口处理（空格、方向键等）。</summary>
    public event Action<KeyEventArgs>? KeyForwarded;

    /// <summary>当前文件能否预览缩略图（有画面的本地文件，且设置里开启）。</summary>
    public bool ThumbnailsEnabled
    {
        get => _thumbsEnabled;
        set { _thumbsEnabled = value; if (!value) HideThumbnail(); }
    }
    /// <summary>缩略图时间和悬停位置相差超过这么多秒，就调暗显示（表示是旧图，新图还在生成）。</summary>
    private const double ThumbnailExactRange = 0.75;

    private bool _thumbsEnabled, _muted;
    private double _hoverValue = double.NaN, _thumbTime = double.NaN;
    public Func<double, string>? FormatTime { get; set; }

    public bool IsRevealed { get; private set; }
    public bool IsSeekDragging => _seekDragging;
    public IntPtr Handle { get; private set; }

    public ControlBar()
    {
        InitializeComponent();

        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _seekDragging = true));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _seekDragging = false;
            SeekRequested?.Invoke(SeekSlider.Value, true);
        }));
        SeekSlider.ValueChanged += (_, e) =>
        {
            if (_settingSeek) return;
            // 拖动中：快速跳到关键帧；单击轨道（IsMoveToPointEnabled）：精确跳转
            SeekRequested?.Invoke(e.NewValue, !_seekDragging);
        };
        SeekSlider.MouseMove += (_, e) => UpdateSeekTip(e);
        SeekSlider.MouseLeave += (_, _) => { if (!_seekDragging) CloseTip(TipOwner.Seek); };
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            if (!SeekSlider.IsMouseOver) CloseTip(TipOwner.Seek);
        }));

        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (_tipOwner == TipOwner.Volume) ShowVolumeTip();
            if (!_settingVolume) VolumeRequested?.Invoke(Math.Round(e.NewValue));
        };
        VolumeSlider.MouseEnter += (_, _) => ShowVolumeTip();
        VolumeSlider.MouseLeave += (_, _) => { if (!VolumeSlider.IsMouseCaptureWithin) CloseTip(TipOwner.Volume); };
        VolumeSlider.LostMouseCapture += (_, _) => { if (!VolumeSlider.IsMouseOver) CloseTip(TipOwner.Volume); };
        VolumeSlider.MouseWheel += (_, e) =>
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + Math.Sign(e.Delta) * 5, 0, VolumeSlider.Maximum);
            e.Handled = true;
        };

        // 控制栏里的控件一律不拿键盘焦点：WPF 给某个元素键盘焦点时会对它所在的窗口调用 SetFocus，
        // 这会把控制栏窗口激活、主窗口失去焦点，空格等快捷键就失效了。在 PreviewGotKeyboardFocus 里取消，
        // WPF 就不会走到 SetFocus 这一步。
        PreviewGotKeyboardFocus += (_, e) => e.Handled = true;
        // 万一控制栏还是收到了按键，转给主窗口处理
        PreviewKeyDown += (_, e) => KeyForwarded?.Invoke(e);

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
            // 点击时明确告诉系统“不激活”：焦点留在主窗口 / mpv，快捷键照常可用。
            // 注意不能在被激活后再去激活主窗口：那会发生在按下鼠标的过程中，打断按钮 / 滑块的鼠标捕获，点击全部失效。
            HwndSource.FromHwnd(Handle)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
            {
                if (msg != Native.WM_MOUSEACTIVATE) return IntPtr.Zero;
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            });
        };
    }

    // ------------------------------------------------------------------ 状态更新

    public void SetDuration(double seconds)
    {
        _settingSeek = true;
        SeekSlider.Maximum = Math.Max(seconds, 0.001);
        _settingSeek = false;
        DurationText.Text = FormatTime?.Invoke(seconds) ?? "";
    }

    public void SetPosition(double seconds)
    {
        TimeText.Text = FormatTime?.Invoke(seconds) ?? "";
        if (_seekDragging) return; // 拖动时不被播放进度覆盖
        _settingSeek = true;
        SeekSlider.Value = Math.Clamp(seconds, 0, SeekSlider.Maximum);
        _settingSeek = false;
    }

    public void SetVolume(double v)
    {
        if (VolumeSlider.IsMouseCaptureWithin) return;
        _settingVolume = true;
        VolumeSlider.Value = v;
        _settingVolume = false;
    }

    public void SetMuted(bool muted)
    {
        _muted = muted;
        if (_tipOwner == TipOwner.Volume) ShowVolumeTip();
    }

    // ------------------------------------------------------------------ 悬停提示（进度条 / 音量）

    private enum TipOwner { None, Seek, Volume }
    private TipOwner _tipOwner;
    private HoverTip? _tip;

    /// <summary>提示窗口归控制栏所有（显示在它上面）；Owner 要在控制栏显示过之后才能设置，所以第一次用到时再创建。</summary>
    private HoverTip? Tip => _tip ??= IsVisible ? new HoverTip { Owner = this } : null;

    private void ShowVolumeTip()
    {
        if (Tip is not { } tip) return;
        int v = (int)Math.Round(VolumeSlider.Value);
        // 锚点放在滑块圆点的位置（WPF-UI 的圆点宽 20，轨道两端各让出半个圆点）
        const double thumb = 20;
        double x = thumb / 2 + Math.Max(0, VolumeSlider.ActualWidth - thumb) * VolumeSlider.Value / Math.Max(1, VolumeSlider.Maximum);
        _tipOwner = TipOwner.Volume;
        tip.ShowAt(VolumeSlider, x, 6, _muted ? $"静音（{v}%）" : $"{v}%", null, false);
    }

    private void UpdateSeekTip(MouseEventArgs e)
    {
        if (FormatTime == null || Tip is not { } tip ||
            SeekSlider.Template.FindName("PART_Track", SeekSlider) is not Track track)
            return;
        double v = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), 0, SeekSlider.Maximum);
        _hoverValue = v;
        // 新位置的缩略图还没出来时，先把上一张调暗留着（不撤掉，免得提示框大小跳来跳去）
        if (!_thumbsEnabled) _thumbImage = null;
        else ThumbnailRequested?.Invoke(v);
        _tipOwner = TipOwner.Seek;
        tip.ShowAt(SeekSlider, e.GetPosition(SeekSlider).X, 10, FormatTime(v), _thumbImage, IsThumbStale());
    }

    private ImageSource? _thumbImage;

    private bool IsThumbStale() =>
        _thumbImage != null && (double.IsNaN(_thumbTime) || Math.Abs(_thumbTime - _hoverValue) > ThumbnailExactRange);

    /// <summary>缩略图线程送来的结果（UI 线程调用）。servedAt 是实际截图的时间点。</summary>
    public void ShowThumbnail(double servedAt, ImageSource image)
    {
        if (!_thumbsEnabled || _tipOwner != TipOwner.Seek || double.IsNaN(_hoverValue)) return;
        _thumbImage = image;
        _thumbTime = servedAt;
        _tip?.UpdateImage(image, IsThumbStale());
    }

    private void HideThumbnail()
    {
        _thumbImage = null;
        _thumbTime = double.NaN;
        if (_tipOwner == TipOwner.Seek) _tip?.UpdateImage(null, false);
    }

    /// <summary>关闭提示；owner 不为 None 时只在提示属于它时才关（免得进度条的离开事件关掉音量提示）。</summary>
    private void CloseTip(TipOwner owner = TipOwner.None)
    {
        if (owner != TipOwner.None && _tipOwner != owner) return;
        _tipOwner = TipOwner.None;
        _hoverValue = double.NaN;
        _thumbImage = null;
        _thumbTime = double.NaN;
        _tip?.HideTip();
    }

    // ------------------------------------------------------------------ 显示 / 隐藏（淡入淡出）

    public void Reveal()
    {
        if (IsRevealed) return;
        IsRevealed = true;
        if (!IsVisible) Show();
        Fade(1, 120, null);
    }

    public void Conceal()
    {
        if (!IsRevealed) return;
        IsRevealed = false;
        CloseTip();
        Fade(0, 250, () => { if (!IsRevealed) Hide(); });
    }

    /// <summary>立即隐藏（全屏切换、窗口关闭等）。</summary>
    public void HideNow()
    {
        IsRevealed = false;
        CloseTip();
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Hide();
    }

    private void Fade(double to, int ms, Action? done)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (done != null) anim.Completed += (_, _) => done();
        Root.BeginAnimation(OpacityProperty, anim);
    }
}
