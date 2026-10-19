namespace HealthHunter;

/// <summary>Live line chart of one value over the last minute or so.</summary>
public sealed class ValueGraph : Control
{
    private readonly Queue<double> _samples = new();
    public int Capacity { get; set; } = 300;
    public string Title { get; set; } = "";
    public ValueKind Kind { get; set; } = ValueKind.Int32;

    public ValueGraph()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(24, 24, 28);
        ForeColor = Color.Gainsboro;
    }

    public void Clear() { _samples.Clear(); Invalidate(); }

    public void Add(double v)
    {
        _samples.Enqueue(v);
        while (_samples.Count > Capacity) _samples.Dequeue();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Offsets are 100 % design values scaled with the (DPI-correct) font; the plot area makes room for the labels.
        float k = Theme.ScaleOf(Font);
        int Px(float v) => Theme.Px(k, v);
        var pts = _samples.Where(v => !double.IsNaN(v)).ToArray();
        using var textBrush = new SolidBrush(ForeColor);
        if (pts.Length == 0)
        {
            g.DrawString(string.IsNullOrEmpty(Title) ? "Select a table row to graph its value" : Title + ": no readings yet", Font, textBrush, Px(6), Px(6));
            return;
        }
        double min = pts.Min(), max = pts.Max();
        if (max - min < 1e-9) { min -= 1; max += 1; }
        string maxText = Kind.Format(max), minText = Kind.Format(min);
        float lh = Font.GetHeight(g);
        float labelW = Math.Max(g.MeasureString(maxText, Font).Width, g.MeasureString(minText, Font).Width);
        int left = Math.Max(Px(50), (int)Math.Ceiling(labelW) + Px(6));
        int top = Math.Max(Px(22), Px(3) + (int)Math.Ceiling(lh) + Px(2));
        var r = new Rectangle(left, top, Math.Max(10, Width - left - Px(10)), Math.Max(10, Height - top - Px(8)));
        using (var grid = new Pen(Color.FromArgb(60, 60, 66)))
        {
            g.DrawRectangle(grid, r);
            g.DrawLine(grid, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);
        }
        // Max label sits on the top line, min label on the bottom line (same placement as at 100 %), kept inside the control.
        g.DrawString(maxText, Font, textBrush, Px(2), Math.Max(0, r.Top - lh * 0.35f));
        g.DrawString(minText, Font, textBrush, Px(2), Math.Max(0, Math.Min(Height - lh, r.Bottom - lh * 0.7f)));
        g.DrawString($"{Title}: {Kind.Format(pts[^1])}   (last {Capacity * 0.2:0} s)", Font, textBrush, r.Left, Px(3));
        if (pts.Length < 2) return;
        var line = new PointF[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            float x = r.Left + (float)i / (Capacity - 1) * r.Width;
            float y = r.Bottom - (float)((pts[i] - min) / (max - min)) * r.Height;
            line[i] = new PointF(x, y);
        }
        using var pen = new Pen(Color.FromArgb(80, 200, 120), 2f * k);
        g.DrawLines(pen, line);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Invalidate(); }
}
