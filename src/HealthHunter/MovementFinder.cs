using System.Diagnostics;

namespace HealthHunter;

/// <summary>What the player is doing with the controls right now (keyboard or controller).</summary>
public struct MoveInput
{
    /// <summary>A movement key or the left stick is held.</summary>
    public bool Moving;
    /// <summary>The jump key / button is held.</summary>
    public bool Jump;
}

/// <summary>
/// v6: finds the player's position in memory from nothing but the controls. Position is three floats next to each
/// other that change while the player walks, stand still while the player stands still, and one of which goes up and
/// comes back down on a jump. So: snapshot while standing still, "Changed" scan while walking, "Unchanged" while
/// standing again (three rounds), then one jump sorts the vertical axis out, and a write-once test picks the real
/// position over render or camera copies: the real one, pushed up a little, falls back down on its own.
/// Result: a "Position" group (X, Y, Height) in the cheat table. Freezing Height is "hold height / no fall"; the
/// group's save/restore slots are teleport. Honest limit: games that keep the position in a physics engine or in
/// double precision, or that reset it from elsewhere every frame, are not found (the finder says so).
/// Nothing here reads the keyboard for anything but "is a movement key down" (GetAsyncKeyState, the same read-only call
/// games use), and nothing is sent to the game.
/// </summary>
public sealed class MovementFinder
{
    public Action<string> Feed;
    public Func<List<TableEntry>> Entries;
    public Action<TableEntry, AssistantItem> Found;
    public Action<AssistantItem> Items;
    public Func<MoveInput> Input = ReadInput;
    public string StatusLine { get; private set; } = "waiting";
    public Task Completion { get; private set; } = Task.CompletedTask;

    private readonly ProcessMemory _mem;
    private readonly AssistantOptions _opt;
    private CancellationTokenSource _cts;
    private AssistantItem _item;
    private static readonly ValueKind[] Kinds = { ValueKind.Float };
    private const int Rounds = 3;            // walk / stand rounds before the jump
    private const int MaxRounds = 8;
    private const long SmallEnough = 60_000; // candidates after the rounds; more means position isn't a plain float here
    private const int JumpTries = 3;
    private const int JumpSampleMs = 2600;   // how long a jump is watched (long jumps in slow games included)

    // Keys that move the player in most games: WASD, arrows, and the usual jump keys.
    private static readonly int[] MoveKeys = { 'W', 'A', 'S', 'D', 0x25, 0x26, 0x27, 0x28 };
    private const int VkSpace = 0x20;

    public MovementFinder(ProcessMemory mem, AssistantOptions opt) { _mem = mem; _opt = opt; }

    public static MoveInput ReadInput() => new()
    {
        Moving = GameWindow.AnyKeyDown(MoveKeys) || Gamepad.LeftStick() > 0.5,
        Jump = GameWindow.AnyKeyDown(VkSpace) || (Gamepad.Buttons() & 0x1000) != 0
    };

    public void Start(CancellationToken outer)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ct = _cts.Token;
        _item = new AssistantItem { Name = "Position", Source = "walking and jumping", State = "finding", Detail = "stand still for a moment, then walk" };
        try { Items?.Invoke(_item); } catch { }
        Say("Position: finding where the game keeps your position. Stand still a moment, walk a bit, stand still, repeat; then jump once in place.");
        Completion = Task.Run(() =>
        {
            try { Run(ct); }
            catch (OperationCanceledException) { _item.State = "stopped"; }
            catch (Exception ex) { Diag.Log("Position: " + ex); _item.State = "error"; _item.Detail = ex.Message; }
        }, ct);
    }

    public void Stop() => _cts?.Cancel();

    public void MarkDone(string why)
    {
        _item = new AssistantItem { Name = "Position", Source = "saved profile", State = "found", Detail = why };
        try { Items?.Invoke(_item); } catch { }
        StatusLine = why;
    }

    private void Say(string s) { Diag.Log("Position: " + s); try { Feed?.Invoke(s); } catch { } }
    private void Status(string s) { StatusLine = s; if (_item != null) _item.Detail = s; }

    private bool Still(CancellationToken ct, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            ct.ThrowIfCancellationRequested();
            var i = Input();
            if (i.Moving || i.Jump) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    private void WaitFor(CancellationToken ct, Func<MoveInput, bool> what, string status)
    {
        Status(status);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (what(Input())) return;
            Thread.Sleep(40);
        }
    }

    private void Run(CancellationToken ct)
    {
        // The position at rest may settle by a hair (physics), so "unchanged" allows a tiny relative drift.
        using var s = new Scanner(_mem) { Aligned = true, KeepUndo = false, PlausibleHealthFilter = false, UnchangedRelTol = 1e-5, FloatTolerance = 0 };
        // 1) Snapshot while standing still (paused so nothing moves half-way through).
        do WaitFor(ct, i => !i.Moving && !i.Jump, "stand still for a moment (no movement keys)…");
        while (!Still(ct, 1200));
        Status("taking a snapshot of the game while you stand still (paused for a moment)…");
        bool paused = _mem.Suspend();
        try { s.FirstScan(Kinds, ScanType.UnknownInitialValue, 0, 0, null, ct); }
        finally { if (paused) _mem.Resume(); }

        // 2) Walk / stand rounds. Every scan compares two resting moments: "Changed" is taken once the player has stopped
        //    again (a scan taken mid-walk would store a position the next "Unchanged" scan then finds changed), and
        //    "Unchanged" after another moment of standing still throws out everything that moves on its own.
        int rounds = 0;
        while (rounds < MaxRounds)
        {
            WaitFor(ct, i => i.Moving, rounds == 0 ? "now walk around for a second or two…" : $"{s.ResultCount:N0} candidates. Walk again…");
            var held = Stopwatch.StartNew();
            while (held.ElapsedMilliseconds < 500) { ct.ThrowIfCancellationRequested(); if (!Input().Moving) break; Thread.Sleep(30); }
            if (held.ElapsedMilliseconds < 400) continue; // a tap, not a walk
            WaitFor(ct, i => !i.Moving, "keep going, then stop…");
            if (!Still(ct, 1000)) continue;               // the character has to come to rest first (games ease out of a walk)
            Status("scanning what moved…");
            s.NextScan(ScanType.Changed, 0, 0, null, ct, null, revertIfEmpty: true);
            Status($"{s.ResultCount:N0} candidates. Keep standing still…");
            if (!Still(ct, 1500)) continue;               // moved again: the next "Changed" scan re-bases on the new rest
            Status("scanning what stayed put…");
            s.NextScan(ScanType.Unchanged, 0, 1e-4, null, ct, null, revertIfEmpty: true);
            rounds++;
            Say($"Position: round {rounds}: {s.ResultCount:N0} values move when you walk and rest when you stand.");
            if (rounds >= Rounds && s.ResultCount <= SmallEnough) break;
            if (s.ResultCount == 0) break;
        }
        if (s.ResultCount == 0 || s.ResultCount > SmallEnough)
        {
            GiveUp(s.ResultCount == 0 ? "nothing in memory moved only while you walked (the position may be in double precision or not a plain float here)."
                                      : $"{s.ResultCount:N0} values still move with you after {rounds} rounds; the position isn't a plain float the way this finder expects.");
            return;
        }
        var cands = Enumerable.Range(0, (int)s.ResultCount).Select(i => s.AddressAt(i)).ToArray();
        var set = new HashSet<long>(cands);

        // 3) A jump in place: the vertical axis goes up and comes back down. On flat ground the height doesn't change
        //    while walking, so it's looked for next to pairs of walking values (X and Y sit beside Z), and among the
        //    walking values themselves when the ground was uneven.
        var zCands = new HashSet<long>();
        foreach (var a in cands)
            if (set.Contains(a + 4)) { zCands.Add(a - 4); zCands.Add(a + 8); zCands.Add(a); zCands.Add(a + 4); }
        if (cands.Length <= 4000) foreach (var a in cands) zCands.Add(a);
        var zArr = zCands.Where(a => a > 0x10000).OrderBy(a => a).ToArray();
        int pairs = cands.Count(a => set.Contains(a + 4));
        Diag.Log($"Position: {cands.Length} walking value(s), {pairs} adjacent pair(s), {zArr.Length} height candidate(s): " +
                 string.Join(" ", cands.Take(24).Select(a => a.ToString("X"))) + (cands.Length > 24 ? " …" : ""));
        if (zArr.Length == 0) { GiveUp("no two walking values sit next to each other in memory, so there's no X/Y/Z block to look at."); return; }
        var jumpers = new List<(long Addr, double Ground, double Peak)>();
        for (int t = 0; t < JumpTries && jumpers.Count == 0; t++)
        {
            WaitFor(ct, i => !i.Moving && !i.Jump, $"{cands.Length:N0} walking values, {zArr.Length:N0} possible heights. Stand still, then jump once in place…");
            if (!Still(ct, 800)) { t--; continue; }
            var v0 = ReadAll(zArr);
            WaitFor(ct, i => i.Jump || i.Moving, "jump once in place (no walking)…");
            if (Input().Moving) { t--; continue; }
            // Sample everything for a while: a jump is a value that leaves its resting level, peaks, and comes back to rest
            // for good. Jumps differ in height and length from game to game, so the shape is read off the samples rather
            // than off fixed moments.
            var series = new List<double[]>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < JumpSampleMs) { ct.ThrowIfCancellationRequested(); Thread.Sleep(30); series.Add(ReadAll(zArr)); }
            for (int i = 0; i < zArr.Length; i++)
            {
                double peak = 0; int kPeak = -1;
                for (int k = 0; k < series.Count; k++)
                {
                    double d = series[k][i] - v0[i];
                    if (double.IsNaN(d)) { kPeak = -1; break; }
                    if (Math.Abs(d) > Math.Abs(peak)) { peak = d; kPeak = k; }
                }
                // A jump moves a position by a real amount (metres or centimetres); float noise in the last digits doesn't count.
                if (kPeak < 1 || Math.Abs(peak) < Math.Max(0.01, 1e-4 * Math.Abs(v0[i]))) continue;
                double tol = 0.05 * Math.Abs(peak) + 1e-3;
                // Up to the peak it only moves one way (no wobble), and the peak isn't the very last sample.
                bool oneWay = true;
                for (int k = 0; k < kPeak; k++) { double d = series[k][i] - v0[i]; if (Math.Abs(d) > tol && Math.Sign(d) != Math.Sign(peak)) { oneWay = false; break; } }
                if (!oneWay || kPeak >= series.Count - 10) continue;
                // Back at rest afterwards and staying there for the last 300 ms of the window at least.
                int kBack = -1;
                for (int k = kPeak + 1; k < series.Count; k++) if (Math.Abs(series[k][i] - v0[i]) <= tol) { kBack = k; break; }
                if (kBack < 0 || series.Count - kBack < 10) continue;
                bool stayed = true;
                for (int k = kBack; k < series.Count; k++) if (Math.Abs(series[k][i] - v0[i]) > tol) { stayed = false; break; }
                if (!stayed) continue;
                jumpers.Add((zArr[i], v0[i], peak));
            }
            Say($"Position: jump {t + 1}: {jumpers.Count} value(s) went up and came back down" +
                (jumpers.Count > 0 ? $" ({string.Join(", ", jumpers.Take(6).Select(j => $"{j.Addr:X} by {j.Peak:0.###}"))})." : "."));
            if (jumpers.Count == 0 && t + 1 < JumpTries) Say("Position: no value behaved like a jump; try once more (jump in place without walking).");
        }
        if (jumpers.Count == 0) { GiveUp("nothing went up and came back down on a jump (this game may not let a jump show in a plain float position)."); return; }

        // 4) Prefer a vertical that sits inside a triplet of walking values (X, Y, Z next to each other).
        var jumpSet = new HashSet<long>(jumpers.Select(j => j.Addr));
        var rest = ReadAll(cands);
        var restOf = cands.Zip(rest).ToDictionary(p => p.First, p => p.Second);
        foreach (var j in jumpers) restOf[j.Addr] = j.Ground;
        var triplets = new List<(long Base, int VertIndex, (long Addr, double Ground, double Peak) J)>();
        foreach (var j in jumpers)
        {
            for (int pos = 0; pos < 3; pos++)
            {
                long b = j.Addr - 4 * pos;
                long a0 = b, a1 = b + 4, a2 = b + 8;
                var others = new[] { a0, a1, a2 }.Where(a => a != j.Addr).ToArray();
                if (!others.All(a => set.Contains(a) && !jumpSet.Contains(a))) continue;
                if (others.All(a => Math.Abs(restOf[a]) < 1e-6) && Math.Abs(j.Ground) < 1e-6) continue; // a velocity: all zero at rest
                triplets.Add((b, pos, j));
            }
        }
        var toTest = triplets.Count > 0 ? triplets.Select(t => t.J).Distinct().ToList() : jumpers.Take(6).ToList();
        Say($"Position: {triplets.Count} X/Y/Z triplet(s) found around the jump values; testing {toTest.Count} vertical candidate(s).");

        // 5) Write-once test: push the vertical up a little; the real position falls back on its own, a copy is overwritten at once.
        WaitFor(ct, i => !i.Moving && !i.Jump, "testing the candidates; stand still…");
        var real = new List<(long Addr, double Ground, double Peak)>();
        var stuck = new List<(long Addr, double Ground, double Peak)>();
        foreach (var j in toTest)
        {
            ct.ThrowIfCancellationRequested();
            if (!Still(ct, 400)) { WaitFor(ct, i => !i.Moving && !i.Jump, "stand still for the test…"); }
            double push = 0.6 * j.Peak;
            bool paused2 = _mem.Suspend();
            double cur; bool ok;
            try { ok = _mem.TryReadValue(j.Addr, ValueKind.Float, out cur) && _mem.WriteValue(j.Addr, ValueKind.Float, cur + push); }
            finally { if (paused2) _mem.Resume(); }
            if (!ok) continue;
            var pts = new List<double>();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1800)
            {
                Thread.Sleep(60);
                _mem.TryReadValue(j.Addr, ValueKind.Float, out double v);
                pts.Add(v);
            }
            double target = cur + push;
            bool revertedAtOnce = pts.Take(2).All(v => Math.Abs(v - cur) < 0.02 * Math.Abs(push));
            bool keptAtFirst = Math.Abs(pts[0] - target) < 0.25 * Math.Abs(push);
            bool backAtEnd = Math.Abs(pts[^1] - cur) < 0.1 * Math.Abs(push) + 1e-3;
            bool stayed = Math.Abs(pts[^1] - target) < 0.1 * Math.Abs(push);
            string verdict;
            if (revertedAtOnce) verdict = "reverted at once (a copy)";
            else if (keptAtFirst && backAtEnd) { verdict = "kept it, then fell back down (the real position)"; real.Add(j); }
            else if (stayed) { verdict = "kept it and stayed there (position with no gravity on it, or a copy nothing refreshes)"; stuck.Add(j); }
            else verdict = "moved in some other way";
            Diag.Log($"Position: wrote {target:0.###} once to {j.Addr:X}: {verdict}. Readings: {string.Join(" ", pts.Take(12).Select(v => v.ToString("0.##")))}…");
            // Put it back if the game didn't.
            if (!backAtEnd)
            {
                _mem.TryReadValue(j.Addr, ValueKind.Float, out double now);
                if (Math.Abs(now - target) < 0.5 * Math.Abs(push)) _mem.WriteValue(j.Addr, ValueKind.Float, cur);
            }
        }
        var pick = real.Count > 0 ? real : stuck;
        if (pick.Count == 0) { GiveUp("the candidates were all overwritten by the game when changed (the position is owned by something else, like a physics engine)."); return; }
        // Among several, prefer one inside a triplet, then the lowest address.
        var chosen = pick.OrderByDescending(j => triplets.Any(t => t.J.Addr == j.Addr)).ThenBy(j => j.Addr).First();
        var trip = triplets.FirstOrDefault(t => t.J.Addr == chosen.Addr);
        Accept(chosen, trip.Base != 0 ? trip : null, real.Count > 0, ct);
    }

    private double[] ReadAll(long[] addrs)
    {
        var v = new double[addrs.Length];
        for (int i = 0; i < addrs.Length; i++) v[i] = _mem.TryReadValue(addrs[i], ValueKind.Float, out double x) ? x : double.NaN;
        return v;
    }

    private void Accept((long Addr, double Ground, double Peak) vert, (long Base, int VertIndex, (long Addr, double Ground, double Peak) J)? trip, bool falls, CancellationToken ct)
    {
        var entries = new List<TableEntry>();
        var height = new TableEntry { Name = "Height", Group = "Position", Kind = ValueKind.Float, RawAddress = vert.Addr, Mode = FreezeMode.FreezeCurrent, MaxValue = vert.Ground, FreezeValue = vert.Ground };
        Status("finding a pointer path for the position…");
        var prog = new Progress2(m => Status(m));
        var (kept, fromEarlier) = AutoFinder.AttachPointerPath(_mem, height, "Height", _opt.PointerDepth, _opt.PointerMaxOffset, prog, ct);
        entries.Add(height);
        if (trip != null)
        {
            string[] names = { "Position A", "Position B" };
            int n = 0;
            for (int i = 0; i < 3; i++)
            {
                if (i == trip.Value.VertIndex) continue;
                long a = trip.Value.Base + 4 * i;
                _mem.TryReadValue(a, ValueKind.Float, out double v);
                var e = new TableEntry { Name = names[n++], Group = "Position", Kind = ValueKind.Float, RawAddress = a, Mode = FreezeMode.FreezeCurrent, MaxValue = v, FreezeValue = v };
                if (height.Chain != null)
                {
                    // Same object, a few bytes along: the same path with the last offset moved.
                    var c = new PointerChain { Module = height.Chain.Module, ModuleOffset = height.Chain.ModuleOffset, Offsets = height.Chain.Offsets.ToList() };
                    if (c.Offsets.Count > 0) c.Offsets[^1] += a - vert.Addr; else c.ModuleOffset += a - vert.Addr;
                    if (c.Resolve(_mem) == a) e.Chain = c;
                }
                entries.Add(e);
            }
        }
        _item.State = "found"; _item.Entry = height;
        _item.Detail = (trip != null ? "X, Y and height found" : "height found (the other two axes weren't next to it)") +
                       (falls ? "" : "; this game doesn't pull it down, so Hold height may not be needed") +
                       (fromEarlier ? $"; {kept} saved pointer path(s) still work" : kept == 0 ? "; no pointer path (works until restart)" : $"; {kept} candidate pointer path(s)");
        Say($"Position found: height at {height.Describe()}{(trip != null ? " with X and Y next to it" : "")}. " +
            "\"Hold height\" (freeze Height) stops falling; the Position group's save/restore slots teleport.");
        foreach (var e in entries)
        {
            try { Found?.Invoke(e, _item); } catch (Exception ex) { Diag.Log("Position: Found callback: " + ex.Message); }
        }
        StatusLine = "found";
    }

    private void GiveUp(string why)
    {
        _item.State = "gave up"; _item.Detail = why;
        StatusLine = "gave up";
        Say("Position: gave up: " + why + " Position cheats need a per-game approach here; this isn't something the assistant can fake.");
    }

    private sealed class Progress2 : IProgress<string>
    {
        private readonly Action<string> _a;
        public Progress2(Action<string> a) { _a = a; }
        public void Report(string value) => _a(value);
    }
}
