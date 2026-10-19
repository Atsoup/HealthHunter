using System.Collections.Concurrent;

namespace HealthHunter;

/// <summary>
/// A path from a static address inside a module (survives restarts) to the value.
/// Resolve: a = module.Base + ModuleOffset; for each o in Offsets: a = *(a) + o.
/// With no offsets the address is simply static.
/// </summary>
public sealed class PointerChain
{
    public string Module { get; set; }
    public long ModuleOffset { get; set; }
    public List<long> Offsets { get; set; } = new();

    public long? Resolve(ProcessMemory mem)
    {
        var mod = mem.FindModule(Module);
        if (mod == null) return null;
        long a = mod.Base + ModuleOffset;
        foreach (var o in Offsets)
        {
            if (!mem.ReadPointer(a, out long p) || p == 0) return null;
            a = p + o;
        }
        return a;
    }

    public override string ToString()
    {
        var s = $"[\"{Module}\"+{ModuleOffset:X}]";
        foreach (var o in Offsets) s += o < 0 ? $" → -{-o:X}" : $" → +{o:X}";
        return s;
    }
}

/// <summary>
/// Reverse pointer search: finds every pointer that points near the target, then pointers that point
/// near those, up to MaxDepth levels, stopping when a pointer lives inside a game module.
/// </summary>
public sealed class PointerScanner
{
    public int MaxDepth { get; set; } = 4;
    public long MaxOffset { get; set; } = 0x1000;
    public int MaxResults { get; set; } = 3000;
    public int MaxNodesPerLevel { get; set; } = 150_000;
    /// <summary>Only accept paths whose base lives in the game's main exe (most stable across updates).</summary>
    public bool MainModuleOnly { get; set; }
    /// <summary>When set, the final offset (pointer → value) must equal this. Use a known struct offset to cut noise.</summary>
    public long? LastOffset { get; set; }

    private long[] _values;  // pointer values, sorted
    private long[] _sources; // where each pointer is stored

    public List<PointerChain> Scan(ProcessMemory mem, long target, IProgress<string> status, CancellationToken ct)
    {
        mem.RefreshModules();
        BuildPointerMap(mem, status, ct);
        status?.Report($"Pointer map: {_values.Length:N0} pointers. Searching paths…");

        var results = new List<PointerChain>();
        var exeMod = mem.Modules.FirstOrDefault();
        bool BaseOk(ModuleInfo m) => m != null && (!MainModuleOnly || m == exeMod);
        var home = mem.ModuleAt(target);
        if (BaseOk(home) && LastOffset == null) // Value lives in a module's static data: no pointers needed.
            results.Add(new PointerChain { Module = home.Name, ModuleOffset = target - home.Base });
        // Each node: address reached, and the offsets from that address down to the target.
        var level = new List<(long Addr, long[] Offsets)> { (target, Array.Empty<long>()) };
        var visited = new HashSet<long> { target };

        for (int depth = 1; depth <= MaxDepth && level.Count > 0 && results.Count < MaxResults; depth++)
        {
            ct.ThrowIfCancellationRequested();
            var next = new ConcurrentBag<(long, long[])>();
            var found = new ConcurrentBag<PointerChain>();
            int lvl = depth;

            Parallel.ForEach(level, new ParallelOptions { CancellationToken = ct }, node =>
            {
                // pointers p where node.Addr - MaxOffset <= *p <= node.Addr
                int lo = LowerBound(_values, node.Addr - MaxOffset);
                for (int i = lo; i < _values.Length && _values[i] <= node.Addr; i++)
                {
                    long off = node.Addr - _values[i];
                    if (node.Offsets.Length == 0 && LastOffset is long want && off != want) continue;
                    long src = _sources[i];
                    var offs = new long[node.Offsets.Length + 1];
                    offs[0] = off;
                    node.Offsets.CopyTo(offs, 1);
                    var mod = mem.ModuleAt(src);
                    if (BaseOk(mod))
                        found.Add(new PointerChain { Module = mod.Name, ModuleOffset = src - mod.Base, Offsets = offs.ToList() });
                    else if (lvl < MaxDepth)
                        next.Add((src, offs));
                }
            });

            // Prefer shorter offsets / modules that are the game itself.
            foreach (var c in found.OrderBy(c => c.Offsets.Sum()).Take(MaxResults - results.Count))
                results.Add(c);

            level = new List<(long, long[])>();
            foreach (var n in next.OrderBy(n => n.Item2[0]))
            {
                if (level.Count >= MaxNodesPerLevel) break;
                lock (visited) if (!visited.Add(n.Item1)) continue;
                level.Add(n);
            }
            status?.Report($"Depth {depth}: {results.Count:N0} chains found, {level.Count:N0} paths to follow");
        }

        _values = null; _sources = null;
        // Rank: exe module first, then fewer levels, then smaller offsets.
        string exe = mem.Modules.FirstOrDefault()?.Name;
        return results
            .OrderBy(c => string.Equals(c.Module, exe, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.Offsets.Count)
            .ThenBy(c => c.Offsets.Sum())
            .ToList();
    }

    /// <summary>Keeps only chains that still resolve to the given address (use after a game restart).</summary>
    public static List<PointerChain> Validate(ProcessMemory mem, IEnumerable<PointerChain> chains, long newTarget)
    {
        mem.RefreshModules();
        return chains.Where(c => c.Resolve(mem) == newTarget).ToList();
    }

    private void BuildPointerMap(ProcessMemory mem, IProgress<string> status, CancellationToken ct)
    {
        var regions = mem.GetRegions(writableOnly: true, includeMapped: false);
        var sorted = mem.GetRegions(writableOnly: false, includeMapped: true);
        long[] rb = sorted.Select(r => r.Base).ToArray();
        long[] re = sorted.Select(r => r.End).ToArray();
        int ps = mem.PointerSize;

        var parts = new ConcurrentBag<(List<long> V, List<long> S)>();
        long total = regions.Sum(r => r.Size), done = 0;
        const int chunk = 1 << 20;

        Parallel.ForEach(regions, new ParallelOptions { CancellationToken = ct }, () => new byte[chunk], (r, _, buf) =>
        {
            var v = new List<long>(); var s = new List<long>();
            for (long a = r.Base; a < r.End; a += chunk)
            {
                int len = (int)Math.Min(chunk, r.End - a);
                int got = mem.Read(a, buf, 0, len);
                for (int off = 0; off + ps <= got; off += ps)
                {
                    long p = ps == 8 ? BitConverter.ToInt64(buf, off) : BitConverter.ToUInt32(buf, off);
                    if (p < 0x10000 || (p & 3) != 0) continue;
                    // Must point into readable memory.
                    int idx = UpperBound(rb, p) - 1;
                    if (idx < 0 || p >= re[idx]) continue;
                    v.Add(p); s.Add(a + off);
                }
                long d = Interlocked.Add(ref done, len);
                if ((a / chunk & 31) == 0)
                    status?.Report($"Building pointer map… {100.0 * d / Math.Max(1, total):0}%");
            }
            parts.Add((v, s));
            return buf;
        }, _ => { });

        int n = parts.Sum(p => p.V.Count);
        _values = new long[n]; _sources = new long[n];
        int i = 0;
        foreach (var p in parts)
        {
            p.V.CopyTo(_values, i); p.S.CopyTo(_sources, i);
            i += p.V.Count;
        }
        Array.Sort(_values, _sources);
    }

    private static int LowerBound(long[] a, long key)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] < key) lo = m + 1; else hi = m; }
        return lo;
    }

    private static int UpperBound(long[] a, long key)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] <= key) lo = m + 1; else hi = m; }
        return lo;
    }
}
