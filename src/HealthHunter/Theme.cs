using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HealthHunter;

/// <summary>
/// v5 dark theme for plain WinForms: one palette, a recursive Apply() that styles the standard controls,
/// dark title bars and scrollbars through the Windows 10/11 dark-mode themes, and a dark menu renderer.
/// </summary>
public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(17, 19, 24);
    public static readonly Color Sidebar = Color.FromArgb(22, 25, 31);
    public static readonly Color Surface = Color.FromArgb(29, 32, 40);
    public static readonly Color Surface2 = Color.FromArgb(38, 42, 52);
    public static readonly Color SurfaceHover = Color.FromArgb(48, 53, 65);
    public static readonly Color Border = Color.FromArgb(52, 57, 70);
    public static readonly Color Text = Color.FromArgb(230, 232, 238);
    public static readonly Color TextDim = Color.FromArgb(150, 157, 172);
    public static readonly Color Accent = Color.FromArgb(79, 140, 255);
    public static readonly Color AccentHover = Color.FromArgb(105, 158, 255);
    public static readonly Color AccentDim = Color.FromArgb(40, 64, 110);
    public static readonly Color Success = Color.FromArgb(63, 185, 80);
    public static readonly Color SuccessDim = Color.FromArgb(28, 70, 40);
    public static readonly Color Warning = Color.FromArgb(222, 166, 52);
    public static readonly Color Danger = Color.FromArgb(240, 85, 75);

    // Design fonts: what each kind of text looks like at 100 % scaling. Controls get them as their Font and WinForms
    // rescales them per monitor; painted text uses Derive() so it follows the painting control's (rescaled) font.
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Strong = new("Segoe UI Semibold", 9.75f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 12f);
    public static readonly Font PageTitle = new("Segoe UI Semibold", 17f);
    public static readonly Font Big = new("Segoe UI Semibold", 22f);
    public static readonly Font Mono = new("Consolas", 9.75f);

    private static readonly bool HasIconFont = FontFamily.Families.Any(f => f.Name == "Segoe MDL2 Assets");
    public static readonly Font Icons = HasIconFont ? new Font("Segoe MDL2 Assets", 11f) : Body;
    /// <summary>Segoe MDL2 glyph, or a plain fallback when the icon font is missing.</summary>
    public static string Glyph(char g, string fallback = "•") => HasIconFont ? g.ToString() : fallback;

    // ------------------------------------------------------------------ scaling

    /// <summary>
    /// DPI at which GDI turns a font's point size into pixels in this process (the system DPI, fixed at sign-in).
    /// TextRenderer, Label and Font.Height all use it. Under PerMonitorV2, when a window sits on a monitor with a
    /// different DPI, WinForms changes the point size of the control fonts instead, so points × this / 72 is the
    /// size the text really has on screen.
    /// </summary>
    private static readonly Lazy<float> s_gdiDpi = new(() =>
    {
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); return g.DpiY; }
        catch { return 96f; }
    });

    /// <summary>Height of one em of <paramref name="f"/> in pixels, as it is drawn.</summary>
    public static float EmPixels(Font f) => f.SizeInPoints * s_gdiDpi.Value / 72f;

    /// <summary>
    /// Layout scale for things drawn next to text in <paramref name="f"/>, a font that was designed at
    /// <paramref name="designPoints"/>: 1 at 100 %, 1.5 at 150 %, 2 at 200 %. It is read from the font itself, so
    /// sizes built with it stay in proportion with the text whatever scaling Windows and WinForms applied.
    /// </summary>
    public static float ScaleOf(Font f, float designPoints = 9.75f) =>
        f == null ? 1f : EmPixels(f) / (designPoints * 96f / 72f);

    /// <summary>Rounds a 100 % design length to pixels at scale <paramref name="k"/>.</summary>
    public static int Px(float k, float designPixels) => (int)Math.Round(designPixels * k);

    private static readonly ConditionalWeakTable<Font, Dictionary<(Font, float), Font>> s_derived = new();

    /// <summary>
    /// <paramref name="design"/>'s family and style, sized relative to <paramref name="basis"/>: a 12 pt card title on
    /// a card whose font is the 9.75 pt body font becomes basis × 12 / 9.75, at whatever size WinForms gave that font
    /// for the current monitor. Cached per basis font instance, so it is cheap to call while painting.
    /// </summary>
    public static Font Derive(Font basis, Font design, float basisDesignPoints = 9.75f)
    {
        if (basis == null) return design;
        var map = s_derived.GetValue(basis, _ => new Dictionary<(Font, float), Font>());
        if (!map.TryGetValue((design, basisDesignPoints), out var f))
        {
            f = new Font(design.FontFamily, basis.SizeInPoints * design.SizeInPoints / basisDesignPoints, design.Style, GraphicsUnit.Point);
            map[(design, basisDesignPoints)] = f;
        }
        return f;
    }

    // ------------------------------------------------------------------ native dark mode

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

    public static void DarkTitleBar(Form f)
    {
        void Set()
        {
            int on = 1;
            try
            {
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch { }
        }
        if (f.IsHandleCreated) Set(); else f.HandleCreated += (_, _) => Set();
    }

    private static void DarkScrollbars(Control c)
    {
        void Set() { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
        if (c.IsHandleCreated) Set(); else c.HandleCreated += (_, _) => Set();
    }

    // ------------------------------------------------------------------ apply

    /// <summary>Styles a control tree. BackColor/ForeColor are ambient, so containers mostly inherit.</summary>
    public static void Apply(Control root)
    {
        if (root is Form f)
        {
            f.BackColor = Bg;
            f.ForeColor = Text;
            f.Font = Body;
            DarkTitleBar(f);
        }
        Style(root);
        DesignScale.Track(root);
        root.ControlAdded += (_, e) => Apply(e.Control);
        foreach (Control c in root.Controls) Apply(c);
    }

    private static void Style(Control c)
    {
        switch (c)
        {
            case ModernButton:
                break;
            case TextBox t:
                t.BackColor = Surface2; t.ForeColor = Text; t.BorderStyle = BorderStyle.FixedSingle;
                if (t.Multiline) DarkScrollbars(t);
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat; cb.BackColor = Surface2; cb.ForeColor = Text;
                DarkScrollbars(cb);
                break;
            case NumericUpDown n:
                n.BackColor = Surface2; n.ForeColor = Text; n.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckBox chk when chk.Appearance == Appearance.Normal:
                chk.FlatStyle = FlatStyle.Flat;
                chk.FlatAppearance.BorderColor = Border;
                chk.FlatAppearance.CheckedBackColor = Accent;
                chk.ForeColor = Text;
                break;
            case ListBox lb:
                lb.BackColor = Surface2; lb.ForeColor = Text; lb.BorderStyle = BorderStyle.None;
                DarkScrollbars(lb);
                break;
            case ListView lv:
                StyleListView(lv);
                break;
            case DataGridView g:
                StyleGrid(g);
                break;
            case PictureBox pb:
                pb.BackColor = Bg; pb.BorderStyle = BorderStyle.None;
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Surface2; b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Border; b.FlatAppearance.MouseOverBackColor = SurfaceHover;
                break;
        }
        if (c.ContextMenuStrip != null) c.ContextMenuStrip.Renderer = new DarkMenuRenderer();
    }

    private static void StyleListView(ListView lv)
    {
        lv.BackColor = Surface; lv.ForeColor = Text; lv.BorderStyle = BorderStyle.None;
        lv.OwnerDraw = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using (var bg = new SolidBrush(Surface2)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var line = new Pen(Border)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            // Header text follows the body font around the list (some lists use a monospace font for their rows).
            var basis = lv.Parent?.Font ?? lv.Font;
            float k = ScaleOf(basis);
            var r = new Rectangle(e.Bounds.X + Px(k, 8), e.Bounds.Y, e.Bounds.Width - Px(k, 10), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Derive(basis, Strong), r, TextDim,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        };
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        DarkScrollbars(lv);
    }

    private static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = Surface; g.GridColor = Border; g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.DefaultCellStyle.BackColor = Surface;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = AccentDim;
        g.DefaultCellStyle.SelectionForeColor = Text;
        g.ColumnHeadersDefaultCellStyle.BackColor = Surface2;
        g.ColumnHeadersDefaultCellStyle.ForeColor = TextDim;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Surface2;
        FitGrid(g);
        // The grid inherits its font, which WinForms rescales on DPI changes: re-fit header and rows to it.
        g.FontChanged += (_, _) => FitGrid(g);
        DarkScrollbars(g);
    }

    /// <summary>Header font, header height, row height and cell padding from the grid's current font.</summary>
    private static void FitGrid(DataGridView g)
    {
        float k = ScaleOf(g.Font);
        var header = Derive(g.Font, Strong);
        g.ColumnHeadersDefaultCellStyle.Font = header;
        g.DefaultCellStyle.Padding = new Padding(Px(k, 4), 0, Px(k, 4), 0);
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(Px(k, 4), 0, Px(k, 4), 0);
        g.ColumnHeadersHeight = Math.Max(Px(k, 34), TextRenderer.MeasureText("Ag", header).Height + Px(k, 12));
        int row = Math.Max(Px(k, 30), TextRenderer.MeasureText("Ag", g.Font).Height + Px(k, 10));
        g.RowTemplate.Height = row;
        foreach (DataGridViewRow r in g.Rows) if (r.Height != row) r.Height = row;
    }

    // ------------------------------------------------------------------ drawing helpers

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Blend(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

/// <summary>Dark menus for right-click menus.</summary>
public sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.TextDim;
        base.OnRenderArrow(e);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface2;
        public override Color ImageMarginGradientBegin => Theme.Surface2;
        public override Color ImageMarginGradientMiddle => Theme.Surface2;
        public override Color ImageMarginGradientEnd => Theme.Surface2;
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Theme.AccentDim;
        public override Color MenuItemSelected => Theme.AccentDim;
        public override Color MenuItemSelectedGradientBegin => Theme.AccentDim;
        public override Color MenuItemSelectedGradientEnd => Theme.AccentDim;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
