using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace HdrPlayer;

/// <summary>
/// 控制栏上方的悬停提示（进度条的缩略图 + 时间、音量百分比）。
/// 不用 WPF Popup：它的自动定位规则（偏移量会先加到目标位置上再交给自定义回调、
/// 第一次打开时内容还没排版等）很容易摆错位置，而且 Popup 窗口会挡住鼠标。
/// 这里是一个常驻的小窗口：透明、不激活、鼠标穿透（WS_EX_TRANSPARENT），隐藏时只是把不透明度设为 0，
/// 所以每次更新内容后都能同步排版、拿到准确尺寸，再按屏幕坐标一次摆好，不会闪。
/// </summary>
internal sealed class HoverTip : Window
{
    private readonly Image _image;
    private readonly TextBlock _text;
    private FrameworkElement? _anchor;
    private double _anchorX, _gap;

    public HoverTip()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        Left = -10000;
        Top = -10000;
        UseLayoutRounding = true;

        _image = new Image
        {
            MaxWidth = 256, MaxHeight = 144, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 3),
            Visibility = Visibility.Collapsed,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _text = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"), FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(6, 0, 6, 1),
        };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var panel = new StackPanel();
        panel.Children.Add(_image);
        panel.Children.Add(_text);
        var border = new Border
        {
            Padding = new Thickness(4), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorQuarternaryBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "OverlayStrokeBrush");
        Content = border;

        SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE,
                new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT));
        };
    }

    public bool IsShowing => IsVisible && Opacity > 0;

    /// <summary>显示在 anchor 上方，水平中心对准 anchor 内的 x（DIP）。</summary>
    public void ShowAt(FrameworkElement anchor, double x, double gap, string text, ImageSource? image)
    {
        _text.Text = text;
        SetImage(image);
        _anchor = anchor;
        _anchorX = x;
        _gap = gap;
        Place();
    }

    /// <summary>只换缩略图（尺寸会变），位置按上一次的锚点重新计算。</summary>
    public void UpdateImage(ImageSource? image)
    {
        SetImage(image);
        if (IsShowing) Place();
    }

    public void HideTip()
    {
        Opacity = 0;
        _anchor = null;
    }

    private void SetImage(ImageSource? image)
    {
        _image.Source = image;
        _image.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Place()
    {
        var anchor = _anchor;
        if (anchor == null || !anchor.IsVisible || PresentationSource.FromVisual(anchor) is not { CompositionTarget: { } ct })
        {
            HideTip();
            return;
        }
        if (!IsVisible) Show();
        UpdateLayout(); // 同步排版，拿到这次内容的真实尺寸

        // 锚点换算成屏幕上的 DIP 坐标（和本窗口的 Left / Top 同一单位）
        var fromDevice = ct.TransformFromDevice;
        Point origin = fromDevice.Transform(anchor.PointToScreen(new Point(0, 0)));
        Point at = fromDevice.Transform(anchor.PointToScreen(new Point(_anchorX, 0)));
        double w = ActualWidth, h = ActualHeight;
        const double overhang = 60; // 允许超出锚点控件左右两端的距离
        double minLeft = origin.X - overhang, maxLeft = Math.Max(minLeft, origin.X + anchor.ActualWidth - w + overhang);
        Left = Math.Clamp(at.X - w / 2, minLeft, maxLeft);
        Top = at.Y - h - _gap;
        Opacity = 1;
    }
}
