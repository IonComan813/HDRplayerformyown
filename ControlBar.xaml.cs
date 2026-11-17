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

    /// <summary>当前文件能否预览缩略图（有画面的本地文件，且设置里开启）。</summary>
    public bool ThumbnailsEnabled
    {
        get => _thumbsEnabled;
        set { _thumbsEnabled = value; if (!value) HideThumbnail(); }
    }
    /// <summary>缩略图的实际时间和悬停位置相差多少秒以内才显示（按片长设置）。</summary>
    public double ThumbnailTolerance { get; set; } = 5;

    private bool _thumbsEnabled;
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
        SeekSlider.MouseLeave += (_, _) => { if (!_seekDragging) CloseSeekTip(); };
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            if (!SeekSlider.IsMouseOver) CloseSeekTip();
        }));

        VolumeSlider.ValueChanged += (_, e) =>
        {
            if (!_settingVolume) VolumeRequested?.Invoke(Math.Round(e.NewValue));
        };
        VolumeSlider.MouseWheel += (_, e) =>
        {
            VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + Math.Sign(e.Delta) * 5, 0, VolumeSlider.Maximum);
            e.Handled = true;
        };

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            // 不激活：点控制栏时焦点留在主窗口 / mpv，快捷键照常可用
            long ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
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

    private void UpdateSeekTip(MouseEventArgs e)
    {
        if (FormatTime == null || SeekSlider.Template.FindName("PART_Track", SeekSlider) is not Track track)
            return;
        double v = Math.Clamp(track.ValueFromPoint(e.GetPosition(track)), 0, SeekSlider.Maximum);
        _hoverValue = v;
        SeekTipText.Text = FormatTime(v);
        if (_thumbsEnabled)
        {
            // 离已显示的缩略图太远就先藏起来，等新的出来；请求会被缩略图线程合并，只处理最新的
            if (!double.IsNaN(_thumbTime) && Math.Abs(_thumbTime - v) > ThumbnailTolerance) HideThumbnail();
            ThumbnailRequested?.Invoke(v);
        }
        SeekPopup.IsOpen = true;
        PlaceSeekTip(e.GetPosition(SeekSlider).X);
    }

    private double _tipX;

    private void PlaceSeekTip(double x)
    {
        _tipX = x;
        SeekTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = SeekTip.DesiredSize;
        SeekPopup.HorizontalOffset = Math.Clamp(x - size.Width / 2, -60, Math.Max(-60, SeekSlider.ActualWidth - size.Width + 60));
        SeekPopup.VerticalOffset = -size.Height - 14;
    }

    /// <summary>缩略图线程送来的结果（UI 线程调用）。servedAt 是实际截图的时间点。</summary>
    public void ShowThumbnail(double servedAt, ImageSource image)
    {
        if (!_thumbsEnabled || !SeekPopup.IsOpen || double.IsNaN(_hoverValue)) return;
        if (Math.Abs(servedAt - _hoverValue) > ThumbnailTolerance) return; // 鼠标已经移走了
        SeekThumb.Source = image;
        SeekThumb.Visibility = Visibility.Visible;
        _thumbTime = servedAt;
        PlaceSeekTip(_tipX);
    }

    private void HideThumbnail()
    {
        SeekThumb.Visibility = Visibility.Collapsed;
        SeekThumb.Source = null;
        _thumbTime = double.NaN;
    }

    private void CloseSeekTip()
    {
        SeekPopup.IsOpen = false;
        _hoverValue = double.NaN;
        HideThumbnail();
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
        CloseSeekTip();
        Fade(0, 250, () => { if (!IsRevealed) Hide(); });
    }

    /// <summary>立即隐藏（全屏切换、窗口关闭等）。</summary>
    public void HideNow()
    {
        IsRevealed = false;
        CloseSeekTip();
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
