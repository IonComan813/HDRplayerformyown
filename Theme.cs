using System.Runtime.InteropServices;

namespace HdrPlayer;

/// <summary>深色主题：配色、Fluent 图标字体、深色标题栏、深色菜单与提示框。</summary>
internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(14, 14, 16);
    public static readonly Color Bar = Color.FromArgb(24, 24, 27);
    public static readonly Color Panel = Color.FromArgb(22, 22, 25);
    public static readonly Color Surface = Color.FromArgb(36, 36, 40);
    public static readonly Color Hover = Color.FromArgb(48, 48, 54);
    public static readonly Color Border = Color.FromArgb(58, 58, 64);
    public static readonly Color Text = Color.FromArgb(232, 232, 236);
    public static readonly Color Dim = Color.FromArgb(150, 150, 158);
    public static readonly Color Accent = Color.FromArgb(76, 154, 255);
    public static readonly Color Selection = Color.FromArgb(40, 64, 104);
    public static readonly Color HdrOn = Color.FromArgb(255, 196, 0);

    // ---------------- 图标（Windows 11 自带 Segoe Fluent Icons，Win10 回落到 Segoe MDL2 Assets）----------------
    public const string IcoPlay = "", IcoPause = "", IcoPrev = "", IcoNext = "";
    public const string IcoVolume = "", IcoMute = "", IcoList = "", IcoSettings = "";
    public const string IcoFullscreen = "";

    private static string? _iconFamily;

    public static Font IconFont(float size)
    {
        if (_iconFamily == null)
        {
            using var probe = new Font("Segoe Fluent Icons", 10f);
            _iconFamily = probe.Name == "Segoe Fluent Icons" ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
        }
        return new Font(_iconFamily, size);
    }

    public static readonly Font UiFont = new("Microsoft YaHei UI", 9f);

    // ---------------- 深色标题栏 / 深色滚动条 ----------------
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Windows 10 2004+ / Windows 11：标题栏改为深色，边框用主题色。</summary>
    public static void DarkTitleBar(Form f)
    {
        void Apply()
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(f.Handle, 19, ref on, 4);        // 旧版 Windows 10 的编号
            int caption = ToColorRef(Bar);
            DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);       // DWMWA_CAPTION_COLOR（Win11）
        }
        if (f.IsHandleCreated) Apply(); else f.HandleCreated += (_, _) => Apply();
    }

    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    /// <summary>让 ListBox / TextBox 等的滚动条变成深色。</summary>
    public static void DarkScrollBars(Control c)
    {
        void Apply() => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
        if (c.IsHandleCreated) Apply(); else c.HandleCreated += (_, _) => Apply();
    }

    // ---------------- 深色提示框 ----------------
    public static ToolTip CreateToolTip(int initialDelay = 400)
    {
        var tip = new ToolTip { InitialDelay = initialDelay, ReshowDelay = 0, OwnerDraw = true, BackColor = Surface, ForeColor = Text };
        tip.Popup += (_, e) =>
        {
            // SetToolTip 的文字可以直接取；ToolTip.Show(text) 的文字取不到，调用方先放进 Tag
            string text = e.AssociatedControl != null && tip.GetToolTip(e.AssociatedControl) is { Length: > 0 } t
                ? t : tip.Tag as string ?? " ";
            var sz = TextRenderer.MeasureText(text, UiFont);
            e.ToolTipSize = new Size(sz.Width + 16, sz.Height + 10);
        };
        tip.Draw += (_, e) =>
        {
            using var bg = new SolidBrush(Surface);
            using var pen = new Pen(Border);
            e.Graphics.FillRectangle(bg, e.Bounds);
            e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, UiFont, e.Bounds, Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        return tip;
    }

    // ---------------- 深色菜单 ----------------
    public static readonly ToolStripRenderer MenuRenderer = new DarkMenuRenderer();

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Selection;
        public override Color CheckSelectedBackground => Selection;
        public override Color CheckPressedBackground => Selection;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Color.FromArgb(100, 100, 106);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Dim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            using var bg = new SolidBrush(Selection);
            e.Graphics.FillRectangle(bg, Rectangle.Inflate(r, 2, 2));
            TextRenderer.DrawText(e.Graphics, "", IconFont(9f), Rectangle.Inflate(r, 2, 2), Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    public static ContextMenuStrip NewMenu() => new()
    {
        Renderer = MenuRenderer, BackColor = Surface, ForeColor = Text, Font = UiFont, ShowImageMargin = true,
    };

    /// <summary>给对话框里的常用控件统一套上深色样式。</summary>
    public static void StyleTree(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Surface;
                    b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = Hover;
                    b.Cursor = Cursors.Hand;
                    break;
                case TextBox t:
                    t.BackColor = Surface; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                    DarkScrollBars(t);
                    break;
                case NumericUpDown n:
                    n.BackColor = Surface; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Surface; cb.ForeColor = Text;
                    break;
                case CheckBox ch:
                    ch.ForeColor = Text; ch.FlatStyle = FlatStyle.Flat;
                    ch.FlatAppearance.BorderColor = Border;
                    ch.FlatAppearance.CheckedBackColor = Accent;
                    break;
                case ListBox lb:
                    lb.BackColor = Panel; lb.ForeColor = Text;
                    DarkScrollBars(lb);
                    break;
                case Label l when l.ForeColor == SystemColors.ControlText || l.ForeColor == Control.DefaultForeColor:
                    l.ForeColor = Text;
                    break;
            }
            if (c.HasChildren) StyleTree(c);
        }
    }
}
