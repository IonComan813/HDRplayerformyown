using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
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
        SeekSlider.MouseLeave += (_, _) => SeekTip.Visibility = Visibility.Collapsed;

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
        double v = track.ValueFromPoint(e.GetPosition(track));
        SeekTipText.Text = FormatTime(Math.Clamp(v, 0, SeekSlider.Maximum));
        SeekTip.Visibility = Visibility.Visible;
        SeekTip.UpdateLayout();
        double x = e.GetPosition(SeekSlider).X - SeekTip.ActualWidth / 2;
        Canvas.SetLeft(SeekTip, Math.Clamp(x, -40, SeekSlider.ActualWidth - SeekTip.ActualWidth + 40));
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
        SeekTip.Visibility = Visibility.Collapsed;
        Fade(0, 250, () => { if (!IsRevealed) Hide(); });
    }

    /// <summary>立即隐藏（全屏切换、窗口关闭等）。</summary>
    public void HideNow()
    {
        IsRevealed = false;
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
