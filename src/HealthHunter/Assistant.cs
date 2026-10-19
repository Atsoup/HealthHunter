using System.Diagnostics;
using System.Drawing;

namespace HealthHunter;

/// <summary>What the assistant may do; mirrored by the check boxes on the Assistant page.</summary>
public sealed class AssistantOptions
{
    /// <summary>Attach to whatever game window is in front (after the anti-cheat check), instead of waiting for Attach.</summary>
    public bool AttachFront = true;
    /// <summary>Turn a cheat on as soon as its value is found (infinite health…), without waiting for a switch to be flipped.</summary>
    public bool Apply = true;
    /// <summary>Also track numbers drawn on the HUD (ammo, gold, XP, stats) with Windows OCR and find them in memory.</summary>
    public bool Numbers = true;
    /// <summary>Also find the player's position from walking and jumping, for "hold height" and save/teleport.</summary>
    public bool Movement = true;
    /// <summary>Confirm a bar's value by writing it once and watching the game (the v5.1 copy test).</summary>
    public bool Verify = true;
    public int PointerDepth = 4;
    public long PointerMaxOffset = 0x1000;
}

/// <summary>One thing the assistant is working on or has finished, for the Assistant page's list.</summary>
public sealed class AssistantItem
{
    public string Name;          // "Health", "Mana", "Gold", "Position"
    public string Source;        // "red bar at the top-left", "number 'Gold 1,250' at the top-right"
    public string State;         // "finding", "found", "on", "gave up", "waiting"
    public string Detail;        // latest status line
    public TableEntry Entry;
    public bool Done => Entry != null;
}

/// <summary>
/// v6: the hands-off mode. Switch it on and it attaches to the game in front, watches the game's window, finds the HUD
/// bars by itself, takes one snapshot of the game's memory and runs the v5.1 auto-finder for every bar at once from it
/// (health, mana, stamina… each confirmed by the write-once copy test), then reads on-screen numbers with Windows OCR
/// (ammo, gold, XP, stats) and finds those too, and works out the player's position from walking and jumping. Every
/// value it finds goes into the cheat table with a pointer path, the profile is saved, and, if allowed, the cheat is
/// switched on straight away.
///
/// The assistant only ever uses the same plain reads and writes of the game's memory as the rest of the app, and
/// refuses games with anti-cheat like everything else (the attach goes through the main form's checks).
/// </summary>
public sealed class Assistant : IDisposable
{
    // ---- wiring to the form (all called on the assistant's own thread; the form marshals to the UI)
    public Func<int, bool> TryAttach;                  // pid → attached (false: refused, e.g. anti-cheat)
    public Func<ProcessMemory> Memory;                 // the attached game or null
    public Func<List<TableEntry>> Entries;             // a copy of the cheat table
    public Action<TableEntry, AssistantItem> Found;    // a value was found: add it to the table, save, maybe switch on
    public Action<List<TableEntry>> Options;           // values the finder couldn't tell apart (added unfrozen)
    public Action<string> Feed;                        // one line for the feed
    public Action<string> Status;                      // the single status line
    public Func<GameProfile> Profile;                  // the loaded profile (for saved bars)
    public Action<List<SavedBar>> SaveBars;            // persist the bars found on screen

    public AssistantOptions Opt { get; } = new();
    public HudDetector Hud { get; } = new();
    public bool Running => _thread != null && _thread.IsAlive && !_cts.IsCancellationRequested;
    public List<AssistantItem> Items { get { lock (_items) return _items.ToList(); } }
    /// <summary>What the assistant last looked at (for the Assistant page's preview).</summary>
    public Bitmap LastFrame { get { lock (_frameLock) return _lastFrame == null ? null : (Bitmap)_lastFrame.Clone(); } }

    private readonly List<AssistantItem> _items = new();
    private Thread _thread;
    private CancellationTokenSource _cts;
    private readonly object _frameLock = new();
    private Bitmap _lastFrame;
    private readonly Dictionary<int, DateTime> _refused = new();     // pids the attach refused, and when
    private int _frontPid; private DateTime _frontSince;
    private ProcessMemory _mem;
    private IntPtr _hwnd; private DateTime _hwndAt;
    private Rectangle _client;
    private readonly Stopwatch _sinceFrame = new();

    // ---- bar finders
    private sealed class BarJob
    {
        public DetectedBar Bar; public AssistantItem Item; public BarDetector Det; public AutoFinder Finder; public Task<AutoFindResult> Task;
        public CancellationTokenSource Cts; public int GaveUp; public DateTime RetryAt; public bool Done;
        public Rectangle Rel; // full rect, client pixels, when started
    }
    private readonly List<BarJob> _jobs = new();
    private Scanner _master; private DateTime _masterAt; private readonly Dictionary<int, double> _masterFracs = new();
    private const int MaxFindersAtOnce = 3;
    private const int SnapshotFreshMinutes = 12;

    private NumberFinder _numbers;
    private MovementFinder _movement;
    private bool _numbersUnavailableSaid;

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Loop(_cts.Token)) { IsBackground = true, Name = "HealthHunter assistant" };
        _thread.Start();
    }

    /// <summary>Stops everything; the finders put back what they wrote before the game handle closes (see MainForm).</summary>
    public void Stop()
    {
        _cts?.Cancel();
        foreach (var j in _jobs.ToList()) j.Cts?.Cancel();
        _numbers?.Stop();
        _movement?.Stop();
    }

    /// <summary>Stops the work on the current game (Detach) but keeps watching for the next one.</summary>
    public void CancelGame()
    {
        foreach (var j in _jobs.ToList()) j.Cts?.Cancel();
        _numbers?.Stop();
        _movement?.Stop();
    }

    /// <summary>Waits for the running finders to finish undoing their test writes (called before Detach / close).</summary>
    public void WaitForFinders(int ms)
    {
        var tasks = _jobs.Where(j => j.Task != null && !j.Task.IsCompleted).Select(j => (Task)j.Task).ToList();
        if (_numbers != null) tasks.Add(_numbers.Completion);
        if (_movement != null) tasks.Add(_movement.Completion);
        try { Task.WaitAll(tasks.ToArray(), ms); } catch { }
    }

    public void Dispose() { Stop(); _master?.Dispose(); lock (_frameLock) { _lastFrame?.Dispose(); _lastFrame = null; } }

    // ================================================================= main loop

    private void Loop(CancellationToken ct)
    {
        Diag.Log("Assistant: started.");
        Say("Assistant on. " + (Opt.AttachFront ? "Bring the game window to the front." : "Attach to a game."));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { Step(ct); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Diag.Log("Assistant: " + ex); Status?.Invoke("Assistant hiccup: " + ex.Message); }
                Thread.Sleep(250);
            }
        }
        finally
        {
            foreach (var j in _jobs) j.Cts?.Cancel();
            _numbers?.Stop(); _movement?.Stop();
            Diag.Log("Assistant: stopped.");
            Status?.Invoke("Assistant off.");
        }
    }

    private void Step(CancellationToken ct)
    {
        var mem = Memory?.Invoke();
        if (mem == null || !mem.IsAlive)
        {
            if (_mem != null) OnGameGone();
            if (Opt.AttachFront) WatchForeground();
            else Status?.Invoke("Assistant on. Attach to a game (or tick \"attach to the game in front\").");
            return;
        }
        if (!ReferenceEquals(mem, _mem)) OnNewGame(mem);

        // Where is the game on screen?
        if (_hwnd == IntPtr.Zero || (DateTime.Now - _hwndAt).TotalSeconds > 2)
        {
            _hwnd = GameWindow.MainWindowOf(mem.Pid);
            _hwndAt = DateTime.Now;
        }
        var client = GameWindow.ClientOnScreen(_hwnd);
        if (client.Width < 64 || client.Height < 64)
        {
            Status?.Invoke($"{mem.ProcessName}: the game window is minimized or hidden. Bring it up (Borderless or Windowed) to continue.");
            return;
        }
        _client = client;

        // One frame per step: find and track the bars.
        using (var bmp = BarDetector.Capture(client))
        {
            Hud.Process(bmp, DateTime.Now);
            lock (_frameLock) { _lastFrame?.Dispose(); _lastFrame = (Bitmap)bmp.Clone(); }
        }
        foreach (var j in _jobs.Where(j => j.Det?.Cal != null))
        {
            // The window may have moved; the finder reads the bar at the window's current place.
            j.Det.Cal.X = client.X + j.Rel.X; j.Det.Cal.Y = client.Y + j.Rel.Y;
        }

        CollectBars();   // results first, so a finished finder is never restarted before its result is read
        ScheduleBars(mem, ct);
        RunNumbers(mem, client, ct);
        RunMovement(mem, ct);
        Status?.Invoke(Summary(mem));
    }

    private void WatchForeground()
    {
        var (pid, hwnd) = GameWindow.Foreground();
        string name = null;
        if (pid != 0)
        {
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { pid = 0; }
        }
        if (pid == 0 || !GameWindow.LooksLikeGame(name)) { _frontPid = 0; Status?.Invoke("Assistant on. Waiting for a game: click into the game window."); return; }
        if (_refused.TryGetValue(pid, out var when) && (DateTime.Now - when).TotalMinutes < 5)
        {
            Status?.Invoke($"Not attaching to {name} (refused a moment ago; see the feed).");
            return;
        }
        var r = GameWindow.ClientOnScreen(hwnd);
        if (r.Width < 480 || r.Height < 270) { Status?.Invoke($"{name} is in front, but its window is small; waiting for the game's main window."); return; }
        if (pid != _frontPid) { _frontPid = pid; _frontSince = DateTime.Now; }
        double held = (DateTime.Now - _frontSince).TotalSeconds;
        if (held < 2) { Status?.Invoke($"{name} is in front… attaching in a moment."); return; }
        Say($"Attaching to {name}.");
        bool ok = false;
        try { ok = TryAttach?.Invoke(pid) ?? false; } catch (Exception ex) { Diag.Log("Assistant: attach failed: " + ex.Message); }
        if (!ok)
        {
            _refused[pid] = DateTime.Now;
            Say($"Couldn't attach to {name} (see the status bar; anti-cheat games are refused). Will not try again for 5 minutes.");
        }
    }

    private void OnNewGame(ProcessMemory mem)
    {
        _mem = mem;
        _hwnd = IntPtr.Zero;
        Hud.Clear();
        foreach (var j in _jobs) j.Cts?.Cancel();
        _jobs.Clear();
        _master?.Dispose(); _master = null; _masterFracs.Clear();
        _numbers?.Stop(); _numbers = null;
        _movement?.Stop(); _movement = null;
        lock (_items) _items.Clear();
        string console = AutoFinder.BigEndianConsole(mem);
        if (console != null) Say($"{mem.ProcessName} emulates the {console} (big-endian numbers), which HealthHunter can't read. The assistant will only use saved values here.");
        // Bars from an earlier session: known straight away, so a missing value is re-found without waiting for the bar to move.
        var prof = Profile?.Invoke();
        int seeded = 0;
        if (prof?.AutoBars != null)
            foreach (var sb in prof.AutoBars)
            {
                if (sb.Width <= 0 || sb.Height <= 0) continue;
                Hud.Seed(sb.Vertical, new Rectangle(sb.X, sb.Y, sb.Width, sb.Height), sb.Anchor, sb.FillArgb, sb.Tolerance, sb.Label);
                seeded++;
            }
        Say($"Watching {mem.ProcessName}." + (seeded > 0 ? $" {seeded} bar(s) remembered from last time." : " Looking for bars on the HUD; play normally."));
    }

    private void OnGameGone()
    {
        Say($"{_mem?.ProcessName ?? "The game"} is gone; the assistant is waiting for a game again.");
        foreach (var j in _jobs) j.Cts?.Cancel();
        _jobs.Clear();
        _master?.Dispose(); _master = null;
        _numbers?.Stop(); _numbers = null;
        _movement?.Stop(); _movement = null;
        _mem = null; _hwnd = IntPtr.Zero;
    }

    // ================================================================= bars

    private void ScheduleBars(ProcessMemory mem, CancellationToken ct)
    {
        if (AutoFinder.BigEndianConsole(mem) != null) return;
        var entries = Entries?.Invoke() ?? new();
        var bars = Hud.Bars;
        int running = _jobs.Count(j => j.Task != null && !j.Task.IsCompleted);
        foreach (var bar in bars.OrderByDescending(b => b.Changes))
        {
            if (!(bar.Live || bar.FromProfile)) continue;
            var job = _jobs.FirstOrDefault(j => j.Bar.Id == bar.Id);
            if (job == null)
            {
                string label = bar.Label.Length > 0 ? bar.Label : Label(bar, bars);
                bar.Label = label;
                job = new BarJob { Bar = bar, Item = new AssistantItem { Name = label, Source = bar.Describe(Hud.ClientSize), State = "waiting" } };
                _jobs.Add(job);
                lock (_items) _items.Add(job.Item);
                var existing = entries.FirstOrDefault(e => string.Equals(e.Name, label, StringComparison.OrdinalIgnoreCase));
                if (existing != null && existing.Resolve(mem) is long ad && mem.TryReadValue(ad, existing.Kind, out double v) && v > 0 &&
                    v <= existing.CurrentMax(mem, ad) * 1.5)
                {
                    job.Done = true; job.Item.Entry = existing; job.Item.State = existing.Frozen ? "on" : "found";
                    job.Item.Detail = "Already in the cheat table from an earlier session.";
                    Say($"{label}: the saved value still works ({existing.Describe()}); {(bar.FromProfile ? "bar remembered" : "bar seen on screen")}.");
                    continue;
                }
                Say($"Found a {bar.Describe(Hud.ClientSize)}; treating it as {label}.");
                if (bar.FromProfile && !(bar.Frames > 0)) { job.Item.Detail = "Waiting for this bar to show on screen."; continue; }
            }
            if (job.Done || job.Task != null) continue; // running, or finished and waiting for CollectBars
            if (job.GaveUp >= 2) continue;
            if (job.GaveUp > 0 && DateTime.Now < job.RetryAt) continue;
            if (bar.FromProfile && bar.Frames == 0) continue; // not seen yet this session
            if (running >= MaxFindersAtOnce) break;
            if (!StartFinder(mem, job, ct)) break;
            running++;
        }
    }

    private static readonly Dictionary<string, string[]> HueLabels = new()
    {
        ["red"] = new[] { "Health" }, ["pink"] = new[] { "Health" }, ["orange"] = new[] { "Health", "Stamina" },
        ["green"] = new[] { "Health", "Stamina" }, ["yellow"] = new[] { "Stamina", "Energy" }, ["white"] = new[] { "Stamina" },
        ["blue"] = new[] { "Mana" }, ["cyan"] = new[] { "Mana", "Stamina" }, ["purple"] = new[] { "Mana", "Energy" }
    };

    /// <summary>A name for a bar from its colour, not used by any other bar yet: red is health, blue mana, yellow stamina…</summary>
    private string Label(DetectedBar bar, List<DetectedBar> all)
    {
        var used = new HashSet<string>(all.Where(b => b.Id != bar.Id && b.Label.Length > 0).Select(b => b.Label), StringComparer.OrdinalIgnoreCase);
        if (HueLabels.TryGetValue(bar.Hue, out var prefs))
            foreach (var p in prefs) if (!used.Contains(p)) return p;
        string baseName = $"{char.ToUpperInvariant(bar.Hue[0])}{bar.Hue[1..]} bar";
        if (!used.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) if (!used.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    /// <summary>One snapshot of the game for all bars, fresh enough and taken while this bar was on screen.</summary>
    private bool EnsureSnapshot(ProcessMemory mem, DetectedBar bar, CancellationToken ct)
    {
        bool fresh = _master != null && _master.InSnapshotMode && (DateTime.Now - _masterAt).TotalMinutes < SnapshotFreshMinutes;
        if (fresh && _masterFracs.ContainsKey(bar.Id)) return true;
        if (_master != null && _master.InSnapshotMode && _jobs.Any(j => j.Task != null && !j.Task.IsCompleted && j.Finder?.SnapshotBarFrac != null) &&
            (DateTime.Now - _masterAt).TotalSeconds < 30)
            return false; // a snapshot was just taken; this bar wasn't visible then, give the finders a moment before taking another
        if (double.IsNaN(bar.Fill)) return false;
        _master?.Dispose();
        _master = new Scanner(mem) { Aligned = true, KeepUndo = false, PlausibleHealthFilter = true, IncludeMappedMemory = AutoFinder.ShouldScanMappedMemory(mem) };
        _masterFracs.Clear();
        foreach (var b in Hud.Bars) if (!double.IsNaN(b.Fill) && b.Missed == 0) _masterFracs[b.Id] = b.Fill;
        Status?.Invoke("Taking one snapshot of the game's memory for all bars (the game is paused while it's taken)…");
        bool paused = mem.Suspend();
        var sw = Stopwatch.StartNew();
        try { _master.FirstScan(ValueKindExt.HealthKinds, ScanType.UnknownInitialValue, 0, 0, null, ct); }
        catch (Exception ex) { Diag.Log("Assistant: snapshot failed: " + ex.Message); _master.Dispose(); _master = null; return false; }
        finally { if (paused) mem.Resume(); }
        _masterAt = DateTime.Now;
        Say($"Snapshot taken ({sw.Elapsed.TotalSeconds:0.0} s). Bars at that moment: " +
            string.Join(", ", _masterFracs.Select(kv => { var b = Hud.Bars.FirstOrDefault(x => x.Id == kv.Key); return $"{(b == null ? "?" : b.Label.Length > 0 ? b.Label : b.Hue + " bar")} {kv.Value * 100:0}%"; })) + ".");
        return _masterFracs.ContainsKey(bar.Id);
    }

    private bool StartFinder(ProcessMemory mem, BarJob job, CancellationToken ct)
    {
        var bar = job.Bar;
        if (!EnsureSnapshot(mem, bar, ct)) return false;
        job.Rel = bar.FullRect;
        job.Det = new BarDetector { Cal = bar.ToCalibration(_client.Location) };
        var scanner = _master.Fork();
        job.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var item = job.Item;
        var progress = new SyncProgress(m => { item.Detail = m; });
        job.Finder = new AutoFinder(mem, scanner, job.Det, progress, bar.Label)
        {
            VerifyByWriting = Opt.Verify, PointerDepth = Opt.PointerDepth, PointerMaxOffset = Opt.PointerMaxOffset,
            SnapshotBarFrac = _masterFracs[bar.Id], Source = bar.Describe(Hud.ClientSize)
        };
        item.State = "finding";
        var token = job.Cts.Token;
        var finder = job.Finder;
        job.Task = Task.Run(() =>
        {
            try { return finder.Run(token); }
            finally { scanner.Dispose(); }
        }, token);
        Say($"{bar.Label}: auto-find started from the shared snapshot ({bar.Describe(Hud.ClientSize)}).");
        return true;
    }

    private void CollectBars()
    {
        foreach (var job in _jobs.Where(j => j.Task != null && j.Task.IsCompleted && !j.Done && j.Finder != null))
        {
            var task = job.Task; job.Task = null;
            var item = job.Item;
            if (task.IsCanceled || task.Exception?.InnerException is OperationCanceledException) { item.State = "stopped"; continue; }
            if (task.IsFaulted)
            {
                var ex = task.Exception?.InnerException ?? task.Exception;
                item.State = "error"; item.Detail = ex?.Message;
                Diag.Log($"Assistant: {job.Bar.Label} finder failed: {ex}");
                job.GaveUp++; job.RetryAt = DateTime.Now.AddMinutes(3);
                continue;
            }
            var res = task.Result;
            if (res.Entry != null)
            {
                job.Done = true;
                item.Entry = res.Entry; item.State = "found"; item.Detail = res.Message;
                Say($"{job.Bar.Label} found: {res.Entry.Kind} at {res.Entry.Describe()}, max ≈ {res.Entry.Kind.Format(res.Entry.MaxValue)}. {res.Message}");
                try { Found?.Invoke(res.Entry, item); } catch (Exception ex) { Diag.Log("Assistant: Found callback: " + ex.Message); }
                PersistBars();
            }
            else
            {
                job.GaveUp++;
                job.RetryAt = DateTime.Now.AddMinutes(job.GaveUp == 1 ? 2 : 10);
                item.State = "gave up"; item.Detail = res.Message;
                Say($"{job.Bar.Label}: gave up for now ({res.Message}) " + (job.GaveUp < 2 ? "Will try again in a couple of minutes." : "Not trying this bar again this session; use Quick start by hand."));
                if (res.Alternatives.Count > 0)
                {
                    try { Options?.Invoke(res.Alternatives); } catch { }
                }
            }
        }
    }

    private void PersistBars()
    {
        var list = new List<SavedBar>();
        foreach (var j in _jobs.Where(j => j.Done))
        {
            var r = j.Bar.FullRect;
            list.Add(new SavedBar
            {
                Label = j.Bar.Label, Vertical = j.Bar.Vertical, X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Anchor = j.Bar.Anchor,
                FillArgb = j.Bar.FillArgb, Tolerance = j.Bar.Tolerance, ClientWidth = Hud.ClientSize.Width, ClientHeight = Hud.ClientSize.Height
            });
        }
        try { SaveBars?.Invoke(list); } catch { }
    }

    // ================================================================= on-screen numbers and movement

    private void RunNumbers(ProcessMemory mem, Rectangle client, CancellationToken ct)
    {
        if (!Opt.Numbers) { _numbers?.Stop(); _numbers = null; return; }
        if (_numbers == null)
        {
            if (!Ocr.Available)
            {
                if (!_numbersUnavailableSaid)
                {
                    _numbersUnavailableSaid = true;
                    Say("On-screen numbers: Windows OCR isn't available here (" + Ocr.Unavailable + "), so numbers (ammo, gold, stats) aren't tracked. Bars still are.");
                }
                return;
            }
            _numbers = new NumberFinder(mem, Opt)
            {
                Feed = Say, Entries = Entries, Found = Found, Options = Options,
                Items = it => { lock (_items) _items.Add(it); }
            };
            _numbers.Start(ct);
            Say("On-screen numbers: reading the HUD with Windows OCR. Numbers that change while you play (ammo, gold, XP…) are found; stat screens too while open.");
        }
        _numbers.Client = client;
    }

    private void RunMovement(ProcessMemory mem, CancellationToken ct)
    {
        if (!Opt.Movement) { _movement?.Stop(); _movement = null; return; }
        if (AutoFinder.BigEndianConsole(mem) != null) return;
        if (_movement == null)
        {
            var entries = Entries?.Invoke() ?? new();
            if (entries.Any(e => e.Group == "Position" && e.Resolve(mem) != null))
            {
                _movement = new MovementFinder(mem, Opt) { Feed = Say, Entries = Entries, Found = Found, Items = it => { lock (_items) _items.Add(it); }, Input = Input };
                _movement.MarkDone("Position is already in the cheat table from an earlier session.");
                return;
            }
            // Position only after the bars have had their turn: it takes snapshots of its own.
            if (_jobs.Any(j => j.Task != null && !j.Task.IsCompleted)) return;
            if (_jobs.Count == 0 && (DateTime.Now - (_masterAt == default ? DateTime.Now : _masterAt)).TotalSeconds < 1 && Hud.Bars.Count(b => b.Live) > 0) return;
            _movement = new MovementFinder(mem, Opt) { Feed = Say, Entries = Entries, Found = Found, Items = it => { lock (_items) _items.Add(it); }, Input = Input };
            _movement.Start(ct);
        }
    }

    /// <summary>Movement keys / jump, read from the keyboard and controller; the test rig swaps in its own.</summary>
    public Func<MoveInput> Input = MovementFinder.ReadInput;

    // ================================================================= status

    private string Summary(ProcessMemory mem)
    {
        var parts = new List<string>();
        foreach (var j in _jobs)
        {
            string s = j.Item.State switch
            {
                "finding" => $"{j.Bar.Label}: {Short(j.Item.Detail)}",
                "found" or "on" => $"{j.Bar.Label} ✓",
                "gave up" => $"{j.Bar.Label}: gave up",
                _ => $"{j.Bar.Label}: {j.Item.State}"
            };
            parts.Add(s);
        }
        int live = Hud.Bars.Count(b => b.Live);
        if (parts.Count == 0)
            return $"{mem.ProcessName}: watching the HUD ({Hud.Bars.Count} shapes tracked, {live} moving). Play normally: bars that move get found.";
        if (_numbers != null) parts.Add("numbers: " + Short(_numbers.StatusLine));
        if (_movement != null) parts.Add("position: " + Short(_movement.StatusLine));
        return $"{mem.ProcessName}: " + string.Join("  ·  ", parts);
    }

    private static string Short(string s) => string.IsNullOrEmpty(s) ? "…" : s.Length > 90 ? s[..88] + "…" : s;

    private void Say(string line)
    {
        Diag.Log("Assistant: " + line);
        try { Feed?.Invoke(line); } catch { }
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; needs a sync context).</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _a;
        public SyncProgress(Action<string> a) { _a = a; }
        public void Report(string value) => _a(value);
    }
}
