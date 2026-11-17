using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace HdrPlayer;

/// <summary>
/// “视频参数”窗口。live：当前播放的文件，每秒刷新一次（HDR10+ 场景元数据、丢帧数等会随播放变化）；
/// 否则是播放列表里其它文件的一次性探测结果，由调用方在读完后调用 <see cref="RefreshText"/>。
/// </summary>
public partial class InfoWindow : FluentWindow
{
    private readonly Func<string> _builder;
    private readonly DispatcherTimer _timer;

    public InfoWindow(Func<string> builder, string title = "视频参数", bool live = true)
    {
        _builder = builder;
        InitializeComponent();
        Title = title;
        InfoTitleBar.Title = title;
        if (!live)
        {
            AutoRefresh.IsChecked = false;
            AutoRefresh.Visibility = Visibility.Collapsed;
        }

        BtnClose.Click += (_, _) => Close();
        BtnCopy.Click += (_, _) => { if (InfoText.Text.Length > 0) Clipboard.SetText(InfoText.Text); };
        BtnRefresh.Click += (_, _) => RefreshText();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { if (AutoRefresh.IsChecked == true) RefreshText(); };
        Loaded += (_, _) => { RefreshText(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    public void RefreshText()
    {
        string s;
        try { s = _builder(); } catch (Exception ex) { s = "读取失败：" + ex.Message; }
        if (s == InfoText.Text) return;
        // 保持滚动位置和选区
        int selStart = InfoText.SelectionStart, selLen = InfoText.SelectionLength;
        double v = InfoText.VerticalOffset, h = InfoText.HorizontalOffset;
        InfoText.Text = s;
        if (selStart + selLen <= s.Length) InfoText.Select(selStart, selLen);
        InfoText.ScrollToVerticalOffset(v);
        InfoText.ScrollToHorizontalOffset(h);
    }
}
