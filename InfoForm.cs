namespace HdrPlayer;

/// <summary>“视频参数”窗口：每秒刷新一次（HDR10+ 场景元数据、丢帧数等会随播放变化）。</summary>
internal sealed class InfoForm : Form
{
    private readonly Func<string> _builder;
    private readonly TextBox _text;
    private readonly CheckBox _auto;
    private readonly System.Windows.Forms.Timer _timer;

    public InfoForm(Func<string> builder)
    {
        _builder = builder;
        Text = "视频参数";
        Size = new Size(820, 760);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        KeyPreview = true;
        ShowInTaskbar = false;

        _text = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical,
            BackColor = Theme.Back, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
            Font = new Font("Sarasa Mono SC", 10f) is { Name: "Sarasa Mono SC" } f ? f : new Font("Consolas", 10f),
            WordWrap = false,
        };
        // Consolas 没有中文，系统会自动回落到中文字体；用等宽中文字体（如更纱黑体）对齐效果更好

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6), BackColor = Theme.Bar,
        };
        var close = new Button { Text = "关闭", AutoSize = true, FlatStyle = FlatStyle.Flat };
        close.Click += (_, _) => Close();
        var copy = new Button { Text = "复制", AutoSize = true, FlatStyle = FlatStyle.Flat };
        copy.Click += (_, _) => { if (_text.Text.Length > 0) Clipboard.SetText(_text.Text); };
        var refresh = new Button { Text = "刷新", AutoSize = true, FlatStyle = FlatStyle.Flat };
        refresh.Click += (_, _) => RefreshText();
        _auto = new CheckBox { Text = "自动刷新", Checked = true, AutoSize = true, Margin = new Padding(3, 6, 12, 0) };
        bottom.Controls.AddRange(new Control[] { close, copy, refresh, _auto });

        Controls.Add(_text);
        Controls.Add(bottom);
        Theme.StyleTree(this);
        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = Theme.Back;
        Theme.DarkTitleBar(this);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => { if (_auto.Checked) RefreshText(); };
        Shown += (_, _) => { RefreshText(); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }

    public void RefreshText()
    {
        string s;
        try { s = _builder(); } catch (Exception ex) { s = "读取失败：" + ex.Message; }
        if (s == _text.Text) return;
        int sel = _text.SelectionStart;
        int firstLine = _text.GetLineFromCharIndex(_text.GetCharIndexFromPosition(new Point(2, 2)));
        _text.Text = s;
        _text.SelectionStart = Math.Min(sel, s.Length);
        // 保持滚动位置
        if (firstLine > 0)
        {
            int idx = _text.GetFirstCharIndexFromLine(Math.Min(firstLine, _text.Lines.Length - 1));
            if (idx >= 0) { _text.SelectionStart = idx; _text.ScrollToCaret(); }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
