using System.Collections.Concurrent;

namespace HealthHunter;

public enum ScanType
{
    ExactValue,
    UnknownInitialValue,
    Decreased,
    Increased,
    DecreasedBy,
    IncreasedBy,
    Changed,
    Unchanged,
    BiggerThan,
    SmallerThan,
    Between,
    SameAsFirst
}

public static class ScanTypeExt
{
    public static bool NeedsValue(this ScanType t) =>
        t is ScanType.ExactValue or ScanType.DecreasedBy or ScanType.IncreasedBy or ScanType.BiggerThan or ScanType.SmallerThan or ScanType.Between;
    public static bool NeedsSecondValue(this ScanType t) => t == ScanType.Between;
    /// <summary>Types that can start a scan (they compare against a typed value, not a previous one).</summary>
    public static bool IsFirstScanType(this ScanType t) =>
        t is ScanType.ExactValue or ScanType.UnknownInitialValue or ScanType.BiggerThan or ScanType.SmallerThan or ScanType.Between;
    public static string Label(this ScanType t) => t switch
    {
        ScanType.ExactValue => "Exact value",
        ScanType.UnknownInitialValue => "Unknown initial value",
        ScanType.Decreased => "Decreased (took damage)",
        ScanType.Increased => "Increased (healed)",
        ScanType.DecreasedBy => "Decreased by…",
        ScanType.IncreasedBy => "Increased by…",
        ScanType.Changed => "Changed",
        ScanType.Unchanged => "Unchanged",
        ScanType.BiggerThan => "Bigger than…",
        ScanType.SmallerThan => "Smaller than…",
        ScanType.Between => "Value between… and…",
        ScanType.SameAsFirst => "Same as first scan",
        _ => t.ToString()
    };
}

/// <summary>
/// Classic narrowing scanner: first scan (exact or snapshot), then repeated next scans that
/// keep only addresses whose value behaved the way health did.
///
/// v5: one scan can track several value types at once (Auto = Int32 + Float + Double), so the user
/// no longer has to guess the type, and every read goes through ProcessMemory.ReadPaged so one
/// unreadable page can't silently drop the real address from the results.
/// </summary>
public sealed class Scanner : IDisposable
{
    private const int ChunkSize = 1 << 20;       // 1 MB reads
    private const int BlockWindow = 64 * 1024;   // group nearby results into one read
    private const int Stride = 8;                // bytes stored per result (room for any type)
    private const int OkLen = ChunkSize / ProcessMemory.PageSize + 4;

    private sealed class Snap { public long Base; public long Size; public string File; }

    /// <summary>Everything a next scan replaces, kept so it can be undone. Arrays are never mutated, only swapped.</summary>
    private sealed class State
    {
        public long[] Addrs; public byte[] Kinds; public byte[] Prev; public byte[] First;
        public List<Snap> Snaps; public string SnapDir; public int ScanCount;
    }
    private const int MaxUndo = 5;
    private const long MaxUndoResults = 2_000_000; // larger result sets are not kept for undo (memory)
    private readonly List<State> _undo = new();

    private readonly ProcessMemory _mem;
    private List<Snap> _snaps;                    // set while in "unknown initial value" mode
    private long[] _addrs = Array.Empty<long>();  // set once narrowed to addresses
    private byte[] _kinds = Array.Empty<byte>();  // ValueKind of each result
    private byte[] _prev = Array.Empty<byte>();   // Stride bytes per result
    private byte[] _first = Array.Empty<byte>();  // value at the first scan, for "Same as first scan"
    private string _snapDir;
    private int _step = 4;
    private ValueKind[] _kindSet = { ValueKind.Int32 };

    /// <summary>The first (or only) type being scanned.</summary>
    public ValueKind Kind => _kindSet[0];
    public IReadOnlyList<ValueKind> Kinds => _kindSet;
    public bool MultiKind => _kindSet.Length > 1;
    public bool HasScan { get; private set; }
    public bool InSnapshotMode => _snaps != null;
    public int ScanCount { get; private set; }

    /// <summary>Reject values a health counter would never hold (NaN, negative, absurdly large, float junk).</summary>
    public bool PlausibleHealthFilter { get; set; } = true;
    public bool IncludeMappedMemory { get; set; } = false;
    /// <summary>"Unchanged" also accepts a change up to this fraction of the value (0 = exact). Auto-find uses it so a
    /// slight drift the bar can't show (slow regeneration, rounding) doesn't throw the real value out.</summary>
    public double UnchangedRelTol { get; set; }
    /// <summary>Fast scan: step by the type's natural alignment. Off = check every byte (slower, finds packed values).</summary>
    public bool Aligned { get; set; } = true;
    /// <summary>Optional address range limit for first scans (0 = no limit).</summary>
    public long RangeStart { get; set; }
    public long RangeEnd { get; set; }
    /// <summary>Upper bound for "Value between".</summary>
    public double Value2 { get; set; }
    /// <summary>Minimum tolerance for Float/Double in exact-value style scans: on-screen numbers are rounded.</summary>
    public double FloatTolerance { get; set; } = 0.5;

    /// <summary>Keep previous results so "Undo scan" works. Auto-find turns this off to save memory.</summary>
    public bool KeepUndo { get; set; } = true;
    public bool CanUndo => _undo.Count > 0;
    public int UndoDepth => _undo.Count;

    /// <summary>One line describing the last scan, for the status bar and the diagnostics log.</summary>
    public string LastSummary { get; private set; } = "";

    public Scanner(ProcessMemory mem) { _mem = mem; }

    // Bar-ratio filter: when set, Decreased/Increased scans also require value/barFraction
    // (the implied max health) to stay the same before and after. Removes most false hits.
    private bool _ratioOn;
    private double _rPrev, _rCur, _rTol;

    // Read statistics for the current scan.
    private long _bytesRead, _bytesBad;

    /// <summary>True when the last next-scan matched nothing and was rolled back.</summary>
    public bool LastScanReverted { get; private set; }

    public long ResultCount => InSnapshotMode ? _snaps.Sum(s => s.Size) / _step * _kindSet.Length : _addrs.Length;

    public long AddressAt(int i) => _addrs[i];
    public ValueKind KindAt(int i) => (ValueKind)_kinds[i];
    public double PreviousAt(int i) => KindAt(i).Decode(_prev.AsSpan(i * Stride, Stride));
    /// <summary>Value at the first scan (the snapshot), or NaN when it isn't kept.</summary>
    public double FirstAt(int i) => _first.Length == _addrs.Length * Stride ? KindAt(i).Decode(_first.AsSpan(i * Stride, Stride)) : double.NaN;

    /// <summary>How many results are of each type (address mode only).</summary>
    public Dictionary<ValueKind, int> CountByKind()
    {
        var d = _kindSet.ToDictionary(k => k, _ => 0);
        foreach (var b in _kinds) { var k = (ValueKind)b; d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1; }
        return d;
    }

    public string CountByKindText()
    {
        if (InSnapshotMode || !MultiKind) return "";
        return string.Join(", ", CountByKind().Select(kv => $"{kv.Key} {kv.Value:N0}"));
    }

    public void Reset()
    {
        foreach (var st in _undo) DeleteDir(st.SnapDir);
        _undo.Clear();
        DeleteSnapshots();
        _addrs = Array.Empty<long>();
        _kinds = Array.Empty<byte>();
        _prev = Array.Empty<byte>();
        _first = Array.Empty<byte>();
        HasScan = false;
        ScanCount = 0;
    }

    // ---------------------------------------------------------------- undo

    private State Capture() => new() { Addrs = _addrs, Kinds = _kinds, Prev = _prev, First = _first, Snaps = _snaps, SnapDir = _snapDir, ScanCount = ScanCount };

    private void PushUndo()
    {
        if (!KeepUndo || (!InSnapshotMode && _addrs.Length > MaxUndoResults))
        {
            // Too big to keep a copy around: undo isn't possible past this point.
            foreach (var st in _undo) if (st.SnapDir != _snapDir) DeleteDir(st.SnapDir);
            _undo.Clear();
            return;
        }
        _undo.Add(Capture());
        if (_undo.Count > MaxUndo)
        {
            var old = _undo[0];
            _undo.RemoveAt(0);
            if (old.SnapDir != null && old.SnapDir != _snapDir && _undo.All(u => u.SnapDir != old.SnapDir)) DeleteDir(old.SnapDir);
        }
    }

    /// <summary>Forget the state pushed for a scan that changed nothing (reverted or cancelled).</summary>
    private void DropLastUndo()
    {
        if (_undo.Count > 0) _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>Goes back to the results before the last next scan. Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var st = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _addrs = st.Addrs; _kinds = st.Kinds; _prev = st.Prev; _first = st.First;
        _snaps = st.Snaps; _snapDir = st.SnapDir; ScanCount = st.ScanCount;
        return true;
    }

    private static void DeleteDir(string dir) => SnapshotRefs.Release(dir);

    /// <summary>
    /// v6: snapshot folders can be shared between scanners (the assistant takes one snapshot of the game and narrows it
    /// towards several bars at once), so a folder is only deleted when the last scanner using it lets go.
    /// </summary>
    private static class SnapshotRefs
    {
        private static readonly Dictionary<string, int> Refs = new();
        public static void Retain(string dir) { if (dir == null) return; lock (Refs) Refs[dir] = Refs.TryGetValue(dir, out int n) ? n + 1 : 1; }
        public static void Release(string dir)
        {
            if (dir == null) return;
            lock (Refs)
            {
                if (Refs.TryGetValue(dir, out int n) && n > 1) { Refs[dir] = n - 1; return; }
                Refs.Remove(dir);
            }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// A second scanner that starts from this scanner's snapshot (which must not have been narrowed yet). Each copy
    /// narrows on its own; the snapshot files stay until the last copy is done with them.
    /// </summary>
    public Scanner Fork()
    {
        if (!InSnapshotMode) throw new InvalidOperationException("Only a scanner still holding a snapshot can be forked.");
        var s = new Scanner(_mem)
        {
            _snaps = _snaps, _snapDir = _snapDir, _step = _step, _kindSet = _kindSet, HasScan = true, ScanCount = ScanCount,
            PlausibleHealthFilter = PlausibleHealthFilter, IncludeMappedMemory = IncludeMappedMemory, UnchangedRelTol = UnchangedRelTol,
            Aligned = Aligned, FloatTolerance = FloatTolerance, KeepUndo = false
        };
        SnapshotRefs.Retain(_snapDir);
        return s;
    }

    private bool Plausible(ValueKind k, double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return false;
        if (!PlausibleHealthFilter) return true;
        if (v < 0 || v > 10_000_000) return false;
        // A float that is tiny but not zero is almost always an integer's bits read as a float (denormal),
        // or random data. Rejecting it stops the same int from also showing up as a "Float" result.
        if (k.IsFloating() && v != 0 && v < 1e-4) return false;
        return true;
    }

    private double TolFor(ValueKind k, double tol) => k.IsFloating() ? Math.Max(tol, FloatTolerance) : tol;

    private bool Match(ValueKind k, ScanType t, double cur, double prev, double v, double tol, double first = double.NaN)
    {
        if (!Plausible(k, cur)) return false;
        const double eps = 1e-6;
        if (_ratioOn && t is ScanType.Decreased or ScanType.Increased)
        {
            if (cur <= 0 || prev <= 0) return false;
            double m1 = prev / _rPrev, m2 = cur / _rCur;
            if (Math.Abs(m1 - m2) > _rTol * Math.Max(m1, m2)) return false;
        }
        double tl = TolFor(k, tol);
        return t switch
        {
            ScanType.ExactValue => Math.Abs(cur - v) <= tl,
            ScanType.Decreased => cur < prev - eps,
            ScanType.Increased => cur > prev + eps,
            ScanType.DecreasedBy => Math.Abs((prev - cur) - v) <= tl,
            ScanType.IncreasedBy => Math.Abs((cur - prev) - v) <= tl,
            ScanType.Changed => Math.Abs(cur - prev) > eps,
            ScanType.Unchanged => Math.Abs(cur - prev) <= Math.Max(Math.Max(tol, eps), UnchangedRelTol * Math.Abs(prev)),
            ScanType.BiggerThan => cur > v,
            ScanType.SmallerThan => cur < v,
            ScanType.Between => cur >= Math.Min(v, Value2) - tol && cur <= Math.Max(v, Value2) + tol,
            ScanType.SameAsFirst => !double.IsNaN(first) && Math.Abs(cur - first) <= Math.Max(tol, eps),
            _ => true
        };
    }

    /// <summary>Whether a value of this type may start at this address under the current alignment setting.</summary>
    private bool AlignOk(ValueKind k, long addr) => !Aligned || (addr & (k.Alignment() - 1)) == 0;

    // ---------------------------------------------------------------- first scan

    public void FirstScan(ValueKind kind, ScanType type, double value, double tol,
                          IProgress<double> progress, CancellationToken ct)
        => FirstScan(new[] { kind }, type, value, tol, progress, ct);

    public void FirstScan(ValueKind[] kinds, ScanType type, double value, double tol,
                          IProgress<double> progress, CancellationToken ct)
    {
        Reset();
        _kindSet = kinds.Distinct().ToArray();
        _step = Aligned ? _kindSet.Min(k => k.Alignment()) : 1;
        _bytesRead = _bytesBad = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var regions = ClipToRange(_mem.GetRegions(writableOnly: true, includeMapped: IncludeMappedMemory));
        long total = regions.Sum(r => r.Size);
        if (type == ScanType.UnknownInitialValue)
            TakeSnapshots(regions, progress, ct);
        else
            ExactFirstScan(regions, type, value, tol, progress, ct);
        HasScan = true;
        ScanCount = 1;
        LastSummary = $"First scan ({type.Label()}, {KindsLabel(_kindSet)}{(Aligned ? "" : ", unaligned")}): " +
                      $"{regions.Count:N0} regions, {Diag.Mb(total)} scanned, {Diag.Mb(_bytesBad)} unreadable, {sw.Elapsed.TotalSeconds:0.0} s → " +
                      (InSnapshotMode ? "snapshot taken" : $"{_addrs.Length:N0} results" + (MultiKind ? $" ({CountByKindText()})" : ""));
        Diag.Log(LastSummary);
    }

    private static string KindsLabel(IReadOnlyList<ValueKind> k) => ValueKindExt.KindsLabel(k);

    private List<MemRegion> ClipToRange(List<MemRegion> regions)
    {
        if (RangeStart <= 0 && RangeEnd <= 0) return regions;
        long lo = Math.Max(0, RangeStart), hi = RangeEnd > 0 ? RangeEnd : long.MaxValue;
        var list = new List<MemRegion>();
        foreach (var r in regions)
        {
            long b = Math.Max(r.Base, lo), e = Math.Min(r.End, hi);
            if (e > b) list.Add(new MemRegion(b, e - b, r.Type));
        }
        return list;
    }

    private static List<(long Base, int Len, long RegionEnd)> Chunk(List<MemRegion> regions)
    {
        var chunks = new List<(long, int, long)>();
        foreach (var r in regions)
            for (long a = r.Base; a < r.End; a += ChunkSize)
                chunks.Add((a, (int)Math.Min(ChunkSize, r.End - a), r.End));
        return chunks;
    }

    private sealed class Hits
    {
        public long Start;
        public readonly List<long> A = new();
        public readonly List<byte> K = new();
        public readonly List<byte> V = new();
        public readonly List<byte> F = new();
        public void Add(long addr, ValueKind k, ReadOnlySpan<byte> v, ReadOnlySpan<byte> first, bool withFirst)
        {
            A.Add(addr); K.Add((byte)k);
            AddPadded(V, v);
            if (withFirst) AddPadded(F, first);
        }
        private static void AddPadded(List<byte> list, ReadOnlySpan<byte> v)
        {
            for (int q = 0; q < Stride; q++) list.Add(q < v.Length ? v[q] : (byte)0);
        }
    }

    private void Collect(IEnumerable<Hits> hits, bool withFirst)
    {
        var ordered = hits.Where(h => h != null).OrderBy(h => h.Start).ToList();
        int total = ordered.Sum(r => r.A.Count);
        _addrs = new long[total];
        _kinds = new byte[total];
        _prev = new byte[total * Stride];
        _first = withFirst ? new byte[total * Stride] : Array.Empty<byte>();
        int i = 0;
        foreach (var r in ordered)
        {
            r.A.CopyTo(_addrs, i);
            r.K.CopyTo(_kinds, i);
            r.V.CopyTo(_prev, i * Stride);
            if (withFirst) r.F.CopyTo(_first, i * Stride);
            i += r.A.Count;
        }
    }

    private void ExactFirstScan(List<MemRegion> regions, ScanType type, double value, double tol,
                                IProgress<double> progress, CancellationToken ct)
    {
        var kinds = _kindSet;
        int step = _step;
        var chunks = Chunk(regions);
        var results = new ConcurrentBag<Hits>();
        int done = 0;
        Parallel.ForEach(chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            () => (buf: new byte[ChunkSize + 8], ok: new bool[OkLen]),
            (c, _, t) =>
            {
                // Read 8 extra bytes so a value straddling the chunk edge is still seen.
                int want = (int)Math.Min(c.Len + 8, c.RegionEnd - c.Base);
                int bad = _mem.ReadPaged(c.Base, t.buf, want, t.ok);
                Interlocked.Add(ref _bytesRead, c.Len);
                if (bad > 0) Interlocked.Add(ref _bytesBad, Math.Min(bad, c.Len));
                Hits h = null;
                for (int off = 0; off < c.Len; off += step)
                {
                    long addr = c.Base + off;
                    foreach (var k in kinds)
                    {
                        int size = k.Size();
                        if (off + size > want || !AlignOk(k, addr) || !ProcessMemory.PagesOk(t.ok, c.Base, off, size)) continue;
                        var span = t.buf.AsSpan(off, size);
                        if (Match(k, type, k.Decode(span), 0, value, tol))
                        {
                            h ??= new Hits { Start = c.Base };
                            h.Add(addr, k, span, default, false);
                        }
                    }
                }
                if (h != null) results.Add(h);
                int d = Interlocked.Increment(ref done);
                if ((d & 63) == 0) progress?.Report((double)d / chunks.Count);
                return t;
            }, _ => { });

        Collect(results, false);
        _first = _prev; // never mutated, so sharing is safe
        progress?.Report(1);
    }

    private void TakeSnapshots(List<MemRegion> regions, IProgress<double> progress, CancellationToken ct)
    {
        _snapDir = Path.Combine(Path.GetTempPath(), "HealthHunter", "snap_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_snapDir);
        SnapshotRefs.Retain(_snapDir);
        _snaps = new List<Snap>();
        long total = regions.Sum(r => r.Size), done = 0;
        var buf = new byte[ChunkSize];
        var ok = new bool[OkLen];
        int idx = 0;
        foreach (var r in regions)
        {
            ct.ThrowIfCancellationRequested();
            var snap = new Snap { Base = r.Base, Size = r.Size, File = Path.Combine(_snapDir, $"{idx++}.bin") };
            using (var fs = new FileStream(snap.File, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                for (long a = r.Base; a < r.End; a += ChunkSize)
                {
                    // Regions can be gigabytes (an emulator's console memory), and the game may be paused meanwhile, so
                    // Stop has to work in the middle of one.
                    ct.ThrowIfCancellationRequested();
                    int len = (int)Math.Min(ChunkSize, r.End - a);
                    _bytesBad += _mem.ReadPaged(a, buf, len, ok);
                    fs.Write(buf, 0, len);
                    done += len;
                }
            }
            _snaps.Add(snap);
            progress?.Report((double)done / Math.Max(1, total));
        }
        _bytesRead = total;
    }

    private void DeleteSnapshots()
    {
        _snaps = null;
        if (_snapDir != null && _undo.All(u => u.SnapDir != _snapDir)) DeleteDir(_snapDir);
        _snapDir = null;
    }

    // ---------------------------------------------------------------- next scan

    /// <summary>
    /// Re-reads every result's current value as its "previous" value, without dropping anything. Auto-find calls
    /// this after it has written test values, so the next Decreased/Increased compares against what is there now.
    /// </summary>
    public void RefreshPrevious()
    {
        if (!HasScan || InSnapshotMode) return;
        var buf = new byte[Stride];
        for (int i = 0; i < _addrs.Length; i++)
        {
            int size = KindAt(i).Size();
            Array.Clear(buf);
            if (_mem.Read(_addrs[i], buf, 0, size) == size) buf.CopyTo(_prev, i * Stride);
        }
    }

    public void NextScan(ScanType type, double value, double tol, IProgress<double> progress, CancellationToken ct)
        => NextScan(type, value, tol, progress, ct, null, false);

    /// <param name="barRatio">Bar fill before/after and tolerance, enabling the ratio filter.</param>
    /// <param name="revertIfEmpty">Undo the scan if nothing matches (protects against a bad bar reading).</param>
    public void NextScan(ScanType type, double value, double tol, IProgress<double> progress, CancellationToken ct,
                         (double PrevFrac, double CurFrac, double Tol)? barRatio, bool revertIfEmpty)
    {
        if (!HasScan) throw new InvalidOperationException("Run a first scan before a next scan.");
        if (type == ScanType.UnknownInitialValue) throw new InvalidOperationException("Unknown initial value is a first-scan type.");
        LastScanReverted = false;
        long before = ResultCount;
        bool wasSnapshot = InSnapshotMode;
        _bytesRead = _bytesBad = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PushUndo();
        _ratioOn = barRatio.HasValue && barRatio.Value.PrevFrac > 0 && barRatio.Value.CurFrac > 0;
        if (_ratioOn) { _rPrev = barRatio.Value.PrevFrac; _rCur = barRatio.Value.CurFrac; _rTol = barRatio.Value.Tol; }
        bool ratio = _ratioOn;
        try
        {
            if (InSnapshotMode) NextFromSnapshots(type, value, tol, progress, ct, revertIfEmpty);
            else
            {
                var st = Capture();
                NextFromAddresses(type, value, tol, progress, ct);
                if (revertIfEmpty && _addrs.Length == 0 && st.Addrs.Length > 0)
                {
                    _addrs = st.Addrs; _kinds = st.Kinds; _prev = st.Prev; _first = st.First;
                    LastScanReverted = true;
                }
            }
        }
        catch (OperationCanceledException) { DropLastUndo(); throw; } // state is untouched until a scan completes
        finally { _ratioOn = false; }
        if (LastScanReverted) DropLastUndo();
        else ScanCount++;

        string what = type.Label() + (ratio ? $" + bar ratio {barRatio.Value.PrevFrac * 100:0.#}%→{barRatio.Value.CurFrac * 100:0.#}% (±{barRatio.Value.Tol * 100:0.#}%)" : "");
        LastSummary = $"Next scan {what}: {(wasSnapshot ? "snapshot" : before.ToString("N0"))} → " +
                      (LastScanReverted ? "0 (nothing matched, scan undone)" : $"{_addrs.Length:N0}" + (MultiKind ? $" ({CountByKindText()})" : "")) +
                      $", {Diag.Mb(_bytesBad)} unreadable, {sw.Elapsed.TotalSeconds:0.0} s";
        Diag.Log(LastSummary);
    }

    private void NextFromSnapshots(ScanType type, double value, double tol, IProgress<double> progress, CancellationToken ct, bool revertIfEmpty)
    {
        var kinds = _kindSet;
        int step = _step;
        var results = new ConcurrentBag<Hits>();
        long total = _snaps.Sum(s => s.Size), done = 0;

        Parallel.ForEach(_snaps, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
            () => (cur: new byte[ChunkSize + 8], old: new byte[ChunkSize + 8], ok: new bool[OkLen]),
            (s, _, bufs) =>
            {
                using var fs = new FileStream(s.File, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                long end = s.Base + s.Size;
                for (long a = s.Base; a < end; a += ChunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int len = (int)Math.Min(ChunkSize, end - a);
                    int want = (int)Math.Min(len + 8, end - a);
                    fs.Position = a - s.Base;
                    int oldGot = ReadFull(fs, bufs.old, want);
                    int bad = _mem.ReadPaged(a, bufs.cur, want, bufs.ok);
                    if (bad > 0) Interlocked.Add(ref _bytesBad, Math.Min(bad, len));
                    Hits h = null;
                    for (int off = 0; off < len; off += step)
                    {
                        long addr = a + off;
                        foreach (var k in kinds)
                        {
                            int size = k.Size();
                            if (off + size > want || off + size > oldGot || !AlignOk(k, addr) || !ProcessMemory.PagesOk(bufs.ok, a, off, size)) continue;
                            var cs = bufs.cur.AsSpan(off, size);
                            var ps = bufs.old.AsSpan(off, size);
                            double prev = k.Decode(ps);
                            // In snapshot mode the snapshot is still the first scan, so prev doubles as "first".
                            if (Match(k, type, k.Decode(cs), prev, value, tol, prev))
                            {
                                h ??= new Hits { Start = a };
                                h.Add(addr, k, cs, ps, true);
                            }
                        }
                    }
                    if (h != null) results.Add(h);
                    long d = Interlocked.Add(ref done, len);
                    progress?.Report((double)d / Math.Max(1, total));
                }
                return bufs;
            }, _ => { });

        int count = results.Sum(r => r.A.Count);
        if (count == 0 && revertIfEmpty) { LastScanReverted = true; return; } // keep the snapshot
        Collect(results, true);
        DeleteSnapshots(); // the files stay on disk while an undo step still needs them
    }

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private void NextFromAddresses(ScanType type, double value, double tol, IProgress<double> progress, CancellationToken ct)
    {
        int n = _addrs.Length;
        if (n == 0) return;

        // Split into ordered batches so results stay sorted.
        int batches = Math.Min(256, Math.Max(1, n / 4096));
        var keep = new Hits[batches];
        bool haveFirst = _first.Length == _addrs.Length * Stride;
        int done = 0;

        Parallel.For(0, batches, new ParallelOptions { CancellationToken = ct }, b =>
        {
            int from = (int)((long)n * b / batches), to = (int)((long)n * (b + 1) / batches);
            var h = new Hits { Start = b };
            var buf = new byte[BlockWindow + Stride];
            var ok = new bool[(BlockWindow + Stride) / ProcessMemory.PageSize + 4];
            int i = from;
            while (i < to)
            {
                long start = _addrs[i];
                int j = i;
                while (j < to && _addrs[j] + Stride - start <= BlockWindow) j++;
                int len = (int)(_addrs[j - 1] - start) + KindAt(j - 1).Size();
                // Pages in between results can be unreadable; ReadPaged keeps everything that can be read.
                int bad = _mem.ReadPaged(start, buf, len, ok);
                if (bad > 0) Interlocked.Add(ref _bytesBad, bad);
                for (int k = i; k < j; k++)
                {
                    var kind = KindAt(k);
                    int size = kind.Size();
                    int off = (int)(_addrs[k] - start);
                    if (off + size > len || !ProcessMemory.PagesOk(ok, start, off, size)) continue; // page vanished
                    var cs = buf.AsSpan(off, size);
                    double cur = kind.Decode(cs);
                    double prev = kind.Decode(_prev.AsSpan(k * Stride, size));
                    double first = haveFirst ? kind.Decode(_first.AsSpan(k * Stride, size)) : double.NaN;
                    if (Match(kind, type, cur, prev, value, tol, first))
                        h.Add(_addrs[k], kind, cs, haveFirst ? _first.AsSpan(k * Stride, Stride) : default, haveFirst);
                }
                i = j;
            }
            keep[b] = h;
            int d = Interlocked.Increment(ref done);
            progress?.Report((double)d / batches);
        });

        Collect(keep, haveFirst);
    }

    public void Dispose() => Reset();
}
