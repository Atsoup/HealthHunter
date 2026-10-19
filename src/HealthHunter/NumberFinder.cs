using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HealthHunter;

/// <summary>
/// v6: finds numbers the game draws on screen (ammo, gold, XP, level, the stats on a character screen…) without
/// anyone typing them in. Windows OCR reads the game window about once a second; every number that stays at the same
/// place on the HUD is tracked, with the word next to it as its name ("Gold", "Ammo"). When a tracked number changes,
/// an exact-value scan is run for the new value and narrowed on every later change. Once a handful of candidates is
/// left, each is written once (+1) and put back: a copy the game refreshes goes straight back, the real value keeps it
/// and the number on screen follows. Stat-screen numbers that never change are looked for next to the values already
/// found (games keep a character's stats in one block) and tested the same way.
/// Honest limits: numbers OCR can't read (stylised fonts, tiny text) and values shown rounded or scaled
/// (a percentage, "1.2k") are not found.
/// </summary>
public sealed class NumberFinder
{
    public Action<string> Feed;
    public Func<List<TableEntry>> Entries;
    public Action<TableEntry, AssistantItem> Found;
    public Action<List<TableEntry>> Options;
    public Action<AssistantItem> Items;
    public Rectangle Client;
    public string StatusLine { get; private set; } = "starting";
    public Task Completion { get; private set; } = Task.CompletedTask;

    private readonly ProcessMemory _mem;
    private readonly AssistantOptions _opt;
    private CancellationTokenSource _cts;
    private readonly List<HudNumber> _numbers = new();
    private int _pass;
    // HUD counters are often stored more compactly than health (lives/ammo commonly use
    // byte or 16-bit values). Search every supported scalar representation in one pass.
    private static readonly ValueKind[] Kinds =
    {
        ValueKind.Byte, ValueKind.Int16, ValueKind.UInt16, ValueKind.Int32,
        ValueKind.UInt32, ValueKind.Int64, ValueKind.Float, ValueKind.Double
    };
    private const int MaxTracked = 40;
    private const long MaxFirstResults = 8_000_000;
    private const int TestAtMost = 12;
    private static readonly Regex NumberRx = new(@"^\d{1,9}(\.\d{1,3})?$", RegexOptions.Compiled);
    private static readonly Regex LabelRx = new(@"^[A-Za-z][A-Za-z' ]{1,23}$", RegexOptions.Compiled);
    private static readonly string[] SkipLabels = { "fps", "ms", "ping", "latency", "time", "clock", "timer", "temp", "version", "build", "x", "y", "z" };

    /// <summary>One number on the HUD and how far it has been found.</summary>
    private sealed class HudNumber
    {
        public string Label = "";
        public Rectangle Rect;
        public int TextHeight;
        public double Value = double.NaN, Pending = double.NaN, LastScanned = double.NaN;
        public int Seen, Changes, Missed, LastSeenPass;
        public readonly Queue<bool> Recent = new(); // true = changed on that pass
        public bool Noisy, Done, Skip;
        public Scanner Scanner;
        public int Narrowed, GaveUp, TestedAt = -1;
        public DateTime LastScanAt;
        public AssistantItem Item;
        public string Where = "";
        public string Name => Label.Length > 0 ? Label : $"Number ({Where})";
    }

    private readonly record struct Reading(double Value, Rectangle Rect, string Label, int TextHeight);

    public NumberFinder(ProcessMemory mem, AssistantOptions opt) { _mem = mem; _opt = opt; }

    public void Start(CancellationToken outer)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = _cts.Token;
        Completion = Task.Run(() =>
        {
            try { Loop(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Diag.Log("Numbers: " + ex); StatusLine = "stopped: " + ex.Message; }
            finally { foreach (var n in _numbers) n.Scanner?.Dispose(); }
        }, ct);
    }

    public void Stop() => _cts?.Cancel();

    private void Say(string s) { Diag.Log("Numbers: " + s); try { Feed?.Invoke(s); } catch { } }

    private void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            if (Client.Width >= 64 && Client.Height >= 64 && _mem.IsAlive)
            {
                List<Reading> readings;
                using (var bmp = BarDetector.Capture(Client))
                {
                    int scale = (long)Client.Width * Client.Height <= 2_200_000 ? 2 : 1;
                    readings = Parse(Ocr.Read(bmp, scale));
                }
                _pass++;
                Track(readings);
                Work(ct);
                StatusLine = Summary();
            }
            int wait = (int)Math.Max(300, 1200 - sw.ElapsedMilliseconds);
            for (int i = 0; i < wait / 100; i++) { ct.ThrowIfCancellationRequested(); Thread.Sleep(100); }
        }
    }

    // ------------------------------------------------------------------ reading the screen

    private static List<Reading> Parse(List<OcrWord> words)
    {
        var list = new List<Reading>();
        foreach (var line in words.GroupBy(w => w.Line))
        {
            var ws = line.OrderBy(w => w.Rect.X).ToList();
            for (int i = 0; i < ws.Count; i++)
            {
                var w = ws[i];
                string t = w.Text.Trim();
                if (t.Contains(':')) continue; // a time
                bool percent = t.EndsWith('%');
                if (percent) continue;        // a percentage is a derived number; the bar finder covers those
                var parts = t.Split('/');
                for (int p = 0; p < parts.Length && p < 2; p++)
                {
                    string num = parts[p].Replace(",", "").Replace(".", parts.Length == 1 && CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == "," ? "" : ".").Trim();
                    if (!NumberRx.IsMatch(num) || !double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
                    if (v > 1e8) continue;
                    string label = LabelFor(ws, i);
                    if (parts.Length == 2) label = (label.Length > 0 ? label : "Number") + (p == 0 ? "" : " (max)");
                    if (label.Length > 0 && SkipLabels.Contains(label.ToLowerInvariant())) continue;
                    var r = w.Rect;
                    if (parts.Length == 2) { int half = Math.Max(1, r.Width / 2); r = new Rectangle(r.X + p * half, r.Y, half, r.Height); }
                    list.Add(new Reading(v, r, label, w.Rect.Height));
                }
            }
        }
        return list;
    }

    /// <summary>The word(s) just before the number on the same line ("Gold", "Hit points"), or just after ("150 XP").</summary>
    private static string LabelFor(List<OcrWord> line, int i)
    {
        int h = Math.Max(6, line[i].Rect.Height);
        var parts = new List<string>();
        for (int j = i - 1; j >= 0 && parts.Count < 2; j--)
        {
            string t = line[j].Text.Trim().TrimEnd(':', '.', '-', '=');
            if (!LabelRx.IsMatch(t) || line[i].Rect.X - line[j].Rect.Right > 4 * h) break;
            parts.Insert(0, t);
        }
        if (parts.Count == 0 && i + 1 < line.Count)
        {
            string t = line[i + 1].Text.Trim().TrimEnd(':', '.', '-', '=');
            if (LabelRx.IsMatch(t) && t.Length >= 2 && line[i + 1].Rect.X - line[i].Rect.Right <= 2 * h) parts.Add(t);
        }
        string label = string.Join(" ", parts).Trim();
        if (label.Length <= 1) return "";
        return label.Length > 24 ? label[..24] : label;
    }

    private void Track(List<Reading> readings)
    {
        var matched = new HashSet<HudNumber>();
        foreach (var r in readings)
        {
            HudNumber best = null; double bestD = double.MaxValue;
            var c = new Point(r.Rect.X + r.Rect.Width / 2, r.Rect.Y + r.Rect.Height / 2);
            foreach (var n in _numbers)
            {
                if (matched.Contains(n)) continue;
                if (n.Label.Length > 0 && r.Label.Length > 0 && !string.Equals(n.Label, r.Label, StringComparison.OrdinalIgnoreCase)) continue;
                if (Math.Abs(n.TextHeight - r.TextHeight) > Math.Max(4, n.TextHeight / 3)) continue;
                var nc = new Point(n.Rect.X + n.Rect.Width / 2, n.Rect.Y + n.Rect.Height / 2);
                double d = Math.Abs(nc.Y - c.Y) * 2 + Math.Abs(nc.X - c.X);
                double limit = Math.Max(24, n.TextHeight * 3);
                if (Math.Abs(nc.Y - c.Y) > n.TextHeight && Math.Abs(nc.Y - c.Y) > 10) continue;
                if (d < bestD && Math.Abs(nc.X - c.X) <= limit) { bestD = d; best = n; }
            }
            if (best == null)
            {
                if (_numbers.Count >= MaxTracked) continue;
                best = new HudNumber { Label = r.Label, Rect = r.Rect, TextHeight = r.TextHeight, Where = DetectedBar.Place(r.Rect, Client.Size) };
                _numbers.Add(best);
            }
            matched.Add(best);
            best.Rect = r.Rect; best.TextHeight = r.TextHeight;
            if (best.Label.Length == 0 && r.Label.Length > 0) best.Label = r.Label;
            best.Seen++; best.Missed = 0; best.LastSeenPass = _pass;
            // A reading has to repeat once before it counts: OCR misreads a digit now and then.
            bool changed = false;
            if (r.Value == best.Pending && r.Value != best.Value)
            {
                if (!double.IsNaN(best.Value)) { best.Changes++; changed = true; }
                best.Value = r.Value;
            }
            best.Pending = r.Value;
            best.Recent.Enqueue(changed);
            while (best.Recent.Count > 12) best.Recent.Dequeue();
            best.Noisy = best.Recent.Count >= 8 && best.Recent.Count(x => x) >= 5;
        }
        foreach (var n in _numbers) if (!matched.Contains(n)) n.Missed++;
        // Numbers that were a passing thing (damage pop-ups, pickups) go; a HUD number that's just hidden by a menu stays.
        _numbers.RemoveAll(n => !n.Done && n.Scanner == null && n.Missed > (n.Changes > 0 ? 300 : 20));
    }

    // ------------------------------------------------------------------ finding

    private void Work(CancellationToken ct)
    {
        var entries = Entries?.Invoke() ?? new();
        foreach (var n in _numbers.Where(n => !n.Done && !n.Skip && !n.Noisy && !double.IsNaN(n.Value)).OrderByDescending(n => n.Changes).ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (n.Missed > 0 && n.Changes == 0) continue;
            if (n.Item == null && (n.Changes >= 1 || (n.Label.Length > 0 && n.Seen >= 3)))
            {
                // A value already in the table with this name and this value is this number: nothing to find.
                var known = entries.FirstOrDefault(e => string.Equals(e.Name, n.Name, StringComparison.OrdinalIgnoreCase));
                if (known != null && known.Resolve(_mem) is long ka && _mem.TryReadValue(ka, known.Kind, out double kv) && Math.Abs(kv - n.Value) < 0.5)
                {
                    n.Done = true; n.Skip = true;
                    Say($"{n.Name} ({n.Value:0.##}) is already in the cheat table from an earlier session.");
                    continue;
                }
                n.Item = new AssistantItem { Name = n.Name, Source = $"number '{n.Name}' at the {n.Where}", State = "waiting", Detail = $"reads {Fmt(n.Value)}" };
                try { Items?.Invoke(n.Item); } catch { }
                Say($"Tracking the number '{n.Name}' at the {n.Where} (reads {Fmt(n.Value)}).");
            }
            if (n.Item == null) continue;
            if (n.GaveUp >= 2) { n.Skip = true; continue; }

            if (n.Scanner == null)
            {
                if (n.Changes >= 1) { FirstScan(n, ct); }
                else if (n.Seen >= 3 && n.Label.Length > 0 && n.TestedAt < 0) NearKnown(n, entries, ct);
                continue;
            }
            if (n.Value != n.LastScanned)
            {
                Narrow(n, ct);
            }
            else if ((DateTime.Now - n.LastScanAt).TotalSeconds > 20 && n.Scanner.ResultCount <= 2_000_000 && n.Scanner.ResultCount > TestAtMost)
            {
                Narrow(n, ct); // the same value again: throws out what drifted meanwhile
            }
            if (n.Scanner.HasScan && !n.Scanner.InSnapshotMode && n.Scanner.ResultCount > 0 && n.Scanner.ResultCount <= TestAtMost &&
                n.Narrowed >= 1 && n.TestedAt != n.Scanner.ScanCount)
            {
                n.TestedAt = n.Scanner.ScanCount;
                Test(n, ct);
            }
            else if (n.Scanner.HasScan && n.Scanner.ResultCount == 0)
            {
                n.Scanner.Dispose(); n.Scanner = null; n.Narrowed = 0; n.GaveUp++;
                n.Item.State = "waiting"; n.Item.Detail = "nothing in memory followed it; will try again on the next change";
                Say($"{n.Name}: nothing in memory followed the number through its changes (it may be shown rounded or scaled). Will try again.");
            }
        }
    }

    private static string Fmt(double v) => v.ToString(v == Math.Floor(v) ? "N0" : "0.###", CultureInfo.InvariantCulture);

    private void FirstScan(HudNumber n, CancellationToken ct)
    {
        n.Scanner = new Scanner(_mem) { Aligned = true, KeepUndo = false, PlausibleHealthFilter = true, FloatTolerance = 0.5, IncludeMappedMemory = AutoFinder.ShouldScanMappedMemory(_mem) };
        n.Item.State = "finding"; n.Item.Detail = $"scanning for {Fmt(n.Value)}…";
        var sw = Stopwatch.StartNew();
        n.Scanner.FirstScan(Kinds, ScanType.ExactValue, n.Value, 0, null, ct);
        n.LastScanned = n.Value; n.LastScanAt = DateTime.Now; n.Narrowed = 0;
        if (n.Scanner.ResultCount > MaxFirstResults)
        {
            Say($"{n.Name}: {n.Scanner.ResultCount:N0} places hold {Fmt(n.Value)}; too many to keep. Waiting for the next change.");
            n.Scanner.Dispose(); n.Scanner = null;
            n.Item.Detail = "too common a value; waiting for it to change";
            return;
        }
        n.Item.Detail = $"{n.Scanner.ResultCount:N0} candidates hold {Fmt(n.Value)} ({sw.Elapsed.TotalSeconds:0.0} s); waiting for it to change";
        Say($"{n.Name}: exact-value scan for {Fmt(n.Value)} across {ValueKindExt.KindsLabel(Kinds)} kept {n.Scanner.ResultCount:N0} candidate(s) in {sw.Elapsed.TotalSeconds:0.0} s.");
    }

    private void Narrow(HudNumber n, CancellationToken ct)
    {
        n.Item.Detail = $"now {Fmt(n.Value)}: narrowing…";
        n.Scanner.NextScan(ScanType.ExactValue, n.Value, 0, null, ct, null, revertIfEmpty: true);
        n.LastScanned = n.Value; n.LastScanAt = DateTime.Now;
        if (!n.Scanner.LastScanReverted) n.Narrowed++;
        n.Item.Detail = n.Scanner.LastScanReverted
            ? $"nothing held {Fmt(n.Value)} (misread, or shown rounded); keeping {n.Scanner.ResultCount:N0} candidates"
            : $"{n.Scanner.ResultCount:N0} candidate(s) after {n.Narrowed} change(s)";
        Say($"{n.Name}: narrowing scan {n.Narrowed} for {Fmt(n.Value)} left {n.Scanner.ResultCount:N0} candidate(s)" + (n.Scanner.LastScanReverted ? " (empty result reverted; likely an OCR misread or rounded display)." : "."));
    }

    /// <summary>A number that never changes (a stat on a character screen): look for it next to the values already found.</summary>
    private void NearKnown(HudNumber n, List<TableEntry> entries, CancellationToken ct)
    {
        var anchors = entries.Where(e => e.Resolve(_mem) is long).Select(e => e.Resolve(_mem).Value).Distinct().ToList();
        n.TestedAt = 0; // once per sighting
        if (anchors.Count == 0) { n.Item.Detail = "doesn't change; will look next to the values found so far once there are some"; return; }
        var found = new List<(long Addr, ValueKind Kind)>();
        foreach (var a in anchors)
        {
            using var s = new Scanner(_mem) { Aligned = true, KeepUndo = false, PlausibleHealthFilter = true, FloatTolerance = 0.5, RangeStart = Math.Max(0x10000, a - 0x2000), RangeEnd = a + 0x2000 };
            s.FirstScan(Kinds, ScanType.ExactValue, n.Value, 0, null, ct);
            for (int i = 0; i < s.ResultCount; i++) found.Add((s.AddressAt(i), s.KindAt(i)));
        }
        found = found.Distinct().ToList();
        if (found.Count == 0 || found.Count > 8)
        {
            n.Item.Detail = found.Count == 0 ? $"doesn't change and isn't stored near the values found so far ({Fmt(n.Value)})" : $"{found.Count} places near the known values hold {Fmt(n.Value)}; too many to test";
            return;
        }
        Say($"{n.Name} ({Fmt(n.Value)}) never changes, but {found.Count} place(s) near the values already found hold it. Testing those.");
        n.Scanner = new Scanner(_mem) { Aligned = true, KeepUndo = false, PlausibleHealthFilter = true, FloatTolerance = 0.5 };
        n.Scanner.RangeStart = found.Min(f => f.Addr); n.Scanner.RangeEnd = found.Max(f => f.Addr) + 8;
        n.Scanner.FirstScan(Kinds, ScanType.ExactValue, n.Value, 0, null, ct);
        n.LastScanned = n.Value; n.LastScanAt = DateTime.Now; n.Narrowed = 1;
        if (n.Scanner.ResultCount > TestAtMost) { n.Scanner.Dispose(); n.Scanner = null; n.Narrowed = 0; return; }
        n.TestedAt = n.Scanner.ScanCount;
        Test(n, ct);
    }

    /// <summary>
    /// Writes each candidate once (+1) and watches: a copy goes straight back; the real value keeps it and the number on
    /// screen follows. Everything is put back before the next one is tried.
    /// </summary>
    private void Test(HudNumber n, CancellationToken ct)
    {
        var s = n.Scanner;
        var cands = new List<(long Addr, ValueKind Kind)>();
        var guards = new Dictionary<long, long>();
        for (int i = 0; i < s.ResultCount; i++)
        {
            long a = s.AddressAt(i); var k = s.KindAt(i);
            if (AutoFinder.InThreadStack(_mem, a, guards)) { Diag.Log($"Numbers: not testing {a:X} ({k}): on a thread stack."); continue; }
            cands.Add((a, k));
        }
        if (cands.Count == 0) { n.Item.Detail = "only thread-stack temporaries were left; waiting for the next change"; return; }
        n.Item.State = "testing"; n.Item.Detail = $"testing {cands.Count} candidate(s) by changing each by 1 for a moment";
        Diag.Log($"Numbers: {n.Name} = {Fmt(n.Value)}: testing " + string.Join(", ", cands.Select(c => $"{c.Addr:X} {c.Kind}")));
        var shown = new List<(long Addr, ValueKind Kind)>();
        var kept = new List<(long Addr, ValueKind Kind)>();
        foreach (var (addr, kind) in cands)
        {
            ct.ThrowIfCancellationRequested();
            if (n.Missed > 0) { n.Item.Detail = "the number left the screen during the test; will test again"; n.TestedAt = -1; return; }
            // Read and write with the game paused for that instant so a change of the game's own can't slip in between.
            bool paused = _mem.Suspend();
            double cur; bool ok;
            try
            {
                ok = _mem.TryReadValue(addr, kind, out cur) && Math.Abs(cur - n.Value) < 0.5 && _mem.WriteValue(addr, kind, cur + 1);
            }
            finally { if (paused) _mem.Resume(); }
            if (!ok) { Diag.Log($"Numbers: {addr:X} skipped (value moved or write failed)."); continue; }
            Thread.Sleep(250);
            _mem.TryReadValue(addr, kind, out double after);
            string verdict;
            if (Math.Abs(after - cur) < 0.5) verdict = "reverted (a copy)";
            else if (Math.Abs(after - (cur + 1)) < 0.5)
            {
                kept.Add((addr, kind));
                bool seen = false;
                for (int t = 0; t < 4 && !seen; t++)
                {
                    Thread.Sleep(t == 0 ? 150 : 350);
                    var r = n.Rect; r.Inflate(r.Width / 2 + n.TextHeight, n.TextHeight);
                    r.Intersect(new Rectangle(0, 0, Client.Width, Client.Height));
                    if (r.Width < 4 || r.Height < 4) break;
                    using var bmp = BarDetector.Capture(new Rectangle(Client.X + r.X, Client.Y + r.Y, r.Width, r.Height));
                    foreach (var rd in Parse(Ocr.Read(bmp, 2)))
                        if (Math.Abs(rd.Value - (cur + 1)) < 0.5) seen = true;
                }
                if (seen) shown.Add((addr, kind));
                verdict = seen ? "kept, and the screen showed it (real)" : "kept, but the screen didn't change";
            }
            else verdict = $"changed on its own to {kind.Format(after)}";
            // Put it back, relative to where it is now in case the game moved it meanwhile.
            paused = _mem.Suspend();
            try
            {
                if (_mem.TryReadValue(addr, kind, out double now) && Math.Abs(now - cur) >= 0.5) _mem.WriteValue(addr, kind, now - 1);
            }
            finally { if (paused) _mem.Resume(); }
            Diag.Log($"Numbers: wrote {kind.Format(cur + 1)} once to {addr:X} ({kind}): {verdict}.");
        }
        if (shown.Count == 1 || (shown.Count == 0 && kept.Count == 1 && n.Changes >= 2))
        {
            var (addr, kind) = shown.Count == 1 ? shown[0] : kept[0];
            Accept(n, addr, kind, shown.Count == 1 ? "the number on screen followed the test write" : "it kept the test write (the screen didn't show it; it may be drawn from a copy)", ct);
            return;
        }
        Diag.Log($"Numbers: confirmation summary for {n.Name}: {cands.Count} tested, {shown.Count} screen-confirmed, {kept.Count} kept writes total.");
        if (shown.Count > 1 || kept.Count > 1)
        {
            var pool = shown.Count > 1 ? shown : kept;
            n.Done = true; n.Item.State = "options"; n.Item.Detail = $"{pool.Count} values kept the test write; added as options (not frozen)";
            var opts = new List<TableEntry>();
            char letter = 'A';
            foreach (var (addr, kind) in pool)
                opts.Add(new TableEntry { Name = $"{n.Name} option {letter++}", Kind = kind, RawAddress = addr, MaxValue = Math.Max(1, n.Value), Mode = FreezeMode.AllowIncrease, FreezeValue = n.Value });
            Say($"{n.Name}: {pool.Count} values behaved like the real one; added to the cheat table as options A/B. Freeze one and see which the game uses.");
            try { Options?.Invoke(opts); } catch { }
            s.Dispose(); n.Scanner = null;
            return;
        }
        n.Item.State = "finding"; n.Item.Detail = $"none of {cands.Count} candidates held a test write; waiting for the next change";
        Say($"{n.Name}: none of the candidates kept a test write (all copies); waiting for it to change again.");
    }

    private void Accept(HudNumber n, long addr, ValueKind kind, string why, CancellationToken ct)
    {
        var e = new TableEntry
        {
            Name = n.Name, Kind = kind, RawAddress = addr, MaxValue = Math.Max(1, n.Value), Mode = FreezeMode.AllowIncrease, FreezeValue = n.Value
        };
        n.Item.State = "found"; n.Item.Detail = "finding a pointer path…";
        var prog = new Progress2(m => n.Item.Detail = m);
        var (keptPaths, fromEarlier) = AutoFinder.AttachPointerPath(_mem, e, n.Name, _opt.PointerDepth, _opt.PointerMaxOffset, prog, ct);
        n.Done = true; n.Item.Entry = e;
        n.Item.Detail = fromEarlier ? $"{keptPaths} saved pointer path(s) still work" : keptPaths == 0 ? "no pointer path (works until the game restarts)" : $"{keptPaths} candidate pointer path(s)";
        Say($"{n.Name} found: {kind} at {e.Describe()} ({why}). {n.Item.Detail}.");
        try { Found?.Invoke(e, n.Item); } catch (Exception ex) { Diag.Log("Numbers: Found callback: " + ex.Message); }
        n.Scanner?.Dispose(); n.Scanner = null;
    }

    private string Summary()
    {
        int tracked = _numbers.Count(n => n.Item != null && !n.Done), found = _numbers.Count(n => n.Done && n.Item?.Entry != null);
        var active = _numbers.FirstOrDefault(n => n.Item != null && !n.Done && n.Scanner != null);
        string s = $"{_numbers.Count} on screen, {tracked} tracked, {found} found";
        if (active != null) s += $"; {active.Name}: {active.Item.Detail}";
        return s;
    }

    private sealed class Progress2 : IProgress<string>
    {
        private readonly Action<string> _a;
        public Progress2(Action<string> a) { _a = a; }
        public void Report(string value) => _a(value);
    }
}
