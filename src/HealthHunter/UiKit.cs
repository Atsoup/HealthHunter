using System.Drawing;
using System.Drawing.Drawing2D;

namespace HealthHunter;

// Sizes in this file are 100 % design values. Anything painted derives its fonts from the control's own Font
// (which WinForms keeps right for the monitor) and its pixel sizes from Theme.ScaleOf(Font) or from measured
// text, so painted parts stay in proportion with the text at any scaling and are recomputed when the font changes.

public enum ButtonVariant { Secondary, Primary, Danger, Ghost }

/// <summary>Rounded, flat button that follows the theme. Drop-in replacement for Button.</summary>
public class ModernButton : Button
{
    private bool _hover, _down;
    private ButtonVariant _variant;

    public ButtonVariant Variant { get => _variant; set { _variant = value; Invalidate(); } }

    public ModernButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Padding = new Padding(12, 5, 12, 5);
        Margin = new Padding(0, 3, 8, 3);
        MinimumSize = new Size(0, 32);
        Font = Theme.Strong;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    private float K => Theme.ScaleOf(Font);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    public override Size GetPreferredSize(Size proposed)
    {
        var s = TextRenderer.MeasureText(Text, Font);
        // Honour MinimumSize in both directions so a flow layout reserves the width the button really takes.
        return new Size(Math.Max(MinimumSize.Width, s.Width + Padding.Horizontal + Theme.Px(K, 4)),
                        Math.Max(MinimumSize.Height, s.Height + Padding.Vertical));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var parentBg = Parent?.BackColor ?? Theme.Bg;
        g.Clear(parentBg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill, text = Theme.Text, border = Color.Empty;
        switch (_variant)
        {
            case ButtonVariant.Primary: fill = _down ? Theme.Accent : _hover ? Theme.AccentHover : Theme.Accent; text = Color.White; break;
            case ButtonVariant.Danger: fill = _hover ? Theme.Blend(Theme.Danger, Color.White, 0.12) : Theme.Danger; text = Color.White; break;
            case ButtonVariant.Ghost: fill = _hover ? Theme.Surface2 : parentBg; break;
            default: fill = _down ? Theme.Surface2 : _hover ? Theme.SurfaceHover : Theme.Surface2; border = Theme.Border; break;
        }
        if (!Enabled) { fill = Theme.Blend(fill, parentBg, 0.55); text = Theme.TextDim; }
        float k = K;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.Round(r, 6 * k))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (border != Color.Empty) using (var p = new Pen(border)) g.DrawPath(p, path);
        }
        if (Focused && ShowFocusCues)
        {
            float pw = 1.5f * k, inset = pw / 2 + 0.75f;
            using var p = new Pen(Theme.Accent, pw);
            using var path = Theme.Round(new RectangleF(inset, inset, Width - 2 * inset - 0.5f, Height - 2 * inset - 0.5f), 5 * k);
            g.DrawPath(p, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>Multi-line label that sizes its own height to fit its text at its current width (use with Dock = Top).</summary>
public sealed class TextBlock : Label, ISelfScaling
{
    bool ISelfScaling.OwnsHeight => true;

    public TextBlock()
    {
        AutoSize = false;
        Dock = DockStyle.Top;
        Padding = new Padding(0, 2, 0, 6);
        UseMnemonic = false;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Fit(); }
    protected override void OnPaddingChanged(EventArgs e) { base.OnPaddingChanged(e); Fit(); }

    private bool _fitting;
    private void Fit()
    {
        if (_fitting || Width <= 0) return;
        _fitting = true;
        var s = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(Math.Max(1, Width - Padding.Horizontal), int.MaxValue),
                                         TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        int h = s.Height + Padding.Vertical;
        if (Height != h) Height = h;
        _fitting = false;
    }
}

/// <summary>Rounded panel with an optional title. Children dock inside its padding.</summary>
public class Card : Panel, ISelfScaling
{
    private string _title = "";
    public string Title { get => _title; set { _title = value ?? ""; UpdatePadding(); Invalidate(); } }
    public string Subtitle { get; set; }

    bool ISelfScaling.OwnsPadding => true;
    bool ISelfScaling.OwnsHeight => FitContent;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Margin = new Padding(0);
        UpdatePadding();
    }

    /// <summary>Scale of this card's (inherited, DPI-correct) font relative to the 100 % body font.</summary>
    protected float K => Theme.ScaleOf(Font);
    protected int Px(float designPixels) => Theme.Px(K, designPixels);
    protected Font TitleFont => Theme.Derive(Font, Theme.CardTitle);
    protected int TitleTop => Px(15);
    protected int TitleHeight => TextRenderer.MeasureText(_title.Length > 0 ? _title : "Ag", TitleFont).Height;
    /// <summary>Left edge of the title text.</summary>
    protected virtual int TitleLeft => Px(18);
    /// <summary>Lowest point of what is drawn in the title row; the children start below it.</summary>
    protected virtual int HeaderBottom => TitleTop + TitleHeight;

    private void UpdatePadding()
    {
        // 50 px at 100 %, or more if the title (measured in the font actually used) needs it.
        int top = _title.Length > 0 ? Math.Max(Px(50), HeaderBottom + Px(13)) : Px(16);
        var p = new Padding(Px(18), top, Px(18), Px(16));
        if (Padding != p) Padding = p;
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdatePadding(); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdatePadding(); Invalidate(); }

    /// <summary>Grow or shrink to fit the docked children (use with Dock = Top).</summary>
    public bool FitContent { get; set; }

    /// <summary>Height the docked children need (padding plus every visible top/bottom child).</summary>
    public int ContentHeight
    {
        get
        {
            int h = Padding.Vertical;
            foreach (Control c in Controls)
                if (c.Visible && c.Dock is DockStyle.Top or DockStyle.Bottom) h += c.Height;
            return h;
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (!FitContent || !Visible) return; // hidden parents report every child as hidden
        int h = ContentHeight;
        if (Height != h) Height = h;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && FitContent) PerformLayout();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10 * K);
        using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
        using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_title.Length == 0) return;
        var r = new Rectangle(TitleLeft, TitleTop, Math.Max(1, Width - TitleLeft - Px(18)), TitleHeight);
        TextRenderer.DrawText(e.Graphics, _title, TitleFont, r, Theme.Text,
                              TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

public enum StepState { Pending, Active, Done }

/// <summary>A numbered step in the Quick start flow: number badge, title, and a done / current / to-do state.</summary>
public sealed class StepCard : Card
{
    private StepState _state;
    public int Number { get; set; }
    public StepState State { get => _state; set { if (_state != value) { _state = value; Invalidate(); } } }

    private Font NumberFont => Theme.Derive(Font, Theme.Strong);

    /// <summary>Number badge: 28 px at 100 % (larger if the digit needs it), centred on the title row.</summary>
    private Rectangle Badge
    {
        get
        {
            int d = Math.Max(Px(28), TextRenderer.MeasureText("8", NumberFont).Height + Px(6));
            int cy = TitleTop + TitleHeight / 2;
            return new Rectangle(Px(16), Math.Max(Px(4), cy - d / 2), d, d);
        }
    }

    protected override int TitleLeft => Badge.Right + Px(12);
    protected override int HeaderBottom => Math.Max(base.HeaderBottom, Badge.Bottom - Px(3));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var badge = Badge;
        Color fill = _state switch { StepState.Done => Theme.Success, StepState.Active => Theme.Accent, _ => Theme.Surface2 };
        using (var b = new SolidBrush(fill)) g.FillEllipse(b, badge);
        if (_state == StepState.Done)
        {
            // Drawn rather than typed: not every font has a check mark glyph. Points are on a 28 px badge.
            float u = badge.Width / 28f;
            using var p = new Pen(Color.White, 2.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(p, new[]
            {
                new PointF(badge.X + 8 * u, badge.Y + 14.5f * u), new PointF(badge.X + 12.5f * u, badge.Y + 19 * u),
                new PointF(badge.X + 20.5f * u, badge.Y + 9.5f * u)
            });
        }
        else
            TextRenderer.DrawText(g, Number.ToString(), NumberFont, badge, _state == StepState.Pending ? Theme.TextDim : Color.White,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (_state == StepState.Active)
        {
            float pw = 2f * K;
            using var p = new Pen(Theme.Accent, pw);
            using var path = Theme.Round(new RectangleF(pw / 2, pw / 2, Width - pw - 1, Height - pw - 1), 10 * K);
            g.DrawPath(p, path);
        }
    }
}

/// <summary>Thin progress bar.</summary>
public sealed class SlimProgress : Control
{
    private double _value;
    public double Value { get => _value; set { double v = Math.Clamp(value, 0, 1); if (Math.Abs(v - _value) > 0.001) { _value = v; Invalidate(); } } }
    public Color BarColor { get; set; } = Theme.Accent;

    public SlimProgress()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 6;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var path = Theme.Round(r, Height / 2f)) using (var b = new SolidBrush(Theme.Surface2)) g.FillPath(b, path);
        if (_value <= 0) return;
        using (var path = Theme.Round(new RectangleF(0, 0, Math.Max(Height, (float)((Width - 1) * _value)), Height - 1), Height / 2f))
        using (var b = new SolidBrush(BarColor)) g.FillPath(b, path);
    }
}

/// <summary>One page of the app. Shown by NavHost, listed in SideNav.</summary>
public sealed class NavPage : Panel
{
    public string Glyph { get; }
    public string Heading { get; }
    public string Blurb { get; }
    /// <summary>Scrolling body of the page under its heading.</summary>
    public Panel Body { get; }

    public NavPage(string text, string glyph, string heading, string blurb)
    {
        Text = text; Glyph = glyph; Heading = heading; Blurb = blurb;
        Dock = DockStyle.Fill;
        Visible = false;
        Padding = new Padding(24, 18, 24, 18);
        Body = new PageBody { Dock = DockStyle.Fill };
        Controls.Add(Body);
        Controls.Add(new PageHead(this) { Dock = DockStyle.Top });
    }

    /// <summary>
    /// Page body. When it scrolls (AutoScroll) and holds a card docked to fill the rest of the page, WinForms ignores
    /// that card when working out the scroll range, so a window shorter than the card's text would cut the text off.
    /// Reserving the card's content height as AutoScrollMinSize makes the page scroll instead; when there is room the
    /// card still fills the page as before.
    /// </summary>
    private sealed class PageBody : Panel
    {
        private bool _fitting;

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control is Card c) c.Layout += (_, _) => FitScrollRange();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            FitScrollRange();
        }

        private void FitScrollRange()
        {
            if (!AutoScroll || _fitting || !Visible) return;
            int h = Padding.Vertical;
            bool fillCard = false;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                if (c.Dock is DockStyle.Top or DockStyle.Bottom) h += c.Height;
                else if (c.Dock == DockStyle.Fill && c is Card card) { h += card.ContentHeight; fillCard = true; }
            }
            var min = fillCard ? new Size(0, h) : Size.Empty;
            if (AutoScrollMinSize == min) return;
            _fitting = true;
            try { AutoScrollMinSize = min; }
            finally { _fitting = false; }
        }
    }

    /// <summary>Page title and one-line blurb; its height comes from the two fonts.</summary>
    private sealed class PageHead : Panel, ISelfScaling
    {
        private readonly NavPage _page;
        bool ISelfScaling.OwnsHeight => true;

        public PageHead(NavPage page)
        {
            _page = page;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Fit();
        }

        private float K => Theme.ScaleOf(Font);
        private int Px(float v) => Theme.Px(K, v);
        private Font TitleFont => Theme.Derive(Font, Theme.PageTitle);
        private int TitleH => TextRenderer.MeasureText(_page.Heading, TitleFont).Height;
        private int BlurbTop => Math.Max(Px(36), TitleH + Px(4));
        private int BlurbH => TextRenderer.MeasureText(_page.Blurb ?? "", Font).Height;

        private void Fit()
        {
            int h = _page.Blurb == null ? Math.Max(Px(44), TitleH + Px(12)) : Math.Max(Px(70), BlurbTop + BlurbH + Px(14));
            if (Height != h) Height = h;
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); Invalidate(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, _page.Heading, TitleFont, new Rectangle(-Px(2), 0, Width + Px(2), TitleH), Theme.Text, flags);
            if (_page.Blurb != null)
                TextRenderer.DrawText(e.Graphics, _page.Blurb, Font, new Rectangle(0, BlurbTop, Width, BlurbH), Theme.TextDim, flags);
        }
    }
}

/// <summary>Holds the pages and shows one at a time (replaces the old TabControl).</summary>
public sealed class NavHost : Panel
{
    private NavPage _selected;
    public List<NavPage> TabPages { get; } = new();
    public event EventHandler SelectedIndexChanged;

    public NavHost() { Dock = DockStyle.Fill; }

    public void Add(NavPage p)
    {
        TabPages.Add(p);
        Controls.Add(p);
        if (_selected == null) SelectedTab = p;
    }

    public NavPage SelectedTab
    {
        get => _selected;
        set
        {
            if (value == null || value == _selected) return;
            SuspendLayout();
            foreach (var p in TabPages) p.Visible = p == value;
            _selected = value;
            ResumeLayout();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Left navigation listing the pages of a NavHost.</summary>
public sealed class SideNav : Panel, ISelfScaling
{
    private readonly NavHost _host;
    private int _hover = -1;
    private int _itemH = 40, _top0 = 12, _iconW = 24;
    /// <summary>Optional small badge text per page (e.g. "Ready", "3").</summary>
    public Func<NavPage, string> Badge { get; set; }

    bool ISelfScaling.OwnsWidth => true;

    public SideNav(NavHost host)
    {
        _host = host;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Dock = DockStyle.Left;
        Width = 214;
        BackColor = Theme.Sidebar;
        Cursor = Cursors.Hand;
        host.SelectedIndexChanged += (_, _) => Invalidate();
        host.ControlAdded += (_, _) => UpdateMetrics();
        UpdateMetrics();
    }

    private float K => Theme.ScaleOf(Font);
    private int Px(float v) => Theme.Px(K, v);
    private Font IconFont => Theme.Derive(Font, Theme.Icons);
    private Font SelectedFont => Theme.Derive(Font, Theme.Strong);
    private Font BadgeFont => Theme.Derive(Font, Theme.Small);

    /// <summary>Item height, icon column and width from the fonts: everything fits, nothing is cut off.</summary>
    private void UpdateMetrics()
    {
        int textH = Math.Max(TextRenderer.MeasureText("Ag", Font).Height, TextRenderer.MeasureText("Ag", SelectedFont).Height);
        int iconH = 0, iconW = Px(24), labelW = 0;
        foreach (var p in _host.TabPages)
        {
            var g = TextRenderer.MeasureText(p.Glyph ?? "", IconFont);
            iconH = Math.Max(iconH, g.Height);
            iconW = Math.Max(iconW, g.Width);
            labelW = Math.Max(labelW, Math.Max(TextRenderer.MeasureText(p.Text, Font).Width, TextRenderer.MeasureText(p.Text, SelectedFont).Width));
        }
        _iconW = iconW;
        _itemH = Math.Max(Px(40), Math.Max(textH, iconH) + Px(14));
        _top0 = Px(12);
        // Room for the widest label plus the widest badge a page can show ("10k+", "9,999").
        int badgeW = Math.Max(TextRenderer.MeasureText("10k+", BadgeFont).Width, TextRenderer.MeasureText("9,999", BadgeFont).Width) + Px(8);
        int w = TextLeft + labelW + Px(6) + badgeW + Px(6) + Px(10) + 1;
        w = Math.Max(Px(214), w);
        if (Width != w) Width = w;
        Invalidate();
    }

    /// <summary>Item rectangle's x is 10, the icon column starts 12 further, the label 8 after the icon.</summary>
    private int TextLeft => Px(10) + Px(12) + _iconW + Px(8);

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateMetrics(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateMetrics(); }

    private int IndexAt(Point p)
    {
        int i = (p.Y - _top0) / _itemH;
        return p.Y >= _top0 && i >= 0 && i < _host.TabPages.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.Location); if (i != _hover) { _hover = i; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        int i = IndexAt(e.Location);
        if (i >= 0) _host.SelectedTab = _host.TabPages[i];
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = K;
        for (int i = 0; i < _host.TabPages.Count; i++)
        {
            var p = _host.TabPages[i];
            bool sel = p == _host.SelectedTab;
            var r = new Rectangle(Px(10), _top0 + i * _itemH, Width - Px(20), _itemH - Px(4));
            if (sel || i == _hover)
                using (var path = Theme.Round(r, 7 * k)) using (var b = new SolidBrush(sel ? Theme.Surface2 : Theme.Surface)) g.FillPath(b, path);
            if (sel)
                using (var path = Theme.Round(new RectangleF(r.X, r.Y + 9 * k, 3 * k, r.Height - 18 * k), 1.5f * k)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
            var col = sel ? Theme.Text : Theme.TextDim;
            TextRenderer.DrawText(g, p.Glyph, IconFont, new Rectangle(r.X + Px(12), r.Y, _iconW, r.Height), sel ? Theme.Accent : col,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine);
            int textRight = r.Right - Px(6);
            string badge = Badge?.Invoke(p);
            if (!string.IsNullOrEmpty(badge))
            {
                var sz = TextRenderer.MeasureText(badge, BadgeFont);
                int bh = Math.Min(r.Height, Math.Max(Px(20), sz.Height + Px(2)));
                var br = new Rectangle(r.Right - Px(6) - sz.Width - Px(8), r.Y + (r.Height - bh) / 2, sz.Width + Px(8), bh);
                using (var path = Theme.Round(br, bh / 2f)) using (var b = new SolidBrush(Theme.AccentDim)) g.FillPath(b, path);
                TextRenderer.DrawText(g, badge, BadgeFont, br, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                textRight = br.X - Px(6);
            }
            TextRenderer.DrawText(g, p.Text, sel ? SelectedFont : Font, new Rectangle(TextLeft, r.Y, Math.Max(1, textRight - TextLeft), r.Height), col,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
        using var line = new Pen(Theme.Border);
        g.DrawLine(line, Width - 1, 0, Width - 1, Height);
    }
}

/// <summary>Small rounded status chip ("Attached", "Not attached").</summary>
public sealed class Pill : Control, ISelfScaling
{
    private Color _dot = Theme.TextDim;
    public Color Dot { get => _dot; set { _dot = value; Invalidate(); } }

    bool ISelfScaling.OwnsWidth => true;
    bool ISelfScaling.OwnsHeight => true;

    public Pill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Small;
        Margin = new Padding(0, 5, 8, 0);
        Fit();
    }

    /// <summary>The pill's own font is the small font (8.75 pt at 100 %).</summary>
    private int Px(float v) => Theme.Px(Theme.ScaleOf(Font, Theme.Small.SizeInPoints), v);

    private void Fit()
    {
        var s = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font);
        var size = new Size(s.Width + Px(36), Math.Max(Px(28), s.Height + Px(10)));
        if (Size != size) Size = size;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f))
        using (var b = new SolidBrush(Theme.Surface2)) g.FillPath(b, path);
        int d = Px(8);
        using (var b = new SolidBrush(_dot)) g.FillEllipse(b, Px(11), (Height - d) / 2, d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Px(24), 0, Width - Px(28), Height), Theme.Text,
                              TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>The "♥ HealthHunter v5.1" mark in the top bar. Its three fonts follow the control's own font.</summary>
public sealed class BrandMark : Control, ISelfScaling
{
    private static readonly Font HeartDesign = new("Segoe UI", 18f);
    private static readonly Font NameDesign = new("Segoe UI Semibold", 15f);
    public string Version { get; set; } = "";

    bool ISelfScaling.OwnsWidth => true;

    public BrandMark()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Fit();
    }

    private float K => Theme.ScaleOf(Font);
    private int Px(float v) => Theme.Px(K, v);
    private Font Heart => Theme.Derive(Font, HeartDesign);
    private Font NameFont => Theme.Derive(Font, NameDesign);
    private Font Ver => Theme.Derive(Font, Theme.Small);

    /// <summary>Distance from a text box's top to its baseline when drawn with TextRenderer.</summary>
    private static float Ascent(Font f)
    {
        try
        {
            var ff = f.FontFamily;
            var st = ff.IsStyleAvailable(f.Style) ? f.Style : FontStyle.Regular;
            return Theme.EmPixels(f) * ff.GetCellAscent(st) / ff.GetEmHeight(st);
        }
        catch { return f.GetHeight() * 0.8f; }
    }

    /// <summary>Positions of the heart, name and version, with their baselines aligned; y relative to the name's top.</summary>
    private (Rectangle heart, Rectangle name, Rectangle ver) Arrange()
    {
        var hs = TextRenderer.MeasureText("♥", Heart);
        var ns = TextRenderer.MeasureText(Text, NameFont);
        var vs = TextRenderer.MeasureText(Version, Ver);
        float baseline = Ascent(NameFont);
        int nameX = Math.Max(Px(30), hs.Width + Px(4));
        return (new Rectangle(0, (int)Math.Round(baseline - Ascent(Heart)), hs.Width, hs.Height),
                new Rectangle(nameX, 0, ns.Width, ns.Height),
                new Rectangle(nameX + ns.Width, (int)Math.Round(baseline - Ascent(Ver)), vs.Width, vs.Height));
    }

    /// <summary>Height the mark needs.</summary>
    public int PreferredHeight
    {
        get
        {
            var (h, n, v) = Arrange();
            return Math.Max(h.Bottom, Math.Max(n.Bottom, v.Bottom)) - Math.Min(h.Top, Math.Min(n.Top, v.Top));
        }
    }

    private void Fit()
    {
        var (_, _, v) = Arrange();
        int w = Math.Max(Px(230), v.Right + Px(16));
        if (Width != w) Width = w;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); Invalidate(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Fit(); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var (h, n, v) = Arrange();
        int top = Math.Min(h.Top, Math.Min(n.Top, v.Top)), bottom = Math.Max(h.Bottom, Math.Max(n.Bottom, v.Bottom));
        int dy = (Height - (bottom - top)) / 2 - top; // centre the whole mark vertically
        var g = e.Graphics;
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, "♥", Heart, new Point(h.X, h.Y + dy), Theme.Danger, flags);
        TextRenderer.DrawText(g, Text, NameFont, new Point(n.X, n.Y + dy), Theme.Text, flags);
        TextRenderer.DrawText(g, Version, Ver, new Point(v.X, v.Y + dy), Theme.TextDim, flags);
    }
}

public static class Ui
{
    /// <summary>Horizontal row of controls that wraps when narrow.</summary>
    public static FlowLayoutPanel Row(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
        f.Controls.AddRange(cs);
        return f;
    }

    /// <summary>Small dim caption placed before an input.</summary>
    public static Label Caption(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = Theme.TextDim, Padding = new Padding(0, 9, 0, 0), Margin = new Padding(0, 0, 6, 0) };

    public static TextBlock Para(string text, bool dim = true) => new() { Text = text, ForeColor = dim ? Theme.TextDim : Theme.Text };

    public static Panel Gap(int h = 14) => new() { Dock = DockStyle.Top, Height = h };

    /// <summary>
    /// Docks controls into a parent in reading order: tops from top to bottom, then fill, then bottoms
    /// from top to bottom. (WinForms docks in reverse add-order, which this hides.)
    /// </summary>
    public static void Stack(Control parent, Control[] tops, Control fill = null, Control[] bottoms = null)
    {
        parent.SuspendLayout();
        if (fill != null) { fill.Dock = DockStyle.Fill; parent.Controls.Add(fill); }
        if (bottoms != null)
            foreach (var b in bottoms) { b.Dock = DockStyle.Bottom; parent.Controls.Add(b); }
        if (tops != null)
            for (int i = tops.Length - 1; i >= 0; i--) { tops[i].Dock = DockStyle.Top; parent.Controls.Add(tops[i]); }
        parent.ResumeLayout();
    }
}
