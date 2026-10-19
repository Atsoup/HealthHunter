using System.Runtime.InteropServices;

namespace HealthHunter;

/// <summary>
/// Holds frozen values from a dedicated background thread at about 1,000 checks per second
/// (a UI timer manages ~40). Addresses are resolved by the UI thread and handed over, so the
/// hot loop only does one read and, when needed, one write per entry.
/// </summary>
public sealed class FreezeEngine : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private sealed record Item(TableEntry Entry, long Address, long? MaxAddress);

    private readonly ProcessMemory _mem;
    private volatile Item[] _items = Array.Empty<Item>();
    private volatile bool _stop;
    private readonly Thread _thread;
    public long Corrections;

    public FreezeEngine(ProcessMemory mem)
    {
        _mem = mem;
        timeBeginPeriod(1); // lets Sleep(1) really sleep ~1 ms
        _thread = new Thread(Loop) { IsBackground = true, Name = "HealthHunter freeze", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Called from the UI thread a few times per second with freshly resolved addresses.</summary>
    public void Update(IEnumerable<TableEntry> entries)
    {
        var list = new List<Item>();
        foreach (var e in entries)
        {
            if (!e.Frozen) continue;
            var a = e.Resolve(_mem);
            if (a == null) continue;
            list.Add(new Item(e, a.Value, e.MaxOffset is long off ? a.Value + off : null));
        }
        _items = list.ToArray();
    }

    private void Loop()
    {
        while (!_stop)
        {
            var items = _items;
            if (items.Length == 0) { Thread.Sleep(20); continue; }
            foreach (var it in items)
            {
                var e = it.Entry;
                if (!e.Frozen || !_mem.TryReadValue(it.Address, e.Kind, out double cur)) continue;
                double max = e.MaxValue;
                if (it.MaxAddress is long ma && _mem.TryReadValue(ma, e.Kind, out double m) && m > 0 && m < 1e9) max = m;
                var want = e.Desired(cur, max);
                if (want.HasValue && _mem.WriteValue(it.Address, e.Kind, want.Value)) Interlocked.Increment(ref Corrections);
            }
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1000);
        timeEndPeriod(1);
    }
}
