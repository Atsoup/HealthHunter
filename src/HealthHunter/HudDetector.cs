using System.Drawing;
using System.Drawing.Imaging;

namespace HealthHunter;

/// <summary>A bar the HUD detector is tracking, in the game window's client pixels.</summary>
public sealed class DetectedBar
{
    public int Id;
    public bool Vertical;
    /// <summary>Position across the bar (y and height for a horizontal bar, x and width for a vertical one).</summary>
    public int CrossStart, CrossSize;
    /// <summary>The filled part seen last, along the bar (inclusive end).</summary>
    public int AlongStart, AlongEnd;
    /// <summary>0 unknown, 1 the fill starts at AlongStart and shrinks towards it (left/top anchored), 2 the other way round.</summary>
    public int Anchor;
    /// <summary>Length of the bar when full: the longest fill seen, or fill + the empty track next to it when that is visible.</summary>
    public int FullLen;
    public int MaxLenSeen, TrackLen;
    public bool TrackBounded;
    public int FillArgb, Tolerance = 45;
    public string Hue = "";
    /// <summary>Fill fraction from the last frame it was seen in.</summary>
    public double Fill = double.NaN;
    public int Changes, Frames, Missed, JitterFrames;
    public bool WentDown, WentUp;
    public double FirstSettled = double.NaN, LastSettled = double.NaN;
    public int SettleFrames, LastLen = -1;
    public DateTime FirstSeen, LastSeen;
    /// <summary>Set when the bar came from a saved profile rather than from this session's screen.</summary>
    public bool FromProfile;
    /// <summary>What the assistant decided the bar is ("Health", "Mana"…); empty until it starts on it.</summary>
    public string Label = "";
    /// <summary>The assistant's state for this bar.</summary>
    public object Tag;

    /// <summary>A bar whose fill really moves with the game, as opposed to a border, panel or decoration.</summary>
    public bool Live => FullLen >= HudDetector.MinLen && Frames >= 6 && JitterFrames * 2 < Frames &&
                        (Changes >= 2 || (Changes >= 1 && Math.Abs(LastSettled - FirstSettled) >= 0.05 * FullLen));

    public int Len => AlongEnd - AlongStart + 1;

    /// <summary>The whole bar (full length) in client pixels.</summary>
    public Rectangle FullRect
    {
        get
        {
            int start = Anchor == 2 ? AlongEnd - FullLen + 1 : AlongStart;
            return Vertical ? new Rectangle(CrossStart, start, CrossSize, FullLen) : new Rectangle(start, CrossStart, FullLen, CrossSize);
        }
    }

    /// <summary>What the existing bar reader needs, in screen coordinates.</summary>
    public BarCalibration ToCalibration(Point clientOrigin)
    {
        var r = FullRect;
        return new BarCalibration
        {
            X = clientOrigin.X + r.X, Y = clientOrigin.Y + r.Y, Width = r.Width, Height = r.Height,
            FillArgb = FillArgb, Tolerance = Tolerance, Vertical = Vertical, Anchor = Anchor
        };
    }

    public string Describe(Size client)
    {
        var r = FullRect;
        string where = Place(r, client);
        return $"{Hue} {(Vertical ? "vertical " : "")}bar, {r.Width}×{r.Height} px at the {where}";
    }

    public static string Place(Rectangle r, Size client)
    {
        if (client.Width <= 0 || client.Height <= 0) return "screen";
        double cx = (r.X + r.Width / 2.0) / client.Width, cy = (r.Y + r.Height / 2.0) / client.Height;
        string v = cy < 0.33 ? "top" : cy > 0.67 ? "bottom" : "middle";
        string h = cx < 0.33 ? "left" : cx > 0.67 ? "right" : "center";
        return v == "middle" && h == "center" ? "center" : $"{v}-{h}";
    }
}

/// <summary>
/// v6: finds HUD bars on its own. Looks for long runs of one color (the colored fill of a bar) in each captured frame,
/// stacks the runs into thin rectangles, and tracks every rectangle from frame to frame. A rectangle whose length changes
/// while one of its ends stays put is a bar the game fills and drains (health, mana, stamina…); borders, panels and
/// decorations never change, so they're ignored. Reads pixels only.
///
/// Limits (said plainly, not hidden): segmented bars (hearts, cells), round gauges and bars with a strong see-through or
/// textured fill are not picked up; mark those by hand on the Quick start page.
/// </summary>
public sealed class HudDetector
{
    public const int MinLen = 24;          // shortest fill run that counts as a bar (px)
    private const int MaxThick = 60;       // bars are thin; thicker uniform blocks are panels
    private const int AdjTol2 = 26 * 26;   // neighbouring pixels of one run may differ this much (smooth gradients)
    private const int FirstTol2 = 90 * 90; // ...and any pixel from the run's first pixel this much
    private const int LineTol2 = 60 * 60;  // runs on neighbouring lines belong to one bar within this
    private const int TrackTol2 = 40 * 40; // the empty track beside the fill must be this uniform
    private const int MaxBars = 80;
    private readonly object _lock = new();
    private readonly List<DetectedBar> _bars = new();
    private int _nextId = 1;
    public Size ClientSize { get; private set; }

    /// <summary>A copy of the tracked bars (safe to read from the UI thread).</summary>
    public List<DetectedBar> Bars { get { lock (_lock) return _bars.ToList(); } }

    public void Clear() { lock (_lock) _bars.Clear(); }

    /// <summary>Adds a bar remembered from an earlier session so it can be used before it has moved on screen.</summary>
    public DetectedBar Seed(bool vertical, Rectangle full, int anchor, int fillArgb, int tolerance, string label)
    {
        lock (_lock)
        {
            var b = new DetectedBar
            {
                Id = _nextId++, Vertical = vertical, Anchor = anchor == 0 ? 1 : anchor, FillArgb = fillArgb, Tolerance = tolerance,
                Hue = HueName(fillArgb), Label = label ?? "", FromProfile = true, FirstSeen = DateTime.Now, LastSeen = DateTime.Now
            };
            b.CrossStart = vertical ? full.X : full.Y;
            b.CrossSize = vertical ? full.Width : full.Height;
            int along0 = vertical ? full.Y : full.X, len = vertical ? full.Height : full.Width;
            b.AlongStart = along0; b.AlongEnd = along0 + len - 1;
            b.FullLen = b.MaxLenSeen = len;
            _bars.Add(b);
            return b;
        }
    }

    private struct Run { public int Line, Start, End, Color; }

    private sealed class Group
    {
        public bool Vertical;
        public int Line0, Line1;
        public readonly List<Run> Runs = new();
        public int Color;        // colour of the middle run
        public int Start, End;   // median run start / end
        public int Spread;       // largest distance of a run colour from Color
        public int Len => End - Start + 1;
        public int Thick => Line1 - Line0 + 1;
    }

    /// <summary>Looks at one frame of the game's client area.</summary>
    public void Process(Bitmap bmp, DateTime now)
    {
        var px = ReadPixels(bmp, out int w, out int h);
        ClientSize = new Size(w, h);
        if (w < MinLen || h < 2) return;
        var groups = new List<Group>();
        FindGroups(px, w, h, false, groups);
        FindGroups(px, w, h, true, groups);
        lock (_lock)
        {
            var matched = new HashSet<DetectedBar>();
            foreach (var g in groups)
            {
                var bar = Match(g, matched);
                if (bar == null)
                {
                    if (_bars.Count >= MaxBars) continue;
                    bar = new DetectedBar
                    {
                        Id = _nextId++, Vertical = g.Vertical, CrossStart = g.Line0, CrossSize = g.Thick, AlongStart = g.Start, AlongEnd = g.End,
                        FillArgb = g.Color, Tolerance = Math.Clamp(g.Spread + 25, 35, 110), Hue = HueName(g.Color),
                        FirstSeen = now
                    };
                    _bars.Add(bar);
                }
                matched.Add(bar);
                Update(bar, g, px, w, h, now);
            }
            foreach (var b in _bars)
                if (!matched.Contains(b)) b.Missed++;
            // Decorations that vanished for good are forgotten; a bar that moved is kept while the user is in a menu or loading.
            _bars.RemoveAll(b => !b.FromProfile && b.Missed > 0 && (now - b.LastSeen).TotalSeconds > (b.Live ? 600 : 20));
        }
    }

    private DetectedBar Match(Group g, HashSet<DetectedBar> taken)
    {
        DetectedBar best = null;
        int bestScore = int.MaxValue;
        foreach (var b in _bars)
        {
            if (taken.Contains(b) || b.Vertical != g.Vertical) continue;
            if (Math.Abs(b.CrossStart - g.Line0) > 3 || Math.Abs(b.CrossSize - g.Thick) > 4) continue;
            if (Dist2(b.FillArgb, g.Color) > LineTol2) continue;
            bool startOk = Math.Abs(b.AlongStart - g.Start) <= 3, endOk = Math.Abs(b.AlongEnd - g.End) <= 3;
            if (b.Anchor == 1 && !startOk) continue;
            if (b.Anchor == 2 && !endOk) continue;
            if (!startOk && !endOk)
            {
                // A fill that shrank towards an end can be shorter than the whole bar from either side: it still has to sit inside it.
                var r = b.FullRect;
                int full0 = g.Vertical ? r.Y : r.X, full1 = full0 + b.FullLen - 1;
                if (g.Start < full0 - 3 || g.End > full1 + 3) continue;
            }
            int score = Math.Abs(b.CrossStart - g.Line0) + Math.Abs(b.CrossSize - g.Thick) + Math.Min(Math.Abs(b.AlongStart - g.Start), Math.Abs(b.AlongEnd - g.End));
            if (score < bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    private void Update(DetectedBar b, Group g, int[] px, int w, int h, DateTime now)
    {
        int len = g.Len;
        if (b.Anchor == 0 && b.Frames > 0)
        {
            if (Math.Abs(g.Start - b.AlongStart) <= 2 && Math.Abs(g.End - b.AlongEnd) > 3) b.Anchor = 1;
            else if (Math.Abs(g.End - b.AlongEnd) <= 2 && Math.Abs(g.Start - b.AlongStart) > 3) b.Anchor = 2;
        }
        b.AlongStart = g.Start; b.AlongEnd = g.End;
        b.CrossStart = g.Line0; b.CrossSize = g.Thick;
        if (!b.FromProfile || b.Frames > 0) { b.FillArgb = g.Color; b.Tolerance = Math.Clamp(g.Spread + 25, 35, 110); b.Hue = HueName(g.Color); }
        b.MaxLenSeen = Math.Max(b.MaxLenSeen, len);
        // The empty part of the bar is usually drawn as a darker track of the same height: measure it, on the side the fill drains to.
        int track = 0; bool bounded = false;
        if (b.Anchor != 2) { track = Track(px, w, h, g, after: true, out bounded); }
        if (b.Anchor != 1 && (track == 0 || !bounded))
        {
            int t2 = Track(px, w, h, g, after: false, out bool bd2);
            if (t2 > track || (bd2 && !bounded)) { track = t2; bounded = bd2; }
        }
        b.TrackLen = track; b.TrackBounded = bounded;
        int full = b.MaxLenSeen;
        if (bounded && len + track > full && len + track <= 6 * Math.Max(MinLen, len)) full = len + track;
        if (b.FromProfile && b.FullLen > 0) full = Math.Max(b.FullLen, Math.Min(full, b.FullLen * 2)); // a saved bar keeps its length unless clearly longer now
        b.FullLen = Math.Max(full, MinLen);
        b.Fill = Math.Clamp((double)len / b.FullLen, 0, 1);

        // A change counts once the new length has held for two frames (hit animations and damage trails settle first).
        if (b.LastLen >= 0 && Math.Abs(len - b.LastLen) > 1) { b.JitterFrames++; b.SettleFrames = 0; }
        else b.SettleFrames++;
        b.LastLen = len;
        if (b.SettleFrames >= 2)
        {
            if (double.IsNaN(b.LastSettled)) { b.FirstSettled = b.LastSettled = len; }
            else if (Math.Abs(len - b.LastSettled) >= Math.Max(2, 0.015 * b.FullLen))
            {
                b.Changes++;
                if (len < b.LastSettled) b.WentDown = true; else b.WentUp = true;
                b.LastSettled = len;
            }
        }
        b.Frames++;
        b.Missed = 0;
        b.LastSeen = now;
    }

    /// <summary>
    /// Length of the uniform strip right after (or before) the fill, the same thickness as the fill and bordered on both
    /// sides, i.e. the bar's empty track. 0 when there is none; bounded = false when the strip is really a panel.
    /// </summary>
    private static int Track(int[] px, int w, int h, Group g, bool after, out bool bounded)
    {
        bounded = false;
        int dim = g.Vertical ? h : w;
        int step = after ? 1 : -1;
        int from = after ? g.End + 1 : g.Start - 1;
        if (from < 0 || from >= dim) return 0;
        int probe = Math.Clamp(from + step, 0, dim - 1);
        int mid = (g.Line0 + g.Line1) / 2;
        int track = At(px, w, g.Vertical, probe, mid);
        if (Dist2(track, g.Color) <= FirstTol2 / 4) return 0; // the fill colour continues: not a track
        int limit = Math.Min(6 * Math.Max(MinLen, g.Len), dim);
        int n = 0;
        for (int p = from; p >= 0 && p < dim && n < limit; p += step)
        {
            bool uniform = true;
            for (int l = g.Line0; l <= g.Line1 && uniform; l++)
                if (Dist2(At(px, w, g.Vertical, p, l), track) > TrackTol2) uniform = false;
            if (!uniform) break;
            n++;
        }
        if (n < 3) return 0;
        int endPos = from + step * n;
        bool stoppedInside = endPos >= 0 && endPos < dim && n < limit;
        int midPos = from + step * (n / 2);
        int crossDim = g.Vertical ? w : h;
        bool aboveDiff = g.Line0 == 0 || Dist2(At(px, w, g.Vertical, midPos, g.Line0 - 1), track) > TrackTol2;
        bool belowDiff = g.Line1 >= crossDim - 1 || Dist2(At(px, w, g.Vertical, midPos, g.Line1 + 1), track) > TrackTol2;
        bounded = stoppedInside && aboveDiff && belowDiff;
        return n;
    }

    private static int At(int[] px, int w, bool vertical, int along, int cross) => vertical ? px[along * w + cross] : px[cross * w + along];

    private static void FindGroups(int[] px, int w, int h, bool vertical, List<Group> output)
    {
        int lines = vertical ? w : h, dim = vertical ? h : w;
        if (dim < MinLen) return;
        int maxLen = (int)(dim * 0.9);
        var open = new List<Group>();
        var runs = new List<Run>();
        for (int line = 0; line < lines; line++)
        {
            runs.Clear();
            int p = 0;
            while (p < dim)
            {
                int c = At(px, w, vertical, p, line);
                if (!FillLike(c)) { p++; continue; }
                int start = p, prev = c, first = c;
                p++;
                while (p < dim)
                {
                    int q = At(px, w, vertical, p, line);
                    if (Dist2(q, prev) > AdjTol2 || Dist2(q, first) > FirstTol2) break;
                    prev = q; p++;
                }
                int len = p - start;
                if (len >= MinLen && len <= maxLen)
                    runs.Add(new Run { Line = line, Start = start, End = p - 1, Color = At(px, w, vertical, start + len / 2, line) });
            }
            // Attach each run to an open group that ended on the previous line, else open a new one.
            foreach (var r in runs)
            {
                Group g = null;
                foreach (var o in open)
                {
                    if (o.Line1 != line - 1) continue;
                    var last = o.Runs[^1];
                    if (Math.Abs(last.Start - r.Start) <= 3 && Math.Abs(last.End - r.End) <= 3 && Dist2(last.Color, r.Color) <= LineTol2) { g = o; break; }
                }
                if (g == null) { g = new Group { Vertical = vertical, Line0 = line }; open.Add(g); }
                g.Line1 = line;
                g.Runs.Add(r);
            }
            // Close groups that didn't continue on this line.
            for (int i = open.Count - 1; i >= 0; i--)
            {
                if (open[i].Line1 == line) continue;
                Close(open[i], output);
                open.RemoveAt(i);
            }
        }
        foreach (var g in open) Close(g, output);
    }

    private static void Close(Group g, List<Group> output)
    {
        if (g.Thick < 2 || g.Thick > MaxThick) return;
        var starts = g.Runs.Select(r => r.Start).OrderBy(x => x).ToList();
        var ends = g.Runs.Select(r => r.End).OrderBy(x => x).ToList();
        g.Start = starts[starts.Count / 2]; g.End = ends[ends.Count / 2];
        if (g.Len < MinLen || g.Len < 3 * g.Thick) return;
        g.Color = g.Runs[g.Runs.Count / 2].Color;
        g.Spread = (int)Math.Sqrt(g.Runs.Max(r => Dist2(r.Color, g.Color)));
        output.Add(g);
    }

    /// <summary>A pixel that could be part of a bar's fill: clearly coloured, or bright.</summary>
    private static bool FillLike(int c)
    {
        int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 80) return false;
        int sat = (max - min) * 255 / max;
        return sat >= 90 || max >= 205;
    }

    private static int Dist2(int a, int b)
    {
        int dr = ((a >> 16) & 255) - ((b >> 16) & 255), dg = ((a >> 8) & 255) - ((b >> 8) & 255), db = (a & 255) - (b & 255);
        return dr * dr + dg * dg + db * db;
    }

    public static string HueName(int argb)
    {
        var c = Color.FromArgb(argb);
        float s = c.GetSaturation(), v = c.GetBrightness(), hue = c.GetHue();
        if (s < 0.25f) return v >= 0.7f ? "white" : "gray";
        return hue < 15 || hue >= 340 ? "red" : hue < 45 ? "orange" : hue < 70 ? "yellow" : hue < 170 ? "green"
             : hue < 200 ? "cyan" : hue < 260 ? "blue" : hue < 300 ? "purple" : "pink";
    }

    private static int[] ReadPixels(Bitmap bmp, out int w, out int h)
    {
        w = bmp.Width; h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[w * h];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w, w);
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }
}
