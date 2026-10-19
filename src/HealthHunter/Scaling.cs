using System.Drawing;
using System.Runtime.CompilerServices;

namespace HealthHunter;

/// <summary>
/// Implemented by controls that work out part of their own layout from their font. <see cref="DesignScale"/> then
/// leaves those parts alone instead of multiplying the 100 % numbers written in code.
/// </summary>
public interface ISelfScaling
{
    bool OwnsWidth => false;
    bool OwnsHeight => false;
    bool OwnsPadding => false;
}

/// <summary>
/// Every width, height, margin, padding, minimum size and list column written in code is a 100 % ("design")
/// value. Track() remembers those values the first time a control is seen; Apply() sets them to design × scale,
/// where the scale comes from the form's current font (<see cref="Theme.ScaleOf"/>), so plain controls grow in
/// step with their text at any Windows scaling, on mixed-DPI monitors and after a DPI change. Values are always
/// recomputed from the design numbers, so repeated DPI changes never compound rounding.
/// Sizes that come from the font anyway (single-line text boxes, combo boxes, AutoSize controls, docked
/// fill) are left to WinForms, and controls implementing <see cref="ISelfScaling"/> size those parts themselves.
/// </summary>
public static class DesignScale
{
    private sealed class Design
    {
        public Size Size, Min, Max;
        public Padding Margin, Padding;
        public float ColumnsAt = 1f; // scale the ListView column widths currently correspond to
    }

    private static readonly ConditionalWeakTable<Control, Design> s_designs = new();

    /// <summary>Remembers <paramref name="c"/>'s design metrics (once) and, if its form is already scaled, applies that scale.</summary>
    public static void Track(Control c)
    {
        if (c is Form || s_designs.TryGetValue(c, out _)) return;
        var d = new Design { Size = c.Size, Min = c.MinimumSize, Max = c.MaximumSize, Margin = c.Margin, Padding = c.Padding };
        s_designs.Add(c, d);
        if (c.FindForm() is ScaledForm f && f.AppliedScale > 0) ApplyOne(c, d, f.AppliedScale);
    }

    /// <summary>Tracks a whole tree (controls already tracked keep their first, design, values).</summary>
    public static void TrackTree(Control root)
    {
        Track(root);
        foreach (Control c in root.Controls) TrackTree(c);
    }

    /// <summary>Sets every tracked control under <paramref name="root"/> to its design metrics × <paramref name="k"/>.</summary>
    public static void Apply(Control root, float k)
    {
        if (s_designs.TryGetValue(root, out var d)) ApplyOne(root, d, k);
        foreach (Control c in root.Controls) Apply(c, k);
    }

    private static int S(int v, float k) => (int)Math.Round(v * k);
    private static Size S(Size s, float k) => new(S(s.Width, k), S(s.Height, k));
    private static Padding S(Padding p, float k) => new(S(p.Left, k), S(p.Top, k), S(p.Right, k), S(p.Bottom, k));

    /// <summary>Heights the control derives from its own font; setting them would be ignored or fight WinForms.</summary>
    private static bool FontSetsHeight(Control c) =>
        c is ComboBox or UpDownBase || (c is TextBoxBase t && !t.Multiline);

    private static void ApplyOne(Control c, Design d, float k)
    {
        if (c is Form) return;
        var own = c as ISelfScaling;

        var margin = S(d.Margin, k);
        if (c.Margin != margin) c.Margin = margin;
        if (own?.OwnsPadding != true)
        {
            var padding = S(d.Padding, k);
            if (c.Padding != padding) c.Padding = padding;
        }
        if (d.Min != Size.Empty) c.MinimumSize = S(d.Min, k);
        if (d.Max != Size.Empty) c.MaximumSize = S(d.Max, k);

        // AutoSize controls size themselves from their text, except text boxes, where it only means "height from font".
        if (!c.AutoSize || c is TextBoxBase)
        {
            bool w = own?.OwnsWidth != true && c.Dock is DockStyle.None or DockStyle.Left or DockStyle.Right;
            bool h = own?.OwnsHeight != true && c.Dock is DockStyle.None or DockStyle.Top or DockStyle.Bottom && !FontSetsHeight(c);
            if (w || h)
            {
                var size = new Size(w ? S(d.Size.Width, k) : c.Width, h ? S(d.Size.Height, k) : c.Height);
                if (c.Size != size) c.Size = size;
            }
        }

        if (c is ListView lv && Math.Abs(d.ColumnsAt - k) > 0.0005f)
        {
            // Relative, so a column the user has resized keeps its share.
            foreach (ColumnHeader col in lv.Columns)
                if (col.Width > 0) col.Width = Math.Max(1, (int)Math.Round(col.Width * k / d.ColumnsAt));
            d.ColumnsAt = k;
        }
    }
}

/// <summary>
/// A form whose sizes in code are 100 % design values. Call <see cref="EnableDesignScaling"/> at the end of the
/// constructor. From then on the form keeps every tracked control at design × the scale of its own font, and
/// re-applies that whenever WinForms rescales the font: when the window is created on a monitor whose DPI differs
/// from the system DPI, when it is moved to another monitor, and when the scaling setting is changed.
/// The form stays in WinForms' default AutoScaleMode (no automatic size scaling), so nothing is scaled twice.
/// </summary>
public class ScaledForm : Form
{
    private Size _designSize, _designMin;
    private SizeF _wanted;   // size the scaling asked for before it was fitted to the screen
    private Size _given;     // size it actually set; differs from Size once the user resizes
    private float _designPoints = 9.75f, _k;
    private bool _inDpiChange;

    /// <summary>Scale the design numbers are currently applied at; 0 before EnableDesignScaling.</summary>
    public float AppliedScale => _k;

    /// <summary>Rounds a 100 % design length to pixels at the current scale.</summary>
    protected int Px(float designPixels) => Theme.Px(_k > 0 ? _k : Theme.ScaleOf(Font, _designPoints), designPixels);

    /// <param name="designFontPoints">Point size the form's Font was designed with.</param>
    protected void EnableDesignScaling(float designFontPoints = 9.75f)
    {
        _designPoints = designFontPoints;
        _designSize = Size;
        _designMin = MinimumSize;
        DesignScale.TrackTree(this);
        Rescale(force: true);
    }

    /// <summary>Called after the design metrics have been re-applied for a new scale.</summary>
    protected virtual void OnUiScaleChanged(float k) { }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e); // children that inherit the font get it first
        if (_k > 0) Rescale(force: false);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        _inDpiChange = true; // Windows supplies the new window rectangle itself
        try { base.OnDpiChanged(e); }
        finally { _inDpiChange = false; }
        if (_k > 0) Rescale(force: true);
    }

    /// <summary>
    /// Let Windows scale the window linearly with the DPI (the same ratio WinForms scales the fonts by). The
    /// default for forms without AutoScaleMode keeps the old pixel size, which crowds the content on a
    /// higher-DPI monitor.
    /// </summary>
    protected override bool OnGetDpiScaledSize(int deviceDpiOld, int deviceDpiNew, ref Size desiredSize) => false;

    private void Rescale(bool force)
    {
        float k = Theme.ScaleOf(Font, _designPoints);
        if (!force && Math.Abs(k - _k) < 0.0005f) return;
        float old = _k;
        _k = k;
        SuspendLayout();
        try
        {
            DesignScale.Apply(this, k);
            var wa = (IsHandleCreated ? Screen.FromHandle(Handle) : Screen.FromPoint(Cursor.Position)).WorkingArea;
            static Size Within(Size s, Size area) => new(Math.Min(s.Width, area.Width), Math.Min(s.Height, area.Height));
            MinimumSize = Within(new Size(Theme.Px(k, _designMin.Width), Theme.Px(k, _designMin.Height)), wa.Size);
            if (old == 0)
            {
                _wanted = new SizeF(_designSize.Width * k, _designSize.Height * k);
                Size = Within(Size.Round(_wanted), wa.Size);
                _given = Size;
            }
            else if (!_inDpiChange && WindowState == FormWindowState.Normal && Math.Abs(old - k) > 0.0005f)
            {
                // The font changed outside WM_DPICHANGED (e.g. the window was created on a monitor whose DPI differs
                // from the system DPI): resize by the same ratio and keep the window on its screen. Start from the
                // unclipped size unless the user has resized the window since, so a size that was cut down to fit
                // one screen is not carried over to a screen where it fits.
                var from = Size == _given ? _wanted : new SizeF(Width, Height);
                _wanted = new SizeF(from.Width * k / old, from.Height * k / old);
                Size = Within(Size.Round(_wanted), wa.Size);
                _given = Size;
                if (IsHandleCreated)
                    Location = new Point(Math.Max(wa.Left, Math.Min(Left, wa.Right - Width)), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height)));
            }
            OnUiScaleChanged(k);
        }
        finally { ResumeLayout(true); }
    }
}
