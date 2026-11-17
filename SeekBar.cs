using System.Drawing.Drawing2D;

namespace HdrPlayer;

/// <summary>扁平风格的进度条/音量条，支持点击和拖动。</summary>
internal sealed class SeekBar : Control
{
    private double _max = 1, _value;
    private bool _dragging, _hover;
    private readonly ToolTip _tip = Theme.CreateToolTip(0);
    // 用 -1 表示“没有显示提示”。之前用 int.MinValue，e.X - int.MinValue 溢出后 Math.Abs 抛 OverflowException
    private int _lastTipX = -1;

    public Color TrackColor { get; set; } = Color.FromArgb(70, 70, 70);
    public Color FillColor { get; set; } = Theme.Accent;

    /// <summary>(值, 是否为最终值——鼠标松开时为 true)</summary>
    public event Action<double, bool>? ValueRequested;

    /// <summary>悬停提示文字；为 null 时不显示提示。</summary>
    public Func<double, string>? TipFormatter { get; set; }

    public SeekBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
        BackColor = Theme.Bar;
        TabStop = false;
    }

    public double Maximum
    {
        get => _max;
        set { _max = Math.Max(value, 0.0001); Invalidate(); }
    }

    public double Value
    {
        get => _value;
        set
        {
            if (_dragging) return; // 拖动时不被播放进度覆盖
            double v = Math.Clamp(value, 0, _max);
            if (Math.Abs(v - _value) < 1e-6) return;
            _value = v;
            Invalidate();
        }
    }

    private double ValueAt(int x)
    {
        int pad = Height / 2;
        double frac = (x - pad) / (double)Math.Max(1, Width - pad * 2);
        return Math.Clamp(frac, 0, 1) * _max;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = Height / 2;
        int h = (_hover || _dragging) ? 6 : 4;
        int y = (Height - h) / 2;
        int w = Math.Max(1, Width - pad * 2);
        using var track = new SolidBrush(TrackColor);
        using var fill = new SolidBrush(Enabled ? FillColor : TrackColor);
        g.FillRectangle(track, pad, y, w, h);
        int fw = (int)(w * (_value / _max));
        g.FillRectangle(fill, pad, y, fw, h);
        if (_hover || _dragging)
        {
            int r = 7;
            g.FillEllipse(Brushes.White, pad + fw - r, Height / 2 - r, r * 2, r * 2);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false; _tip.Hide(this); _lastTipX = -1; Invalidate(); base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        _value = ValueAt(e.X);
        Invalidate();
        ValueRequested?.Invoke(_value, false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (TipFormatter != null && (_lastTipX < 0 || Math.Abs(e.X - _lastTipX) > 2))
        {
            _lastTipX = e.X;
            string text = TipFormatter(ValueAt(e.X));
            _tip.Tag = text;
            _tip.Show(text, this, e.X + 12, -30, 1500);
        }
        if (!_dragging) return;
        _value = ValueAt(e.X);
        Invalidate();
        ValueRequested?.Invoke(_value, false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        _value = ValueAt(e.X);
        Invalidate();
        ValueRequested?.Invoke(_value, true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}
