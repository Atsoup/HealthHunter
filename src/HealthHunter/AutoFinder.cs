using System.Diagnostics;
using System.Text.Json;

namespace HealthHunter;

/// <summary>Outcome of an auto-find run.</summary>
public sealed class AutoFindResult
{
    public TableEntry Entry;
    public bool PathConfirmedAcrossSessions;
    public int PathsKept;
    public string Message;
    /// <summary>Values that passed the test but couldn't be told apart (added to the table unfrozen).</summary>
    public List<TableEntry> Alternatives = new();
}

/// <summary>
/// Hands-off health finder. Watches the calibrated health bar and drives the memory scanner:
///   1. Snapshots memory (tracking Int32, Float and Double at once), then every time the bar settles at a new level runs Decreased/Increased
///      with the bar-ratio filter (value ÷ bar% must stay constant = max health).
///   2. While the bar is steady, runs Unchanged scans to throw out noise.
///   3. Once few candidates remain, writes each one once and watches what the game does: copies go back by
///      themselves, the real value keeps what was written and its copies follow it. Two values that both keep
///      it are told apart at the next change of health (see the confirmation section).
///   4. Works out max health, finds pointer paths, and checks them against paths saved in earlier
///      sessions so the profile survives restarts.
/// The player only has to take damage (or heal) a few times while it runs.
/// </summary>
public sealed class AutoFinder
{
    private readonly ProcessMemory _mem;
    private readonly Scanner _scanner;
    private readonly BarDetector _bar;
    private readonly IProgress<string> _status;
    private readonly string _stat;

    public bool VerifyByWriting { get; set; } = true;
    public int PointerDepth { get; set; } = 4;
    public long PointerMaxOffset { get; set; } = 0x1000;
    /// <summary>
    /// v6: the assistant takes one snapshot for all bars and hands each finder a scanner that already holds it. This is
    /// what the bar read when that snapshot was taken; the first attempt then starts from it instead of taking its own.
    /// </summary>
    public double? SnapshotBarFrac { get; set; }
    /// <summary>What this finder is for, as the assistant shows it ("red bar at the top-left"); only used in log lines.</summary>
    public string Source { get; set; }

    private const double BarHiddenBelow = 0.02;   // bar covered by a menu, dead, or loading screen
    private const int SustainedHiddenMs = 500;    // ...for at least this long (shorter is a flash of the bar on a hit)
    private const double ChangeThreshold = 0.015; // bar must move at least 1.5% to count as a hit
    private const double SteadyEpsilon = 0.003;   // "unchanged" means within 0.3%
    private const int MaxCandidatesToVerify = 12;
    private const int MaxCandidatesWhenStalled = 40;  // ...or this many once the list stopped shrinking
    private const int StalledChanges = 5;
    private const int GiveUpStalled = 10;             // changes without shrinking, with still too many to test
    private const int TestableCountUpTo = 5000;       // once stalled, results up to this many are checked for stack values and mirrors

    public AutoFinder(ProcessMemory mem, Scanner scanner, BarDetector bar, IProgress<string> status, string stat = "Health")
    {
        _mem = mem; _scanner = scanner; _bar = bar; _status = status; _stat = stat;
    }

    public static string ChainsFileFor(string process, string stat) =>
        Path.Combine(ProfileStore.Folder, string.Concat($"{process}.{stat}".Split(Path.GetInvalidFileNameChars())) + ".paths.json");

    public string ChainsFile => ChainsFileFor(_mem.ProcessName, _stat);

    public AutoFindResult Run(CancellationToken ct)
    {
        if (!_bar.IsCalibrated) throw new InvalidOperationException("Calibrate the health bar first.");
        Diag.Log($"Auto-find started for {_stat} in {_mem.ProcessName} ({(_mem.Is64Bit ? "64" : "32")}-bit). " +
                 $"Tracking {ValueKindExt.KindsLabel(ValueKindExt.HealthKinds)} at the same time. Verify by writing: {VerifyByWriting}.");
        string console = BigEndianConsole();
        if (console != null)
        {
            Diag.Log($"Auto-find: {_mem.ProcessName} emulates the {console}, which stores numbers big-endian; not starting.");
            return new AutoFindResult
            {
                Message = $"{_mem.ProcessName} emulates the {console}, which stores numbers in the opposite byte order to a PC (big-endian). " +
                          $"HealthHunter only reads the PC's byte order, so it can't find {_stat.ToLowerInvariant()} in this emulator."
            };
        }
        bool oldMapped = _scanner.IncludeMappedMemory;
        double oldRelTol = _scanner.UnchangedRelTol;
        try
        {
            _scanner.IncludeMappedMemory = ShouldScanMappedMemory();
            _scanner.UnchangedRelTol = UnchangedRelTol;
            return RunFind(ct);
        }
        finally
        {
            _scanner.IncludeMappedMemory = oldMapped;
            _scanner.UnchangedRelTol = oldRelTol;
        }
    }

    private const double UnchangedRelTol = 0.01;   // "unchanged" while the bar stands still: at least within 1% of the value
    private const double MappedMemoryForEmulator = 512.0 * 1024 * 1024;

    private static readonly string[] EmulatorNames =
    {
        "kyty", "shadps4", "yuzu", "suyu", "sudachi", "citron", "ryujinx", "pcsx2", "ppsspp",
        "citra", "lime3ds", "azahar", "xemu", "duckstation", "retroarch", "vita3k", "melonds", "desmume", "flycast",
        "redream", "mgba", "mesen", "snes9x", "bsnes", "project64", "mupen64"
    };

    /// <summary>Emulators of consoles that store numbers big-endian. Auto-find only decodes little-endian values.</summary>
    private static readonly (string Name, string Console)[] BigEndianEmulators =
    {
        ("rpcs3", "PlayStation 3"), ("cemu", "Wii U"), ("dolphin", "GameCube and Wii"), ("xenia", "Xbox 360")
    };

    private string BigEndianConsole() => BigEndianConsole(_mem);

    public static string BigEndianConsole(ProcessMemory mem)
    {
        string name = mem.ProcessName ?? "";
        foreach (var (n, c) in BigEndianEmulators)
            if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    /// <summary>
    /// Games keep health in their own (private) memory, which is all auto-find normally reads. Emulators often keep the
    /// emulated console's memory in a shared (mapped) section instead, so for them that is read too: when the process is a
    /// known emulator, or has a lot of writable shared memory.
    /// </summary>
    private bool ShouldScanMappedMemory() => ShouldScanMappedMemory(_mem);

    public static bool ShouldScanMappedMemory(ProcessMemory mem)
    {
        var regions = mem.GetRegions(writableOnly: true, includeMapped: true);
        long mapped = regions.Where(r => r.Type == Native.MEM_MAPPED).Sum(r => r.Size);
        long other = regions.Where(r => r.Type != Native.MEM_MAPPED).Sum(r => r.Size);
        string name = mem.ProcessName ?? "";
        bool emulator = EmulatorNames.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase));
        bool include = emulator || mapped >= MappedMemoryForEmulator;
        Diag.Log($"Auto-find: writable memory {Diag.Mb(other)} private + {Diag.Mb(mapped)} shared (mapped); " +
                 (include ? $"reading the shared part too ({(emulator ? "an emulator" : "a lot of shared memory, as emulators use")})."
                          : "reading the private part (where games keep their data)."));
        return include;
    }

    private AutoFindResult RunFind(CancellationToken ct)
    {
        var found = Find(ct, out string why);
        if (found != null)
        {
            Diag.Log($"Auto-find: {_stat} is at {found.RawAddress:X} ({found.Kind}), max ≈ {found.Kind.Format(found.MaxValue)}.");
            var done = FinishWithPointers(found, ct);
            // A copy the game only refreshes on a change of health (often the one the HUD shows) can still hold a test value.
            if (_resyncNeeded) done.Message += $" (The {_stat.ToLowerInvariant()} bar may show a test value until your {_stat.ToLowerInvariant()} next changes.)";
            return done;
        }
        Diag.Log("Auto-find gave up: " + why);
        var res = new AutoFindResult
        {
            Message = $"Couldn't find {_stat.ToLowerInvariant()} automatically. {why} " +
                      "Open Diagnostics, click Copy log, and paste the log back so the cause can be pinned down."
        };
        int letter = 0;
        foreach (var (addr, kind, max) in _undecided)
        {
            _mem.TryReadValue(addr, kind, out double v);
            double m = Math.Max(max, v);
            if (!kind.IsFloating()) m = Math.Round(m);
            res.Alternatives.Add(new TableEntry
            {
                Name = $"{_stat} option {(char)('A' + letter++)}", Kind = kind, RawAddress = addr,
                MaxValue = m > 0 ? m : 100, Mode = FreezeMode.LockAtMax, FreezeValue = m > 0 ? m : 100
            });
        }
        return res;
    }

    // ------------------------------------------------------------------ narrowing

    private string Counts() => _scanner.InSnapshotMode ? "snapshot" : $"{_scanner.ResultCount:N0} candidates ({_scanner.CountByKindText()})";

    /// <summary>
    /// v5: tracks Int32, Float and Double together from one snapshot. v4 tried one type at a time, so
    /// a game that stores health as a float made the player go through a whole failed Int32 round first.
    /// </summary>
    private TableEntry Find(CancellationToken ct, out string why)
    {
        for (int attempt = 1; ; attempt++)
        {
            var e = FindOnce(ct, attempt, out why, out bool lost);
            if (e == null && _leftAtTest is long left)
            {
                why += $" Also, the value tested at {left:X} couldn't be put back safely (the bar was hidden and it changed oddly), so if " +
                       $"your {_stat.ToLowerInvariant()} looks lower or higher than it should, {RestoreVerb} or reload.";
                _leftAtTest = null;
            }
            if (e != null || !lost) return e;
            if (attempt >= MaxAttempts)
            {
                why += $" (Tried {attempt} times, each from a fresh snapshot.)";
                return null;
            }
            // The real value was thrown out somewhere (a bad bar reading, a change auto-find couldn't see). Everything left
            // is noise, so narrowing further can't bring it back; a fresh snapshot can.
            Diag.Log($"Auto-find: lost track of {_stat.ToLowerInvariant()}: {why} Starting over with a fresh snapshot (attempt {attempt + 1} of {MaxAttempts}).");
        }
    }

    private const int MaxAttempts = 3;

    /// <param name="what">Status shown while a big scan runs, without the ending (it says whether the game is paused).</param>
    private void ScanFor(ScanType type, CancellationToken ct, (double PrevFrac, double CurFrac, double Tol)? ratio, string what = null)
    {
        void Scan() => _scanner.NextScan(type, 0, 0, null, ct, ratio, revertIfEmpty: true);
        // Comparing with a snapshot, or a list this long, takes a while; a hit meanwhile would split it between two levels.
        if (_scanner.InSnapshotMode || _scanner.ResultCount > PauseAboveCandidates) Paused(Scan, ct, what);
        else Scan();
    }

    private const long PauseAboveCandidates = 1_000_000;

    /// <summary>
    /// Big scans read gigabytes and can take minutes. If health changed meanwhile, part of memory would be read at one
    /// health and part at another, and the real value would be thrown out. So the game is paused while they run.
    /// </summary>
    private void Paused(Action scan, CancellationToken ct, string what = null)
    {
        bool paused = _mem.Suspend();
        if (!paused) Diag.Log("Auto-find: couldn't pause the game for a big scan; reading memory while it runs.");
        if (what != null) _status.Report(what + (paused ? "; the game is paused until it's done…" : "…"));
        int resumed = 0;
        void ResumeOnce() { if (Interlocked.Exchange(ref resumed, 1) == 0) _mem.Resume(); }
        // Stop resumes the game straight away instead of when the scan next checks for it.
        using var stop = paused ? ct.Register(ResumeOnce) : default;
        var sw = Stopwatch.StartNew();
        try { scan(); }
        finally
        {
            if (paused)
            {
                ResumeOnce();
                // After a few seconds without a response Windows may draw the game's window faded ("Not responding"). Give it
                // a moment to come back before the bar is read again.
                if (sw.Elapsed.TotalSeconds > 3 && !ct.IsCancellationRequested) Thread.Sleep(1500);
            }
        }
    }

    private TableEntry FindOnce(CancellationToken ct, int attempt, out string why, out bool lost)
    {
        why = "";
        lost = false;
        string again = attempt > 1 ? $"Lost track of {_stat.ToLowerInvariant()}, so HealthHunter is starting over (attempt {attempt} of {MaxAttempts}). " : "";
        double frac;
        if (attempt == 1 && SnapshotBarFrac is double preset && _scanner.InSnapshotMode)
        {
            // The assistant's shared snapshot: the bar's level then is the reference, whatever it reads now.
            frac = preset;
            Diag.Log($"Auto-find ({_stat}): using the shared snapshot; the bar read {frac * 100:0.0}% when it was taken.");
        }
        else
        {
            frac = WaitForSteadyBar(ct, again + $"Waiting for the {_stat.ToLowerInvariant()} bar to be visible and steady…");
            Diag.Log($"Auto-find: bar reads {frac * 100:0.0}% at the start (fill-color match {_bar.LastMatchRatio * 100:0}% of pixels).");
            Paused(() => _scanner.FirstScan(ValueKindExt.HealthKinds, ScanType.UnknownInitialValue, 0, 0, null, ct), ct,
                   again + "Taking a snapshot of game memory (big games can take a minute or two)");
        }

        double lastFrac = frac;
        _startFrac = frac;
        int hits = 0, rejected = 0, verifiedAtHits = -1, failedVerifies = 0, ratioMisses = 0, noRoomRounds = 0, verifyRuns = 0, tooLowRounds = 0, steadyMisses = 0;
        double rejectedAt = double.NaN; // bar level of the last change nothing in memory matched, while the bar stays there
        double lastTooLow = double.NaN; // reference level (lastFrac) at the last test skipped because health was too low
        int stalled = 0;                // changes in a row that didn't shrink the list
        _lone = default;
        _changeScans = 0;
        _testableAt = -1;
        bool useRatio = true, ratioPaused = false;
        _steps.Clear();
        _shiftResults.Clear();
        _notStat.Clear();
        _undecided.Clear();
        _resyncNeeded = false;
        _leftAtTest = null;
        _scanBeforeVerify = false;
        _barHidden = false;
        // What the last test asked the player to do; shown until the next accepted change instead of the generic hint.
        string note = null;
        var sinceScan = Stopwatch.StartNew();
        int along = _bar.Cal.Vertical ? _bar.Cal.Height : _bar.Cal.Width;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            string hint = note ?? (hits == 0
                ? $"Ready. Now make your {_stat.ToLowerInvariant()} bar go down in the game (take a hit, sprint, cast…)."
                : $"{Counts()} after {hits} change(s). Change the bar again (down or up).");
            frac = WaitForSteadyBar(ct, hint);
            NoteHiddenBar();
            double delta = frac - lastFrac;
            // A rejected level is only skipped while the bar stays there. Once it moved away, the same level is scanned again
            // (stamina refilling to full between dodges of the same cost lands on it every time).
            if (Math.Abs(frac - rejectedAt) >= ChangeThreshold / 2) rejectedAt = double.NaN;

            if (Math.Abs(delta) >= ChangeThreshold && _resyncNeeded)
            {
                // First change after a shift test: copies catch up with the real value now, so this change says nothing
                // about direction. Take it as the new reference point instead of scanning.
                _resyncNeeded = false;
                _scanner.RefreshPrevious();
                Diag.Log($"Auto-find: bar {lastFrac * 100:0.0}% → {frac * 100:0.0}% after a test; used as the new reference point, not scanned.");
                lastFrac = frac;
                hits++;
                note = null;
                sinceScan.Restart();
            }
            else if (Math.Abs(delta) >= ChangeThreshold && !(Math.Abs(frac - rejectedAt) < ChangeThreshold / 2))
            {
                var type = delta < 0 ? ScanType.Decreased : ScanType.Increased;
                long countBefore = _scanner.InSnapshotMode ? long.MaxValue : _scanner.ResultCount;
                // Pixel rounding matters more when the bar is short or nearly empty.
                double tol = 0.03 + 3.0 / (Math.Max(20, along) * Math.Min(lastFrac, frac));
                _status.Report($"Bar {lastFrac * 100:0}% → {frac * 100:0}%. Scanning…");
                Diag.Log($"Auto-find: bar {lastFrac * 100:0.0}% → {frac * 100:0.0}% ({type}).");
                // After a test the bar can lag the values until the game refreshes its copies, so skip the ratio once.
                bool ratioNow = useRatio && !ratioPaused;
                ratioPaused = false;
                ScanFor(type, ct, ratioNow ? (lastFrac, frac, tol) : null, $"Bar {lastFrac * 100:0}% → {frac * 100:0}%. Comparing with the snapshot");

                if (_scanner.LastScanReverted && ratioNow && ++ratioMisses >= 2)
                {
                    // Twice in a row nothing kept the same value-to-bar ratio. Either the bar isn't drawn in
                    // proportion to the value (curved, segmented, includes armor) or the reading is off.
                    // Fall back to plain Decreased/Increased, which only needs the direction to be right.
                    useRatio = false;
                    Diag.Log("Auto-find: no value kept a constant value-to-bar ratio twice in a row; switching the ratio filter off.");
                    ScanFor(type, ct, null, $"Bar {lastFrac * 100:0}% → {frac * 100:0}%. Comparing with the snapshot");
                }

                if (_scanner.LastScanReverted)
                {
                    // Either a bad bar reading or the value was lost. Keep the old reference point, but don't scan this same
                    // bar level again: wait for the bar to move somewhere else.
                    rejectedAt = frac;
                    if (++rejected >= 4)
                    {
                        _undecided.Clear(); // values offered by an earlier test don't belong to this reason
                        why = hits == 0
                            ? "Nothing in memory went down when the bar went down. The bar reading may be wrong (check the live % on the Health bar page), or the game keeps health somewhere HealthHunter can't read."
                            : "After narrowing, nothing left in memory followed the bar any more.";
                        lost = hits > 0;
                        return null;
                    }
                    note = $"That change matched nothing in memory, so it was ignored. Change the bar again (down or up). ({Counts()})";
                    continue;
                }
                rejected = 0;
                rejectedAt = double.NaN;
                steadyMisses = 0;
                _scanBeforeVerify = false;
                if (ratioNow) ratioMisses = 0;
                hits++;
                _changeScans++;
                stalled = _scanner.ResultCount < countBefore ? 0 : stalled + 1;
                note = null;
                _steps.Add(delta);
                lastFrac = frac;
                sinceScan.Restart();
                if (stalled >= GiveUpStalled && TestableCount(stalled: true) > MaxCandidatesWhenStalled)
                {
                    // Every change keeps all of them, so more changes won't help, and testing them one by one would take too long.
                    _undecided.Clear();
                    why = $"{TestableCount(stalled: true):N0} values follow the bar exactly and never drop out (an emulator can show the same memory at " +
                          "several addresses), which is too many to test one by one. Try a Manual scan with the value you see in the game.";
                    return null;
                }
            }
            else if (!_scanner.InSnapshotMode && Math.Abs(delta) <= SteadyEpsilon && sinceScan.Elapsed.TotalSeconds >= 3)
            {
                // What the bar can't show (part of a pixel, plus what counts as steady) is a fixed share of max, so a bigger
                // share of the value when the bar is low: slow regeneration there must not throw the real value out.
                _scanner.UnchangedRelTol = Math.Max(UnchangedRelTol,
                    1.5 * (SteadyEpsilon + 1.0 / Math.Max(20, along)) / Math.Max(Math.Min(lastFrac, frac), 0.02));
                ScanFor(ScanType.Unchanged, ct, null);
                sinceScan.Restart();
                if (!_scanner.LastScanReverted) { lastFrac = frac; steadyMisses = 0; }
                else if (++steadyMisses >= 3)
                {
                    // Every value left keeps changing while the bar stands still, so none of them is health.
                    _undecided.Clear();
                    why = "The values left kept changing while the bar stood still, so none of them is the health the bar shows.";
                    lost = true;
                    return null;
                }
            }

            // Test only once per new hit, so health isn't dipped over and over.
            // Not while the values left change on their own with the bar still (they're noise, and one may be the next to go).
            // Some games keep many copies that follow health exactly and never drop out; once the list stops shrinking, test more.
            if (hits >= 2 && hits != verifiedAtHits && !_scanBeforeVerify && steadyMisses == 0 &&
                TestableCount(stalled >= StalledChanges) <= (stalled >= StalledChanges ? MaxCandidatesWhenStalled : MaxCandidatesToVerify))
            {
                verifiedAtHits = hits;
                var e = Verify(ct, out var outcome);
                if (e != null) return e;
                if (outcome != VerifyOutcome.TooLow)
                {
                    // The test wrote to memory and the player may have been hit meanwhile: take a fresh reference point.
                    lastFrac = WaitForSteadyBar(ct, "Re-reading the bar after the test…");
                    _scanner.RefreshPrevious();
                    ratioPaused = true;
                    // Rounds that only waited for room (health at the wrong level for the next test) have their own limit.
                    if (outcome != VerifyOutcome.NoRoom) verifyRuns++;
                }
                NoteHiddenBar();
                if (outcome == VerifyOutcome.TooLow)
                {
                    // Consecutive rounds only, and not the ones where health went up since the last: the player is healing
                    // (or regenerating) as asked. Impossible from the start when hits take half the bar or more (seen twice:
                    // one big drop can be several hits the bar showed as one).
                    // (Compared at the level of the scanned change that started each round, which only moves when the bar does.)
                    if (!(lastFrac > lastTooLow + SteadyEpsilon)) tooLowRounds++;
                    lastTooLow = lastFrac;
                    if (tooLowRounds >= 6 || _steps.Count(s => -s * ProbeHeadroom >= ProbeFactor * 0.97) >= 2)
                    {
                        _undecided.Clear();
                        why = $"Hits in this game take up to {BiggestHitFrac() * 100:0}% of the bar at once, so lowering {_stat.ToLowerInvariant()} " +
                              "for the test could kill the character. Untick \"Confirm by changing each value once\" and run Auto-find again: " +
                              "it then picks a value without testing it.";
                        return null;
                    }
                }
                else { tooLowRounds = 0; lastTooLow = double.NaN; }
                if (outcome == VerifyOutcome.HitDuringTest) _resyncNeeded = true; // copies refreshed on events may hold test values
                if (outcome == VerifyOutcome.NoneFound) _undecided.Clear(); // nothing from this round is worth offering
                sinceScan.Restart();
                if (outcome == VerifyOutcome.AllCopies)
                {
                    // Narrowing further can't bring the real value back, so stop here.
                    _undecided.Clear();
                    why = "The last candidates all turned out to be copies the game refreshes from somewhere else (they went back to the " +
                          "old value when changed, or followed another value). The real value may be stored as a type auto-find doesn't " +
                          "track (try a Manual scan with a 2-byte or 8-byte integer type).";
                    return null;
                }
                // Outcomes that only mean "not now" (no room, a hit mid-test, the bar hidden, health too low) don't count as failures.
                bool counts = outcome is VerifyOutcome.NoneFound or VerifyOutcome.Undecided;
                if (outcome == VerifyOutcome.NoRoom) noRoomRounds++;
                else if (counts) noRoomRounds = 0;
                if (_scanner.ResultCount == 0 || (counts && ++failedVerifies >= 3) || noRoomRounds >= 6 || verifyRuns >= 10)
                {
                    // Nothing worth offering and nothing left that behaved like health: the real value was probably lost.
                    lost = _undecided.Count == 0 && (_scanner.ResultCount == 0 || failedVerifies >= 3);
                    why = _undecided.Count == 1
                        ? $"One value behaves like {_stat.ToLowerInvariant()}, but the test couldn't finish confirming it. It's been added to the " +
                          "cheat table (not frozen). " + (IsHealth
                            ? "Freeze it and take a few hits that would normally kill you: if you survive, that's the one."
                            : $"Freeze it and {UseVerb} a lot: if it never runs out, that's the one.")
                        : _undecided.Count > 0
                        ? $"{_undecided.Count} values behave like {_stat.ToLowerInvariant()} and the test couldn't tell which one the game uses. " +
                          (IsHealth
                            ? "They've been added to the cheat table (not frozen). Freeze one and take a few hits that would normally kill you: " +
                              "if you survive, that's the one; if you die anyway, it only kept the bar full, so delete it and try the next."
                            : $"They've been added to the cheat table (not frozen). Freeze one and {UseVerb} a lot: if it never runs out, that's " +
                              "the one; otherwise delete it and try the next.")
                        : "A few candidates were left, but none of them behaved like the real value when tested.";
                    return null;
                }
                note = outcome switch
                {
                    VerifyOutcome.Undecided => $"Couldn't tell the remaining values apart yet. {Cap(UseVerb)} again; HealthHunter will test again.",
                    VerifyOutcome.NoRoom when _needUp == true => $"{Cap(_stat)} is too low to finish the test safely. {Cap(RestoreVerb)}; HealthHunter will test again after that.",
                    VerifyOutcome.NoRoom when _needUp == false => $"{Cap(_stat)} is too high to finish the test. {Cap(UseVerb)} once more; HealthHunter will test again.",
                    VerifyOutcome.NoRoom => $"{Cap(_stat)} is too high or too low to finish the test right now. {Cap(UseVerb)} or {RestoreVerb}; HealthHunter will test again.",
                    VerifyOutcome.Pending => $"One more change of {_stat.ToLowerInvariant()} is needed before the next test. {Cap(UseVerb)}; HealthHunter will test again.",
                    VerifyOutcome.Unconfirmed => $"One value is left, but changing it didn't move the bar, so it needs a little more checking. {Cap(UseVerb)} or {RestoreVerb} once or twice more; HealthHunter will test again.",
                    VerifyOutcome.HitDuringTest => $"Inconclusive (did you take a hit during the test?). {Cap(UseVerb)} again; HealthHunter will test again.",
                    VerifyOutcome.Interrupted => $"The bar was hidden during the test (menu, loading or death), so nothing was concluded. {Cap(UseVerb)} again once you're back; HealthHunter will test again.",
                    VerifyOutcome.TooLow => $"{Cap(_stat)} is too low to test safely (the test lowers it for a moment). {Cap(RestoreVerb)}; HealthHunter will test again after that.",
                    _ => $"None of the remaining candidates passed the test. {Cap(UseVerb)} again; HealthHunter will test again after narrowing further."
                } + $" ({Counts()})";
                if (_leftAtTest is long left)
                {
                    // Said once, ahead of the usual hint.
                    note = $"The value tested at {left:X} couldn't be put back safely (the bar was hidden and it changed oddly), so if your " +
                           $"{_stat.ToLowerInvariant()} looks lower or higher than it should, {RestoreVerb} or reload. " + note;
                    _leftAtTest = null;
                }
                _status.Report(note);
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>
    /// Candidates the test would write to. Values on a thread stack (temporaries such as drawing arguments, which can follow
    /// health exactly and never drop out) are never written, so they don't count towards the limit. Neither do extra views
    /// of the same memory (emulators often map the console's memory at several addresses): writing one changes them all,
    /// so the test skips the others as copies.
    /// </summary>
    /// <param name="stalled">The list stopped shrinking: then it's worked out for up to a few thousand results (once per scan),
    /// since a dozen mirrors of a few copies is still few enough to test. Otherwise only for a few hundred.</param>
    private long TestableCount(bool stalled = false)
    {
        if (_scanner.InSnapshotMode) return long.MaxValue;
        long n = _scanner.ResultCount;
        if (n <= MaxCandidatesToVerify || n > (stalled ? TestableCountUpTo : 4 * MaxCandidatesWhenStalled)) return n;
        if (_testableAt != _scanner.ScanCount)
        {
            _testableAt = _scanner.ScanCount;
            var guards = new Dictionary<long, long>();
            var left = Enumerable.Range(0, (int)n).Select(i => (Addr: _scanner.AddressAt(i), Kind: _scanner.KindAt(i)))
                                 .Where(c => !InThreadStack(c.Addr, guards)).ToList();
            _testable = left.Count - MappedViewsCount(left);
        }
        return _testable;
    }
    private int _testableAt = -1;
    private long _testable;

    /// <summary>
    /// How many of these are a second (third…) view of memory another one already shows: in shared memory, same type,
    /// same value at the same place in its page, and the whole 4 KB page identical.
    /// </summary>
    private int MappedViewsCount(List<(long Addr, ValueKind Kind)> cands)
    {
        int extra = 0;
        var sz = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
        var groups = cands.Where(c => Native.VirtualQueryEx(_mem.Handle, (IntPtr)c.Addr, out var m, sz) != IntPtr.Zero && m.Type == Native.MEM_MAPPED)
                          .Select(c => (c.Addr, c.Kind, Value: _mem.TryReadValue(c.Addr, c.Kind, out double v) ? v : double.NaN))
                          .Where(c => !double.IsNaN(c.Value))
                          .GroupBy(c => (c.Kind, Offset: c.Addr & 0xFFF, c.Value));
        foreach (var g in groups.Where(g => g.Count() > 1))
        {
            var pages = new List<byte[]>();
            foreach (var c in g)
            {
                var page = new byte[0x1000];
                if (_mem.Read(c.Addr & ~0xFFFL, page, 0, page.Length) != page.Length) continue;
                if (pages.Any(q => q.AsSpan().SequenceEqual(page))) extra++;
                else pages.Add(page);
            }
        }
        return extra;
    }

    /// <summary>
    /// Polls the bar until four readings in a row agree (animations finished). Bars that keep creeping
    /// (health regeneration, damage over time) never sit perfectly still, so after a few seconds a
    /// looser test is accepted instead of waiting forever.
    /// </summary>
    private double WaitForSteadyBar(CancellationToken ct, string message)
    {
        _status.Report(message);
        var recent = new Queue<double>();
        var sw = Stopwatch.StartNew();
        var hiddenFor = new Stopwatch();
        int hidden = 0, black = 0;
        bool warnedHidden = false, warnedResync = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!_mem.IsAlive) throw new InvalidOperationException("The game closed while auto-find was running.");
            double f = MeasureBar();
            if (double.IsNaN(f) || f < BarHiddenBelow)
            {
                recent.Clear();
                hidden++;
                // A flash of the bar (many games flash it on a hit) isn't the bar going away; a menu, loading screen or death lasts.
                if (!hiddenFor.IsRunning) hiddenFor.Restart();
                if (hiddenFor.ElapsedMilliseconds >= SustainedHiddenMs) _barHidden = true;
                if (_bar.LastCaptureBlack) black++;
                if (_resyncNeeded && !warnedResync && hiddenFor.Elapsed.TotalSeconds >= 3 && !_bar.LastCaptureBlack)
                {
                    // A copy the game only updates when health changes can still hold a test value, and the bar may be drawn from it.
                    warnedResync = true;
                    _status.Report($"The {_stat.ToLowerInvariant()} bar reads as empty. If it isn't really, it may still show a test value: " +
                                   $"{UseVerb} or {RestoreVerb} once and it will catch up.");
                }
                if (!warnedHidden && hidden >= 60)
                {
                    warnedHidden = true;
                    Diag.Log(black > hidden / 2
                        ? "Bar: the screen capture is black. Exclusive fullscreen or HDR stops screen capture; use Borderless/Windowed and turn HDR off."
                        : $"Bar: reads empty for a while (fill-color match {_bar.LastMatchRatio * 100:0}%). The box or color may be off; redraw the bar box at full health.");
                    _status.Report(black > hidden / 2
                        ? "The screen capture is black. Switch the game to Borderless or Windowed and turn HDR off."
                        : "The bar reads as empty. If it isn't, redraw the bar box at full health.");
                }
                Thread.Sleep(150);
                continue;
            }
            hiddenFor.Reset();
            recent.Enqueue(f);
            if (recent.Count > 8) recent.Dequeue();
            var last4 = recent.Skip(Math.Max(0, recent.Count - 4)).ToList();
            if (last4.Count == 4 && last4.Max() - last4.Min() <= SteadyEpsilon) return last4.Average();
            if (sw.Elapsed.TotalSeconds > 2.5 && recent.Count >= 6 && recent.Max() - recent.Min() <= 0.02)
                return recent.OrderBy(x => x).ElementAt(recent.Count / 2);
            Thread.Sleep(110);
        }
    }

    /// <summary>
    /// After the bar was hidden (menu, loading screen, death) a reload may have moved things in memory, and slots that
    /// held candidates may hold something else now. Nothing is written again until a scan has checked them.
    /// </summary>
    private void NoteHiddenBar()
    {
        if (!_barHidden) return;
        _barHidden = false;
        if (!_scanBeforeVerify && !_scanner.InSnapshotMode)
            Diag.Log("Auto-find: the bar was hidden for a while; the next test waits until a change of the bar has been scanned.");
        _scanBeforeVerify = true;
    }

    private double MeasureBar()
    {
        try
        {
            double f = _bar.Measure(out var bmp);
            bmp?.Dispose();
            return f;
        }
        catch { return double.NaN; }
    }

    // ------------------------------------------------------------------ confirmation
    //
    // v5.1: games often keep health in one place and copy it elsewhere, for example for the HUD. v5 confirmed a
    // candidate by holding it at half for a second and watching the bar, which a HUD copy passes too, and freezing
    // a copy doesn't keep the player alive. Now each candidate is written once and then left alone:
    //   - a copy the game refreshes all the time goes straight back to its old value;
    //   - the real value keeps what was written, and copies refreshed from it follow it.
    // Two values can both keep what was written without following each other: typically the real value and a copy
    // the game only updates when health changes. Those are told apart by ShiftTest, one change of health each.

    private sealed class Probe
    {
        public int Index;
        public long Addr;
        public ValueKind Kind;
        public double Orig, Test, After;
        public bool Stuck, Ramped, Reverted, BarMoved, Died;
        /// <summary>The bar was hidden and the value changed oddly, so the test value was left in (the slot may be reused).</summary>
        public bool LeftAtTest;
        /// <summary>The game changed health while the test value was in, so this round can't conclude anything.</summary>
        public bool Event;
        public readonly List<int> Followers = new();
        /// <summary>Followers that caught up with the test value before it changed by itself: certainly copied from it.
        /// The others may have been moved by a hit or a heal at the same time, so they are tested themselves.</summary>
        public readonly HashSet<int> Strong = new();
        /// <summary>Other views of the very same memory (emulators).</summary>
        public readonly HashSet<int> Aliases = new();
        /// <summary>Watched (never written) values that followed the test value.</summary>
        public readonly List<int> Watched = new();
    }

    private enum VerifyOutcome { NoneFound, HitDuringTest, AllCopies, Undecided, NoRoom, Pending, TooLow, Interrupted, Unconfirmed }
    private enum ShiftVerdict { Real, Copy, Unclear, NoEvent, NoRoom }
    private enum SeparateEnd { Undecided, NoRoom, Pending, AllCopies }

    private const double ProbeFactor = 0.6;      // the single test write sets a candidate to 60% of its value
    private const int ProbeWatchMs = 700;        // then watches it, the other candidates and the bar this long
    private const int RevertWithinMs = 300;      // a copy refreshed all the time is back by then (slow emulators, UI timers included)
    private const int FollowerReturnMs = 1500;   // how long values that followed the test get to come back after it...
    private const int FollowerReturnMaxMs = 4000; // ...or this long while they're on their way back (animated HUD numbers)
    private const double ProbeHeadroom = 1.2;    // health × 60% must stay above 1.2 × the biggest hit seen
    private const int ShiftWaitSeconds = 60;     // how long ShiftTest waits for the player to take a hit
    private const int MaxRootsToSeparate = 3;    // each one costs a hit

    /// <summary>Values that passed the test but couldn't be told apart; handed to the user when auto-find gives up.</summary>
    private readonly List<(long Addr, ValueKind Kind, double Max)> _undecided = new();

    /// <summary>Bar changes accepted while narrowing (signed fraction of the bar): tells how big hits are in this game.</summary>
    private readonly List<double> _steps = new();

    /// <summary>
    /// Set when a test moved a value and the game changed health meanwhile: copies the game only refreshes on a change of
    /// health may still hold a test value, so the next change re-syncs them and must not be used to narrow (it could drop
    /// the real value).
    /// </summary>
    private bool _resyncNeeded;

    /// <summary>A test value that couldn't be put back safely (the bar was hidden and the value changed oddly); the player is told.</summary>
    private long? _leftAtTest;

    /// <summary>ShiftTest results per address for this run: with the value moved above the others, and below.</summary>
    private readonly Dictionary<long, (ShiftVerdict? Up, ShiftVerdict? Down)> _shiftResults = new();

    /// <summary>
    /// v6: values that kept a test write but then sat still while the bar changed by a good margin: whatever they are,
    /// they aren't what the bar shows (a counter that happened to move with the bar during the scans). They are left out
    /// of the shift tests, so a regenerating stat with one such value beside it no longer stays undecided for good.
    /// </summary>
    private readonly HashSet<long> _notStat = new();
    private const double BarChangeMin = 0.04;    // the bar has to move at least this much for that rule

    /// <summary>Bar reading when the snapshot was taken (for the value-to-bar ratio of overlapping candidates).</summary>
    private double _startFrac;

    /// <summary>Set by <see cref="WaitForSteadyBar"/> whenever the bar was hidden (menu, loading screen, death).</summary>
    private bool _barHidden;

    /// <summary>Set after the bar was hidden: candidates must go through a scan again before anything is written to them.</summary>
    private bool _scanBeforeVerify;

    /// <summary>Changes of the bar scanned (and matched) so far in this attempt.</summary>
    private int _changeScans;

    /// <summary>
    /// A lone source that didn't move the bar when written, and the change count when it was first seen. It is accepted
    /// once it has followed <see cref="LoneConfirmHits"/> more changes of the bar.
    /// </summary>
    private (long Addr, int Hits) _lone;
    private const int LoneConfirmHits = 2;

    /// <summary>After a NoRoom: true when health has to go up before the missing test fits, false when down, null if unknown.</summary>
    private bool? _needUp;


    private bool IsHealth => _stat.Equals("Health", StringComparison.OrdinalIgnoreCase);
    private string UseVerb => IsHealth ? "take a hit"
        : _stat.StartsWith("Stamina", StringComparison.OrdinalIgnoreCase) || _stat.StartsWith("Energy", StringComparison.OrdinalIgnoreCase) ? "use some stamina (sprint, dodge)"
        : _stat.StartsWith("Mana", StringComparison.OrdinalIgnoreCase) || _stat.StartsWith("MP", StringComparison.OrdinalIgnoreCase) ? "use some mana (cast a spell)"
        : $"make the {_stat.ToLowerInvariant()} go down";
    private string RestoreVerb => IsHealth ? "heal" : "let it refill";
    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private double BiggestHitFrac() => _steps.Where(s => s < 0).Select(s => -s).DefaultIfEmpty(0.15).Max();

    private static double Progress(double from, double to, double now) => (now - from) / (to - from);

    /// <summary>Whether the 4 KB pages holding two addresses are byte for byte the same (two views of one memory).</summary>
    private bool SamePage(long a, long b)
    {
        var pa = new byte[0x1000];
        var pb = new byte[0x1000];
        return _mem.Read(a & ~0xFFFL, pa, 0, pa.Length) == pa.Length && _mem.Read(b & ~0xFFFL, pb, 0, pb.Length) == pb.Length &&
               pa.AsSpan().SequenceEqual(pb);
    }

    /// <summary>Average speed between two readings.</summary>
    private static double SpanRate(List<(long Ms, double V)> pts, int a, int b) => (pts[b].V - pts[a].V) / Math.Max(1, pts[b].Ms - pts[a].Ms);

    private TableEntry Verify(CancellationToken ct, out VerifyOutcome outcome)
    {
        outcome = VerifyOutcome.NoneFound;
        int n = (int)_scanner.ResultCount;
        var all = Enumerable.Range(0, n).Select(i => (Addr: _scanner.AddressAt(i), Kind: _scanner.KindAt(i), Idx: i)).ToList();
        Diag.Log($"Auto-find: verifying {n} candidate(s): " + string.Join(", ", all.Select(c => $"{c.Addr:X} {c.Kind}")));

        if (!VerifyByWriting)
        {
            // Without testing, prefer a floating-point candidate (most modern games), else the first one. Never a value on a
            // thread stack if there's anything else: freezing it would write into the stack all the time.
            var pool = all.Where(c => !InThreadStack(c.Addr)).ToList();
            if (pool.Count == 0) pool = all;
            var pick = pool.FirstOrDefault(c => c.Kind.IsFloating());
            if (pick.Addr == 0) pick = pool[0];
            return MakeEntry(pick.Addr, pick.Kind, WaitForSteadyBar(ct, "Reading bar…"));
        }

        // Overlapping candidates (say a Float that is really the upper half of a Double) can't both be written: writing one
        // scrambles the other. Keep the one whose value-to-bar ratio held since the snapshot (health ÷ bar = max health).
        var byAddr = all.OrderBy(c => c.Addr).ToList();
        var drop = new HashSet<int>();
        var tangled = new HashSet<int>();
        double fracNow = double.NaN;
        bool canTell = false;
        double RatioDrift((long Addr, ValueKind Kind, int Idx) c)
        {
            double first = _scanner.FirstAt(c.Idx);
            if (!(_startFrac > 0.02) || !(fracNow > 0.02) || !double.IsFinite(first) || !_mem.TryReadValue(c.Addr, c.Kind, out double v)) return double.NaN;
            double r0 = first / _startFrac, r1 = v / fracNow, m = Math.Max(Math.Abs(r0), Math.Abs(r1));
            return m > 0 ? Math.Abs(r1 - r0) / m : double.NaN;
        }
        for (int i = 0; i < byAddr.Count; i++)
            for (int j = i + 1; j < byAddr.Count && byAddr[j].Addr < byAddr[i].Addr + byAddr[i].Kind.Size(); j++)
            {
                if (double.IsNaN(fracNow))
                {
                    fracNow = WaitForSteadyBar(ct, "Reading bar…");
                    canTell = Math.Abs(fracNow - _startFrac) >= 0.15;
                }
                var (ci, cj) = (byAddr[i], byAddr[j]);
                double di = RatioDrift(ci), dj = RatioDrift(cj);
                string keep;
                if (canTell && di <= 0.1 && !(dj <= 0.1)) { drop.Add(cj.Idx); keep = $"keeping {ci.Addr:X}"; }
                else if (canTell && dj <= 0.1 && !(di <= 0.1)) { drop.Add(ci.Idx); keep = $"keeping {cj.Addr:X}"; }
                else { tangled.Add(ci.Idx); tangled.Add(cj.Idx); keep = "can't tell which is real yet"; }
                Diag.Log($"Auto-find: {ci.Addr:X} ({ci.Kind}) and {cj.Addr:X} ({cj.Kind}) overlap in memory; their value-to-bar ratios " +
                         $"moved {di:P0} and {dj:P0} since the start; {keep}.");
            }
        if (tangled.Count > 0)
        {
            // Writing either one could wreck the other, and leaving both out could leave a copy as the only candidate.
            _undecided.Clear();
            outcome = canTell ? VerifyOutcome.Undecided : VerifyOutcome.Pending;
            Diag.Log("Auto-find: not writing anything this round" + (canTell ? "." : " (the bar hasn't moved far enough from where it started)."));
            return null;
        }
        if (drop.Count > 0) all = all.Where(c => !drop.Contains(c.Idx)).ToList();

        // Find clears _barHidden before calling Verify, so this catches the bar going away at any point of this round.
        bool Interrupted()
        {
            if (!_barHidden) return false;
            Diag.Log("Auto-find: the bar was hidden (menu, loading screen or death) during this round; not concluding anything.");
            return true;
        }
        if (Interrupted()) { outcome = VerifyOutcome.Interrupted; return null; }
        var cands = SafeToWrite(all, out var watch);
        if (cands.Count == 0) return null;
        _status.Report($"{cands.Count} candidates left. Testing each one: it is changed once and HealthHunter watches what the game does with it (the bar may dip for a moment)…");
        // Reference point for working out max later: the bar and every candidate before anything is written.
        double frac0 = WaitForSteadyBar(ct, "Reading bar…");
        if (Interrupted()) { outcome = VerifyOutcome.Interrupted; return null; }
        var v0 = cands.Select(c => _mem.TryReadValue(c.Addr, c.Kind, out double v) ? v : 0).ToArray();

        var probes = new List<Probe>();
        var followed = new Dictionary<int, long>();   // candidate → the tested value it followed
        var untested = new List<int>();               // candidates whose test write failed
        int skipped = 0;
        double barNow = frac0;
        for (int i = 0; i < cands.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (followed.TryGetValue(i, out long src))
            {
                skipped++;
                // Copied from a value already tested, so it can't be the source; writing it only risks the game's stability
                // (such copies include drawing arguments and other temporaries).
                Diag.Log($"Auto-find: not testing {cands[i].Addr:X} ({cands[i].Kind}): it followed {src:X}, so it's a copy of it.");
                continue;
            }
            _status.Report($"Testing {cands[i].Addr:X} ({cands[i].Kind})… keep the bar visible.");
            double before = barNow;
            if (IsHealth && before * ProbeFactor < ProbeHeadroom * BiggestHitFrac())
            {
                // The real value is held at 60% for a moment; a normal hit then must not be able to kill.
                Diag.Log($"Auto-find: health at {before * 100:0}% is too low to dip to {ProbeFactor:P0} of it safely " +
                         $"(biggest hit so far ≈ {BiggestHitFrac() * 100:0}% of the bar); not testing now.");
                outcome = VerifyOutcome.TooLow;
                return null;
            }
            var p = ProbeOne(cands, i, before, watch, ct, out bool writeFailed);
            ct.ThrowIfCancellationRequested(); // ProbeOne stops early when cancelled, after putting the value back
            if (p == null)
            {
                if (writeFailed) untested.Add(i);
                continue;
            }
            foreach (int j in p.Strong) followed.TryAdd(j, p.Addr);
            if (p.LeftAtTest) _leftAtTest = p.Addr;
            if (p.Died)
            {
                Diag.Log($"Auto-find: {p.Addr:X} went to 0 and the bar emptied during the test (the player died?); nothing was written back.");
                outcome = VerifyOutcome.HitDuringTest;
                return null;
            }
            if (p.Event)
            {
                // Something changed health while the test value was in (a hit, a heal), so what the values did says nothing
                // reliable: a value hit meanwhile can look like it followed the test, and a healed one like it went back.
                Diag.Log($"Auto-find: {p.Addr:X} or the values that followed it changed in a way the test doesn't explain (a hit or heal during the test?); not concluding anything this round.");
                outcome = VerifyOutcome.HitDuringTest;
                return null;
            }
            // The bar must come back once the value is restored; if not, the player was hit mid-test.
            double after = WaitForSteadyBar(ct, p.BarMoved ? $"Bar reacted to {p.Addr:X}. Checking that it comes back…" : "Reading bar…");
            if (Interrupted()) { outcome = VerifyOutcome.Interrupted; return null; }
            if (after < before * 0.9)
            {
                Diag.Log($"Auto-find: the bar is at {after * 100:0.0}% after testing {p.Addr:X} (was {before * 100:0.0}%); probably hit during the test.");
                outcome = VerifyOutcome.HitDuringTest;
                return null;
            }
            barNow = after;
            probes.Add(p);
        }

        // Sources: values that kept the write, or kept it and then climbed back (regeneration) pulling copies along.
        var sources = probes.Where(p => p.Stuck || (p.Ramped && p.Followers.Count > 0)).ToList();
        // A value that followed another source's write is copied from it, so it can't be the source itself.
        var roots = sources.Where(p => !sources.Any(q => q != p && q.Strong.Contains(p.Index))).ToList();
        if (roots.Count == 0 && sources.Count > 0) roots = sources.OrderByDescending(p => p.Followers.Count).Take(1).ToList();
        // A copy the game refreshes all the time that followed none of the sources is refreshed from somewhere else.
        var unexplained = probes.Where(r => r.Reverted && !sources.Any(s => s.Followers.Contains(r.Index))).ToList();
        Diag.Log($"Auto-find: {probes.Count} tested ({skipped} more skipped as copies), {probes.Count(p => p.Reverted)} went back by themselves (copies), " +
                 $"{probes.Count(p => p.Stuck)} kept the written value, {probes.Count(p => p.Ramped)} climbed back gradually; source candidates: " +
                 (roots.Count == 0 ? "none" : string.Join(", ", roots.Select(r => $"{r.Addr:X} {r.Kind}"))) +
                 (unexplained.Count > 0 ? $"; {unexplained.Count} copy/copies followed none of them ({string.Join(", ", unexplained.Select(u => $"{u.Addr:X}"))})" : "") + ".");

        double MaxOf(Probe p) => frac0 > 0.02 && v0[p.Index] > 0 ? v0[p.Index] / frac0 : 0;
        double FracNow(Probe p)
        {
            double max = MaxOf(p);
            return max > 0 && _mem.TryReadValue(p.Addr, p.Kind, out double v) ? v / max : WaitForSteadyBar(ct, "Reading bar…");
        }
        void Offer(IEnumerable<Probe> ps)
        {
            _undecided.Clear();
            foreach (var p in ps) _undecided.Add((p.Addr, p.Kind, MaxOf(p)));
        }
        // Nothing left that could be the real value: every tested value went back by itself or was proven a copy.
        // (A value that went back while others followed it might be the real value refilling very fast, so that isn't proof
        // enough to give up.)
        bool OnlyCopiesLeft() => probes.Count > 0 &&
            probes.All(p => (p.Reverted && p.Followers.All(p.Aliases.Contains)) || Decided(p.Addr) == ShiftVerdict.Copy);

        if (roots.Count == 0)
        {
            outcome = OnlyCopiesLeft() ? VerifyOutcome.AllCopies : VerifyOutcome.NoneFound;
            return null;
        }
        // Values that sat still while the bar moved (see _notStat) aren't the stat at all: out, and no use as a reference.
        var junk = roots.Where(r => _notStat.Contains(r.Addr)).ToList();
        if (junk.Count > 0)
        {
            roots = roots.Except(junk).ToList();
            Diag.Log($"Auto-find: leaving out {string.Join(", ", junk.Select(r => $"{r.Addr:X}"))}: {(junk.Count == 1 ? "it" : "they")} didn't move when the bar did.");
            if (roots.Count == 0) { outcome = VerifyOutcome.NoneFound; return null; }
        }
        // Values earlier shift tests in this run proved to be copies are out. They still serve as the reference for testing
        // the rest: a value is never taken as health just because the others were ruled out, since a big hit or a heal at
        // the wrong moment can make the real value look like a copy.
        var allRoots = roots;
        var proven = roots.Where(r => Decided(r.Addr) == ShiftVerdict.Copy).ToList();
        if (proven.Count > 0)
        {
            roots = roots.Except(proven).ToList();
            Diag.Log($"Auto-find: leaving out {string.Join(", ", proven.Select(r => $"{r.Addr:X}"))}: earlier tests showed {(proven.Count == 1 ? "it's a copy" : "they're copies")}.");
            if (roots.Count == 0)
            {
                outcome = OnlyCopiesLeft() ? VerifyOutcome.AllCopies : VerifyOutcome.NoneFound;
                return null;
            }
        }
        if (unexplained.Count > 0)
        {
            // Those copies come from a value that isn't among the candidates, which is then the likely real one.
            Diag.Log("Auto-find: not accepting any candidate, because some copies are refreshed from a value auto-find isn't tracking.");
            Offer(roots);
            outcome = VerifyOutcome.Undecided;
            return null;
        }
        // A value whose test write failed and that followed none of the tested ones may be the real value itself, with
        // everything tested being copies of it.
        var unseen = untested.Where(i => !followed.ContainsKey(i)).Select(i => cands[i].Addr).ToList();
        if (unseen.Count > 0)
        {
            Diag.Log($"Auto-find: not accepting any candidate, because {unseen.Count} value(s) couldn't be written and followed none of the tested ones " +
                     $"({string.Join(", ", unseen.Select(a => $"{a:X}"))}); one of them may be the real value.");
            Offer(roots);
            outcome = VerifyOutcome.Undecided;
            return null;
        }
        // Values on a thread stack are never written. Ones that followed nothing are usually locals of the game's damage code
        // (they change on hits only), so they don't block anything; logged in case health itself lives on a stack.
        var still = watch.Where((w, k) => !probes.Any(p => p.Watched.Contains(k))).ToList();
        if (still.Count > 0)
            Diag.Log($"Auto-find: {still.Count} value(s) on a thread stack followed none of the tested ones ({string.Join(", ", still.Select(w => $"{w.Addr:X} {w.Kind}"))}).");
        if (roots.Count == 1 && proven.Count == 0)
        {
            var r = roots[0];
            // The bar reacting to the write is the strongest sign. Without it (the bar may be drawn from a copy the game only
            // refreshes on a change of health, or the value may be something else entirely), it has to keep following the bar
            // for a couple more changes first.
            if (r.BarMoved || _lone.Addr == r.Addr && _changeScans >= _lone.Hits + LoneConfirmHits)
                return MakeEntry(r.Addr, r.Kind, FracNow(r));
            if (_lone.Addr != r.Addr) _lone = (r.Addr, _changeScans);
            Diag.Log($"Auto-find: {r.Addr:X} is the only value that kept what was written, but the bar didn't move when it was changed; " +
                     $"accepting it once it has followed {LoneConfirmHits} more changes of the bar ({_changeScans - _lone.Hits} so far).");
            outcome = VerifyOutcome.Unconfirmed;
            return null;
        }
        if (roots.Count > MaxRootsToSeparate)
        {
            Diag.Log($"Auto-find: {roots.Count} values behave alike; too many to tell apart one hit at a time.");
            Offer(roots);
            outcome = VerifyOutcome.Undecided;
            return null;
        }
        if (Interrupted()) { outcome = VerifyOutcome.Interrupted; return null; }

        var real = SeparateRoots(roots, allRoots, v0, frac0, ct, out var end);
        if (real != null)
            return MakeEntry(real.Addr, real.Kind, FracNow(real));
        if (end == SeparateEnd.AllCopies)
        {
            // Proven copies are never offered as health.
            outcome = OnlyCopiesLeft() ? VerifyOutcome.AllCopies : VerifyOutcome.NoneFound;
            return null;
        }
        Offer(roots);
        outcome = end switch
        {
            SeparateEnd.NoRoom => VerifyOutcome.NoRoom,
            SeparateEnd.Pending => VerifyOutcome.Pending,
            _ => VerifyOutcome.Undecided
        };
        return null;
    }

    /// <summary>
    /// Drops candidates that are unsafe or pointless to write: values on a thread stack (temporary locals, such as
    /// drawing arguments that happen to follow health; writing them can crash the game) and slots that hold
    /// something else now than at the last scan (reused memory).
    /// </summary>
    /// <param name="watch">Values on a thread stack that hold something health-like: never written, only watched during the tests.</param>
    private List<(long Addr, ValueKind Kind)> SafeToWrite(List<(long Addr, ValueKind Kind, int Idx)> cands, out List<(long Addr, ValueKind Kind)> watch)
    {
        var keep = new List<(long Addr, ValueKind Kind)>();
        watch = new List<(long Addr, ValueKind Kind)>();
        foreach (var c in cands)
        {
            string why = null;
            double prev = _scanner.PreviousAt(c.Idx);
            bool readable = _mem.TryReadValue(c.Addr, c.Kind, out double v) && double.IsFinite(v) && v > 0;
            if (InThreadStack(c.Addr))
            {
                why = "it is on a thread stack";
                if (readable && Math.Abs(v - prev) <= 0.10 * Math.Abs(prev)) watch.Add((c.Addr, c.Kind));
            }
            else if (!readable) why = "it can't be read or isn't positive";
            else if (Math.Abs(v - prev) > 0.10 * Math.Abs(prev)) why = $"it holds {c.Kind.Format(v)} now but {c.Kind.Format(prev)} at the last scan";
            if (why == null) keep.Add((c.Addr, c.Kind));
            else Diag.Log($"Auto-find: not testing {c.Addr:X} ({c.Kind}): {why}.");
        }
        return keep;
    }

    /// <summary>Thread stacks have a guard page inside their allocation, below the part in use.</summary>
    /// <param name="guards">When checking many addresses: the lowest guard page found per allocation, so each is walked once.</param>
    private bool InThreadStack(long addr, Dictionary<long, long> guards = null) => InThreadStack(_mem, addr, guards);

    public static bool InThreadStack(ProcessMemory mem, long addr, Dictionary<long, long> guards = null)
    {
        var sz = (IntPtr)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
        if (Native.VirtualQueryEx(mem.Handle, (IntPtr)addr, out var mbi, sz) == IntPtr.Zero || mbi.Type != Native.MEM_PRIVATE) return false;
        long ab = (long)mbi.AllocationBase;
        if (guards != null && guards.TryGetValue(ab, out long known)) return known < addr;
        long guard = long.MaxValue;
        for (long q = ab; guards != null || q < addr;)
        {
            if (Native.VirtualQueryEx(mem.Handle, (IntPtr)q, out var m, sz) == IntPtr.Zero || (long)m.AllocationBase != ab) break;
            if ((m.Protect & Native.PAGE_GUARD) != 0) { guard = q; break; }
            long next = (long)m.BaseAddress + (long)m.RegionSize;
            if (next <= q) break;
            q = next;
        }
        if (guards != null) guards[ab] = guard;
        return guard < addr;
    }

    /// <summary>
    /// Writes one candidate once, watches it, the other candidates and the bar, then puts it back if the game
    /// didn't. Returns null when the candidate can't be tested (unreadable, zero, an integer too small to lower, or the
    /// write failed: <paramref name="writeFailed"/>). What it did decides what it is:
    ///   - back at its old value within <see cref="RevertWithinMs"/> and staying there: a copy the game refreshes;
    ///   - back by itself fast at first, then slower and slower: a copy the game eases towards the real value (HUD numbers);
    ///   - kept the test value, give or take small upward ticks: a source (the real value, or a copy the game only
    ///     refreshes on a change of health);
    ///   - kept it, then climbed back in several small steps: a source that regenerates;
    ///   - anything else (a jump, a drop, back somewhere else, a follower that doesn't come back): the game changed
    ///     health meanwhile, and nothing from this round is reliable (<see cref="Probe.Event"/>).
    /// </summary>
    private Probe ProbeOne(List<(long Addr, ValueKind Kind)> cands, int idx, double before, List<(long Addr, ValueKind Kind)> watch,
                           CancellationToken ct, out bool writeFailed)
    {
        writeFailed = false;
        var (addr, kind) = cands[idx];
        int n = cands.Count;
        var bases = new double[n];
        var readable = new bool[n];
        for (int j = 0; j < n; j++)
            readable[j] = _mem.TryReadValue(cands[j].Addr, cands[j].Kind, out bases[j]) && double.IsFinite(bases[j]);
        var wBases = watch.Select(w => _mem.TryReadValue(w.Addr, w.Kind, out double v) && double.IsFinite(v) ? v : 0).ToArray();
        if (!readable[idx] || bases[idx] <= 0) return null;
        // The test value is worked out from the value at the instant it's written (the game paused for that moment), so a
        // regeneration tick just before isn't lost when the value is put back.
        double Lowered(double v) => kind.IsFloating() ? v * ProbeFactor : Math.Max(1, Math.Floor(v * ProbeFactor));
        var wrote = ReadThenWrite(addr, kind, v => v > 0 && Lowered(v) < v ? Lowered(v) : null, out double orig, out double test);
        if (wrote != WriteResult.Written)
        {
            if (wrote == WriteResult.Failed)
            {
                Diag.Log($"Auto-find: couldn't write to {addr:X}; skipping it.");
                writeFailed = true;
            }
            return null;
        }
        bases[idx] = orig;
        double eps = kind.IsFloating() ? Math.Max(1e-4, orig * 0.002) : 0.5;
        double dead = kind.IsFloating() ? eps : 0;
        // Highest value worth putting back: a heal during the test can't take health past max.
        double maxEst = before > 0.02 ? orig / before : orig;
        double cap = Math.Max(orig, maxEst);
        double gap = orig - test;
        // A change bigger than this between two readings is a jump (the game putting it back, a hit, a heal), not a tick.
        double jump = Math.Max(eps, 0.15 * gap);

        var p = new Probe { Index = idx, Addr = addr, Kind = kind, Orig = orig, Test = test };
        // Another view of the same memory (emulators map it at several addresses) took the write itself: same type, same
        // place in its page, the test value, and the whole page identical. Certainly not the source, whatever happens next.
        for (int j = 0; j < n; j++)
        {
            if (j == idx || !readable[j] || cands[j].Kind != kind || ((cands[j].Addr ^ addr) & 0xFFF) != 0) continue;
            if (_mem.TryReadValue(cands[j].Addr, kind, out double vj) && vj == test && SamePage(addr, cands[j].Addr))
            {
                p.Strong.Add(j);
                p.Aliases.Add(j);
                p.Followers.Add(j);
            }
        }
        double ratio = test / orig, low = before, high = before, now = test, restoredTo = double.NaN;
        double? first = null;
        bool sawMid = false, sawHidden = false, hidden = false, wentDown = false, leftBack = false, backNear = false;
        bool touched = false, selfLost = false;                 // touched: it had changed by itself (beyond a little drift)
        bool sawDead = false, firstPass = true;                  // sawDead: read at 0 at some point (died, or the slot cleared)
        var easeLast = new Dictionary<int, double>();            // followers easing in since the first reading: where they got
        var easeOff = new HashSet<int>();                        // ...and the ones that aren't
        int steps = 0;
        double maxStep = 0, last = test, selfPr = 1;
        long backAtMs = -1;                                      // when it was first back near its old value
        var pts = new List<(long Ms, double V)> { (0, test) };   // the way back, up to the reading that got there
        var hiddenFor = new Stopwatch();
        double selfAtHide = double.NaN;                          // its value when the current hide of the bar began
        // One reading of the bar. A hide counts once it has lasted SustainedHiddenMs (a flash on a hit doesn't), wherever it
        // falls across the watch, the restore and the wait for followers below.
        double BarTick()
        {
            double f = MeasureBar();
            if (double.IsNaN(f) || f < BarHiddenBelow)
            {
                sawHidden = true;
                // (Once a hide has counted, the value at its start is kept: a flicker of the bar after a load must not
                // make the reused slot's contents look like where it was.)
                if (!hiddenFor.IsRunning) { hiddenFor.Restart(); if (!hidden) selfAtHide = last; }
                if (hiddenFor.ElapsedMilliseconds >= SustainedHiddenMs) hidden = _barHidden = true;
            }
            else { hiddenFor.Reset(); if (!hidden) selfAtHide = double.NaN; }
            return f;
        }
        // A hide still going on: wait until it's over (a flash) or has lasted long enough to count. (Bounded by
        // SustainedHiddenMs, so before the restore it runs even after Stop: the restore needs to know.)
        void FinishHide(bool evenIfStopped = false)
        {
            while (hiddenFor.IsRunning && !hidden && (evenIfStopped || !ct.IsCancellationRequested)) { Thread.Sleep(50); BarTick(); }
        }
        try
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ProbeWatchMs && !ct.IsCancellationRequested)
            {
                Thread.Sleep(50);
                long ms = sw.ElapsedMilliseconds;
                if (_mem.TryReadValue(addr, kind, out double self))
                {
                    double pr = Progress(orig, test, self);
                    first ??= pr;
                    selfPr = pr;
                    if (self <= dead) sawDead = true;
                    if (Math.Abs(self - test) > Math.Max(eps, 0.05 * gap)) touched = true;
                    if (pr > 0.25 && pr < 0.75) sawMid = true;
                    double d = self - last;
                    if (Math.Abs(d) > eps)
                    {
                        steps++;
                        maxStep = Math.Max(maxStep, Math.Abs(d));
                    }
                    if (d < -eps) wentDown = true;
                    if (backAtMs < 0) pts.Add((ms, self));
                    // Back means within a quarter of the way to the test value; going higher (regeneration, a heal) still counts.
                    if (pr <= 0.25)
                    {
                        if (backAtMs < 0)
                        {
                            backAtMs = ms;
                            // A refreshed copy comes back to what it copies: the old value, plus what regeneration adds meanwhile.
                            backNear = Math.Abs(self - orig) <= Math.Max(eps, orig * (0.02 + 0.3 * Math.Min(ms, RevertWithinMs) / 1000.0));
                        }
                        // After it was back: a hit, or a rise too big for regeneration (heals are left to the check below).
                        else if (d < -jump || d >= 0.5 * gap) leftBack = true;
                    }
                    else if (backAtMs >= 0) leftBack = true;
                    last = self;
                }
                for (int j = 0; j < n; j++)
                {
                    if (j == idx || !readable[j] || bases[j] == 0 || !_mem.TryReadValue(cands[j].Addr, cands[j].Kind, out double vj)) continue;
                    // A copy moves in proportion, which also covers copies kept as a percentage or a different type.
                    double pj = Progress(bases[j], bases[j] * ratio, vj);
                    if (pj is >= 0.5 and <= 1.5 && !p.Followers.Contains(j)) p.Followers.Add(j);
                    // Caught up with the write while this value still held it: copied from it. One that only got there after
                    // this value changed by itself, or that stopped somewhere else, may have been moved by a hit or a heal.
                    if (!touched && Math.Abs(pj - selfPr) <= 0.1) p.Strong.Add(j);
                    // Already there at the first reading: it followed the write straight away (a copy drawn with an offset,
                    // say, like a bar's right edge, moves in step but not exactly in proportion).
                    if (!touched && firstPass && pj is >= 0.5 and <= 1.5) p.Strong.Add(j);
                    // Or it was already on its way at the first reading and got closer at every one since, while this value
                    // still held the write: an eased HUD copy of it. (A value hit meanwhile sits still until the hit.)
                    if (!touched && !easeOff.Contains(j))
                    {
                        if (!easeLast.TryGetValue(j, out double lp))
                        {
                            if (firstPass && pj > 0.02 && pj < 0.5) easeLast[j] = pj; else easeOff.Add(j);
                        }
                        else if (pj > lp + 0.002)
                        {
                            easeLast[j] = pj;
                            if (pj is >= 0.5 and <= 1.5) p.Strong.Add(j);
                        }
                        else easeOff.Add(j);
                    }
                }
                firstPass = false;
                for (int w = 0; w < watch.Count; w++)
                {
                    if (wBases[w] == 0 || p.Watched.Contains(w)) continue;
                    if (_mem.TryReadValue(watch[w].Addr, watch[w].Kind, out double vw) && Progress(wBases[w], wBases[w] * ratio, vw) is >= 0.5 and <= 1.5)
                        p.Watched.Add(w);
                }
                double f = BarTick();
                if (!double.IsNaN(f)) { low = Math.Min(low, f); high = Math.Max(high, f); }
            }
        }
        finally
        {
            if (_mem.TryReadValue(addr, kind, out double cur)) now = cur;
            if (now > dead && first != null)
            {
                bool back = backAtMs >= 0 && !leftBack;
                bool snapped = back && backNear && backAtMs <= RevertWithinMs;
                // An eased copy (HUD numbers) slows down on the way back: the last third of the way is much slower than the
                // first. Measured over spans of readings, so the uneven timing of steady regeneration doesn't look like that.
                // Back within 4 readings is too fast for regeneration anyway, and too few readings for spans.
                int m = pts.Count - 1, k = Math.Max(2, m / 3);
                bool slowed = m >= 5 ? SpanRate(pts, m - k, m) <= 0.55 * SpanRate(pts, 0, k)
                            : m >= 2 && SpanRate(pts, m - 1, m) <= 0.7 * SpanRate(pts, 0, 1);
                bool eased = back && !wentDown && slowed;
                p.Reverted = snapped || eased;
                // Came back, but not to where it was, while the bar went up: a heal on this very value can look like that.
                if (p.Reverted && IsHealth && Math.Abs(now - orig) > Math.Max(eps, 0.02 * orig) && high >= before + 3 * ChangeThreshold)
                    p.Reverted = false;
                bool small = maxStep < 0.5 * gap && !wentDown;   // only upward ticks (regeneration, a small heal)
                p.Stuck = !p.Reverted && small && Progress(orig, test, now) >= 0.75;
                p.Ramped = !p.Reverted && !p.Stuck && small && first >= 0.75 && sawMid && steps >= 2;
                p.Event = !p.Reverted && !p.Stuck && !p.Ramped;
            }
            // A hide that started near the end of the watch decides whether the slot may have been reused (see below).
            FinishHide(evenIfStopped: true);
            if (hidden && _mem.TryReadValue(addr, kind, out double cur2)) now = cur2;
            // Put it back, unless the game already did: it owns copies, and writing an old value over one could undo a real
            // change. Whatever the game itself did meanwhile (regeneration, a hit, a heal) is kept, up to max. Never put
            // anything back into a value that went to 0: that's a character that just died. After the bar was hidden (a
            // loading screen, say), the slot may belong to something else now: only write if it still holds what the test
            // left in it, give or take a normal hit, or if it dropped before the bar went (a big hit can empty the bar,
            // which then reads as hidden).
            if (now > dead && !p.Reverted)
            {
                bool droppedFirst = !sawDead && now < test && Math.Abs(now - selfAtHide) <= jump;
                if (hidden && Math.Abs(now - test) > Math.Max(jump, 1.1 * BiggestHitFrac() * maxEst) && !droppedFirst)
                {
                    p.LeftAtTest = true;
                    Diag.Log($"Auto-find: the bar was hidden while {addr:X} was being tested and it now holds {kind.Format(now)}; leaving it alone.");
                }
                else if (ReadThenWrite(addr, kind, v => v > dead ? Math.Min(orig + (v - test), cap) : null, out _, out double put) == WriteResult.Written)
                {
                    restoredTo = put;
                    // The game changed it while the test value was in: copies it refreshes only on such changes now hold
                    // test-based values the restore doesn't touch, so the next change only re-syncs them.
                    if (steps > 0) _resyncNeeded = true;
                }
            }
        }

        // Values that followed the test must come back with the restore. One that doesn't moved for another reason (a hit
        // meanwhile), and a value hit during the test can look exactly like a copy following it. Animated HUD numbers can
        // take a few seconds to climb back, which is fine as long as they started back at once and keep getting closer.
        // The value itself must keep what was put back: one that soon loses it copies a value that changed meanwhile (an
        // on-change copy of mana just spent, say), and the "followers" were that value and its copies.
        var stray = new List<int>();
        if (now > dead && !p.Event && p.Followers.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            var best = new Dictionary<int, (double Pr, long Ms)>();   // the closest each one got so far, and when
            var onTheWay = new HashSet<int>();                         // started back right after the restore
            bool awayOrStopped;
            do
            {
                Thread.Sleep(50);
                BarTick();
                long ms = sw.ElapsedMilliseconds;
                if (!double.IsNaN(restoredTo) && _mem.TryReadValue(addr, kind, out double sv) && sv < restoredTo - 0.5 * gap)
                {
                    selfLost = true;
                    break;
                }
                stray.Clear();
                awayOrStopped = false;
                foreach (int j in p.Followers)
                {
                    if (!_mem.TryReadValue(cands[j].Addr, cands[j].Kind, out double vj)) { stray.Add(j); awayOrStopped = true; continue; }
                    double pj = Progress(bases[j], bases[j] * ratio, vj);
                    if (pj <= 0.25) continue;
                    stray.Add(j);
                    if (!best.TryGetValue(j, out var b)) best[j] = (pj, ms);
                    else if (pj < b.Pr - 0.005) { best[j] = (pj, ms); if (ms <= 500) onTheWay.Add(j); }
                    else if (pj > b.Pr + 0.05 || ms - b.Ms > 300) awayOrStopped = true;
                }
            } while (stray.Count > 0 && !ct.IsCancellationRequested &&
                     (sw.ElapsedMilliseconds < FollowerReturnMs ||
                      sw.ElapsedMilliseconds < FollowerReturnMaxMs && !awayOrStopped && stray.All(onTheWay.Contains)));
            FinishHide();
            if (stray.Count > 0 || selfLost) p.Event = true;
        }

        p.After = now;
        p.BarMoved = low <= before * (1 - (1 - ProbeFactor) / 2);
        if (now <= dead) p.Died = sawHidden; // dropped to 0: the player died (the bar emptied or vanished), or it isn't health
        string what = now <= dead ? "it dropped to 0"
            : first == null ? "it couldn't be read back"
            : selfLost ? "it didn't keep the value put back (a hit right after, or it copies a value that changed)"
            : stray.Count > 0 ? $"{stray.Count} of the values that followed it didn't come back ({string.Join(", ", stray.Select(j => $"{cands[j].Addr:X}"))})"
            : p.Reverted ? $"the game put it back to {kind.Format(now)}{(backAtMs > RevertWithinMs || !backNear ? " gradually" : "")}"
            : p.Stuck ? $"it kept the new value{(Math.Abs(now - test) > eps ? $" (now {kind.Format(now)})" : "")}"
            : p.Ramped ? $"it kept it, then climbed back to {kind.Format(now)}"
            : $"it changed in a way the test doesn't explain (to {kind.Format(now)})";
        Diag.Log($"Auto-find: wrote {kind.Format(test)} once to {addr:X} ({kind}, was {kind.Format(orig)}): {what}; " +
                 $"{p.Followers.Count} other candidate(s) followed" +
                 (p.Followers.Count > 0 ? " (" + string.Join(", ", p.Followers.Select(j => $"{cands[j].Addr:X}")) + ")" : "") +
                 (p.Followers.Count > p.Strong.Count ? $", {p.Followers.Count - p.Strong.Count} of them not straight away (tested too)" : "") +
                 (p.Watched.Count > 0 ? $", {p.Watched.Count} unwritten one(s) too" : "") +
                 $"; bar {before * 100:0.0}% → lowest {low * 100:0.0}%{(p.BarMoved ? " (moved)" : "")}.");
        return p;
    }

    /// <summary>
    /// Decides between roots with <see cref="ShiftTest"/>, one change of health per test. A single test can be fooled
    /// in one specific way: a heal when the value was moved above the others makes a copy look real, and a heal when
    /// it was moved below makes the real value look like a copy (hits up to 1.1x the biggest seen are never misread).
    /// So a root counts as real only when a test above AND a test below both say real, and as a copy only when both say
    /// copy. Results are kept for the whole run, because health often only has room for one direction at a time.
    /// </summary>
    private Probe SeparateRoots(List<Probe> roots, List<Probe> refs, double[] v0, double frac0, CancellationToken ct, out SeparateEnd end)
    {
        end = SeparateEnd.Undecided;
        _needUp = null;
        bool upNoRoom = false, downNoRoom = false;
        int total = roots.Count * 2;
        foreach (var a in roots)
        {
            foreach (bool up in new[] { true, false })
            {
                var d = Decided(a.Addr);
                if (d == ShiftVerdict.Real)
                {
                    Diag.Log($"Auto-find: {a.Addr:X} carried its test value through both above and below the others, so it's the real value.");
                    return a;
                }
                if (d == ShiftVerdict.Copy || _notStat.Contains(a.Addr)) break;
                _shiftResults.TryGetValue(a.Addr, out var have);
                if ((up ? have.Up : have.Down) != null) continue;
                var others = refs.Where(r => r != a && !_notStat.Contains(r.Addr)).ToList();
                if (others.Count == 0) return null; // nothing left to compare with; Verify's lone-value rule takes it from here
                int nr = _shiftResults.Values.Sum(h => (h.Up != null ? 1 : 0) + (h.Down != null ? 1 : 0)) + 1;
                var verdict = ShiftTest(a, others, v0, frac0, up, nr, Math.Max(total, nr), ct);
                // v6: a test that ruled the other values out by the bar (they sat still while it moved) leaves one value that
                // both moved the bar when written and moved with it afterwards: that's the stat.
                var left = roots.Where(r => !_notStat.Contains(r.Addr) && Decided(r.Addr) != ShiftVerdict.Copy).ToList();
                if (left.Count == 1 && left[0].BarMoved && roots.Count > 1)
                {
                    Diag.Log($"Auto-find: {left[0].Addr:X} is the only value left that moved the bar when written and moved with the bar afterwards; the others sat still while the bar changed.");
                    return left[0];
                }
                if (verdict == ShiftVerdict.NoRoom)
                {
                    if (up) upNoRoom = true; else downNoRoom = true;
                    continue; // maybe the other direction fits now
                }
                if (verdict is ShiftVerdict.Unclear or ShiftVerdict.NoEvent) return null;
                _shiftResults[a.Addr] = up ? (verdict, have.Down) : (have.Up, verdict);
                if (have.Up != null && have.Up != verdict || have.Down != null && have.Down != verdict)
                {
                    Diag.Log($"Auto-find: the two tests of {a.Addr:X} disagree (a heal or an unusual hit during one of them); testing it again later.");
                    _shiftResults.Remove(a.Addr);
                    return null;
                }
                if (verdict == ShiftVerdict.Copy)
                {
                    // The shift has been taken back out of a (a big hit or a heal can make the real value look like a copy). If a
                    // is a copy, the game had already overwritten it, so it is now off by the shift until the game's next change,
                    // and no further test may use it as a reference before then.
                    if (Decided(a.Addr) == ShiftVerdict.Copy) Diag.Log($"Auto-find: {a.Addr:X} lost its test value both times, so it's a copy.");
                    end = SeparateEnd.Pending;
                    return null;
                }
            }
            var dd = Decided(a.Addr);
            if (dd == ShiftVerdict.Real)
            {
                Diag.Log($"Auto-find: {a.Addr:X} carried its test value through both above and below the others, so it's the real value.");
                return a;
            }
            if (dd != ShiftVerdict.Copy)
            {
                // Needs more changes of health: one of the tests had no room yet.
                if (upNoRoom || downNoRoom)
                {
                    end = SeparateEnd.NoRoom;
                    _needUp = upNoRoom && downNoRoom ? null : downNoRoom; // no room below: health has to go up first
                }
                return null;
            }
            Diag.Log($"Auto-find: {a.Addr:X} lost its test value both times, so it's a copy.");
        }
        Diag.Log("Auto-find: every value turned out to be a copy of something auto-find isn't tracking.");
        end = SeparateEnd.AllCopies;
        return null;
    }

    private ShiftVerdict? Decided(long addr) =>
        _shiftResults.TryGetValue(addr, out var h) && h.Up != null && h.Up == h.Down ? h.Up : null;

    /// <summary>
    /// Moves root <paramref name="a"/> away from the other roots, waits for the next change of health, and checks
    /// where the change started from. Afterwards the game has made the roots equal again. If a is the real value, the
    /// change started from a's new value; if it's a copy, from the others' value. Which start explains it depends on
    /// how big the change was, so the gap is made much bigger than any hit seen while narrowing: then only one of the
    /// two explanations is a plausible hit (or a small regeneration tick). A heal can still fit the wrong explanation
    /// in one direction (see <see cref="SeparateRoots"/>), which is why both directions are needed. The shift is taken
    /// back out of a afterwards whatever the verdict, except when health hit 0 or the slot may have been reused while the
    /// bar was hidden; the others are never written.
    /// </summary>
    private ShiftVerdict ShiftTest(Probe a, List<Probe> others, double[] v0, double frac0, bool up, int nr, int count, CancellationToken ct)
    {
        string stat = _stat.ToLowerInvariant();
        double maxEst = frac0 > 0.02 && v0[a.Index] > 0 ? v0[a.Index] / frac0 : 0;
        if (maxEst <= 0 || !_mem.TryReadValue(a.Addr, a.Kind, out double b) || !(b > 0)) return ShiftVerdict.Unclear;

        // Others in a's units, using how they related when everything was in step (some may be a percentage).
        var scale = others.Select(o => v0[o.Index] != 0 ? v0[a.Index] / v0[o.Index] : double.NaN).ToArray();
        double[] ReadOthers()
        {
            var r = new double[others.Count];
            for (int k = 0; k < others.Count; k++)
                r[k] = !double.IsNaN(scale[k]) && _mem.TryReadValue(others[k].Addr, others[k].Kind, out double v) ? v * scale[k] : double.NaN;
            return r;
        }
        static double Median(double[] xs)
        {
            var ok = xs.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
            return ok.Length == 0 ? double.NaN : ok[ok.Length / 2];
        }

        var oPre = ReadOthers();
        double reference = Median(oPre);   // where the real value is, if a is a copy
        if (double.IsNaN(reference)) return ShiftVerdict.Unclear;

        double hMax = _steps.Where(s => s < 0).Select(s => -s * maxEst).DefaultIfEmpty(0.15 * maxEst).Max();
        // With this gap a hit up to 1.1x the biggest one seen, or a small tick, is plausible from only one of the two starts.
        double gap = Math.Max(1.4 * hMax, 0.15 * reference);
        double tick = 0.25 * hMax, bigHit = 1.1 * hMax;
        bool Plausible(double d) => Math.Abs(d) <= tick || (d < 0 && -d <= bigHit);

        // Above the others: needs room below max. Below: only when a normal hit would still leave health if a is real.
        double tag;
        if (up && b >= reference + gap && b <= 0.95 * maxEst) tag = b;                // already far enough above (earlier test)
        else if (!up && b <= reference - gap && b >= 1.5 * hMax) tag = b;             // already far enough below
        else if (up && reference + gap <= 0.95 * maxEst) tag = reference + gap;
        else if (!up && reference - gap >= 1.5 * hMax) tag = reference - gap;
        else
        {
            Diag.Log($"Auto-find: no safe room to test {a.Addr:X} {(up ? "above" : "below")} the others at {a.Kind.Format(reference)} " +
                     $"(biggest hit so far ≈ {a.Kind.Format(hMax)}, max ≈ {a.Kind.Format(maxEst)}); will try after the next change of {stat}.");
            return ShiftVerdict.NoRoom;
        }
        if (!a.Kind.IsFloating()) tag = Math.Round(tag);
        double shift = tag - b;
        double eps = a.Kind.IsFloating() ? Math.Max(1e-4, maxEst * 0.002) : 0.5;

        Diag.Log($"Auto-find: telling apart {others.Count + 1} values that keep what's written to them, test {nr}: " +
                 $"{a.Addr:X} {a.Kind.Format(b)} → {a.Kind.Format(tag)} ({(up ? "above" : "below")} the others at {a.Kind.Format(reference)}); " +
                 $"biggest hit so far ≈ {a.Kind.Format(hMax)}.");
        string ask = IsHealth ? $"take one hit now (not a heal) within {ShiftWaitSeconds} s" : $"{UseVerb} once now, within {ShiftWaitSeconds} s";
        _status.Report($"Two or more places in memory look like {stat}. Test {nr} of about {count}: setting it up…");

        if (shift != 0)
        {
            // Read and written with the game paused for that instant, so the shift is exactly what's taken back out later.
            if (ReadThenWrite(a.Addr, a.Kind, v => v > 0 ? tag : null, out double bNow, out _) != WriteResult.Written) return ShiftVerdict.Unclear;
            b = bNow;
            shift = tag - b;
        }
        bool undo = shift != 0;
        if (shift != 0) _resyncNeeded = true;
        // v6: where the bar and every value stand once the shift has been drawn; a value that doesn't move at all while the
        // bar then changes by a good margin isn't the stat (see _notStat).
        double barPre = WaitForSteadyBar(ct, "Reading bar…");
        var oStart = (double[])oPre.Clone();
        _status.Report($"Two or more places in memory look like {stat}. Test {nr} of about {count} (one {(IsHealth ? "hit" : "use")} each): {ask}. " +
                       (shift > 0 ? "One of them was raised for this test and is put back afterwards."
                        : shift < 0 ? "One of them was lowered for this test and is put back afterwards." : ""));
        // A change counts when it's bigger than a regeneration tick could be: smaller ones can't be told apart anyway (see
        // Plausible), and a regenerating stat read at uneven moments would otherwise end the wait on its own creep.
        double trig = Math.Max(eps, tick);
        // Whether any other root moved (if not, a change of a alone was the game adjusting a itself, e.g. clamping it to
        // its real max), and whether the bar was hidden meanwhile (menu, loading screen, death).
        // Hidden means the bar stayed away a while (menu, loading screen, death), not a flash of it on a hit.
        bool othersMoved = false, hiddenSeen = false, hiddenNow = false, hiddenAtChange = false;
        var barSw = Stopwatch.StartNew();
        var hiddenFor = new Stopwatch();
        void CheckBar()
        {
            if (barSw.ElapsedMilliseconds < 200) return;
            barSw.Restart();
            double f = MeasureBar();
            if (double.IsNaN(f) || f < BarHiddenBelow) { if (!hiddenFor.IsRunning) hiddenFor.Restart(); }
            else hiddenFor.Reset();
            hiddenNow = hiddenFor.IsRunning && hiddenFor.ElapsedMilliseconds >= SustainedHiddenMs;
            hiddenSeen |= hiddenNow;
            if (hiddenNow) _barHidden = true;
        }
        bool changed = false;
        double settledAt = double.NaN; // a's value when the change was judged
        double aPre = tag;             // a's value just before the change
        try
        {
            // 1. Wait for the change.
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < ShiftWaitSeconds)
            {
                ct.ThrowIfCancellationRequested();
                if (!_mem.IsAlive) throw new InvalidOperationException("The game closed while auto-find was running.");
                Thread.Sleep(50);
                CheckBar();
                if (!_mem.TryReadValue(a.Addr, a.Kind, out double av)) return ShiftVerdict.Unclear;
                var oNow = ReadOthers();
                bool any = Math.Abs(av - aPre) > trig;
                for (int k = 0; k < oNow.Length; k++)
                    if (!double.IsNaN(oNow[k]) && !double.IsNaN(oPre[k]) && Math.Abs(oNow[k] - oPre[k]) > trig) any = othersMoved = true;
                if (any) { changed = true; hiddenAtChange = hiddenNow; break; }
                // Follow slow drift (regeneration below the threshold) so it isn't mistaken for the change.
                aPre = av;
                oPre = oNow;
            }
            if (!changed)
            {
                Diag.Log($"Auto-find: no change of {stat} within {ShiftWaitSeconds} s; taking the test value back out.");
                return ShiftVerdict.NoEvent;
            }
            reference = Median(oPre);

            // 2. Let the game finish: copies can lag (animated bars, delayed updates).
            double xa = aPre;
            var xo = oPre;
            bool settled = false;
            var quiet = Stopwatch.StartNew();
            var limit = Stopwatch.StartNew();
            while (limit.ElapsedMilliseconds < 4000)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(50);
                CheckBar();
                if (!_mem.TryReadValue(a.Addr, a.Kind, out double av)) return ShiftVerdict.Unclear;
                var oNow = ReadOthers();
                bool moving = Math.Abs(av - xa) > eps;
                for (int k = 0; k < oNow.Length; k++)
                    if (!double.IsNaN(oNow[k]) && !double.IsNaN(xo[k]) && Math.Abs(oNow[k] - xo[k]) > eps) moving = othersMoved = true;
                xa = av; xo = oNow;
                hiddenAtChange |= hiddenNow;
                if (moving) quiet.Restart();
                else if (quiet.ElapsedMilliseconds >= 600) { settled = true; break; }
            }

            // 3. Decide. (The bar not showing now counts too: the flash of a hit is long over by the time values settle.)
            settledAt = xa;
            hiddenAtChange |= hiddenFor.IsRunning;
            if (!hiddenAtChange && !hiddenSeen && settled)
            {
                // v6: the bar is the ground truth for "the stat changed". A value that didn't move at all while the bar moved
                // by a good margin can't be the stat, whatever it kept during the write test.
                double barPost = WaitForSteadyBar(ct, "Reading bar…");
                if (barPre >= BarHiddenBelow && barPost >= BarHiddenBelow && Math.Abs(barPost - barPre) >= BarChangeMin)
                {
                    for (int k = 0; k < others.Count; k++)
                        if (!double.IsNaN(xo[k]) && !double.IsNaN(oStart[k]) && Math.Abs(xo[k] - oStart[k]) <= 2 * eps && _notStat.Add(others[k].Addr))
                            Diag.Log($"Auto-find: {others[k].Addr:X} stayed at {a.Kind.Format(oStart[k])} while the bar went {barPre * 100:0}% → {barPost * 100:0}%; it isn't {stat}.");
                    if (Math.Abs(xa - tag) <= 2 * eps && _notStat.Add(a.Addr))
                        Diag.Log($"Auto-find: {a.Addr:X} stayed at {a.Kind.Format(tag)} while the bar went {barPre * 100:0}% → {barPost * 100:0}%; it isn't {stat}.");
                }
            }
            if (xa <= eps)
            {
                // Hit 0: never put health back into a character that just died.
                undo = false;
                Diag.Log($"Auto-find: {stat} went to 0 during the test; can't tell anything from that.");
                return ShiftVerdict.Unclear;
            }
            double tolerance = Math.Max(eps * 2, maxEst * 0.01);
            bool together = xo.All(x => double.IsNaN(x) || Math.Abs(x - xa) <= tolerance);
            double dReal = xa - aPre, dCopy = xa - reference;
            bool pr = Plausible(dReal), pc = Plausible(dCopy);
            if (hiddenAtChange)
            {
                if (!pr && !pc)
                {
                    // The bar went away and a ended up somewhere no hit explains: a death, a reload or a loading screen may have
                    // handed the slot to something else. Don't write to it again.
                    undo = false;
                    _leftAtTest = a.Addr;
                    Diag.Log($"Auto-find: the bar was hidden while {stat} changed ({a.Addr:X} {a.Kind.Format(aPre)} → {a.Kind.Format(xa)}); " +
                             "can't tell anything from that, and the value is left alone.");
                    return ShiftVerdict.Unclear;
                }
                // The change itself looks normal, but the bar went away around it (a menu opened right after a hit, say).
                // Nothing is concluded; the shift comes back out once the bar is back, if a hasn't been changed oddly since.
                // (Stop works meanwhile; the cleanup below then decides from the value alone.)
                _status.Report($"Test {nr} of about {count}: waiting for the {stat} bar to come back…");
                while (hiddenFor.IsRunning)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!_mem.IsAlive) throw new InvalidOperationException("The game closed while auto-find was running.");
                    Thread.Sleep(200);
                    CheckBar();
                }
                if (!_mem.TryReadValue(a.Addr, a.Kind, out double later) || later <= eps || !Plausible(later - xa))
                {
                    undo = false;
                    _leftAtTest = a.Addr;
                    Diag.Log($"Auto-find: the bar was hidden during the test of {a.Addr:X} and the value changed oddly since; " +
                             "nothing concluded, and the value is left alone.");
                }
                else Diag.Log($"Auto-find: the bar was hidden around the change during the test of {a.Addr:X}; nothing concluded.");
                return ShiftVerdict.Unclear;
            }
            if (hiddenSeen && !pr && !pc && !(othersMoved && together))
            {
                // The bar was away for a while earlier in the test, and now a holds something no hit explains, without the
                // others moving with it: the slot may belong to something else now. Leave it alone.
                undo = false;
                _leftAtTest = a.Addr;
                Diag.Log($"Auto-find: the bar was hidden for a while during the test of {a.Addr:X}, and the value changed oddly; " +
                         "nothing concluded, and the value is left alone.");
                return ShiftVerdict.Unclear;
            }
            if (xa >= 0.985 * maxEst)
            {
                // (Checked after the hidden-bar cases above: a slot reused during a load can hold anything, max included.)
                // Hit max: the game may have clamped it, so nothing can be learned. The shift still comes back out: if a is
                // real, that leaves it where it would be without the test (or, if the game clamped it, at the least it
                // could be). Except after the bar was away (death, reload) with a raised: everything starting over at full is
                // the game's own doing, and taking the shift out would leave it short.
                if (hiddenSeen && shift > 0) undo = false;
                Diag.Log($"Auto-find: {stat} went to about max during the test; can't tell anything from that.");
                return ShiftVerdict.Unclear;
            }
            var verdict = !settled || !together ? ShiftVerdict.Unclear
                        : pr && !pc ? ShiftVerdict.Real
                        : pc && !pr ? ShiftVerdict.Copy
                        : ShiftVerdict.Unclear;
            Diag.Log($"Auto-find: {stat} changed; {a.Addr:X} {a.Kind.Format(aPre)} → {a.Kind.Format(xa)}, others " +
                     string.Join("/", xo.Select(x => double.IsNaN(x) ? "?" : a.Kind.Format(x))) + ". " +
                     $"If it's real the change was {a.Kind.Format(dReal)} ({(pr ? "plausible" : "implausible")}), if it's a copy {a.Kind.Format(dCopy)} " +
                     $"({(pc ? "plausible" : "implausible")}) → " +
                     (verdict == ShiftVerdict.Real ? "real."
                      : verdict == ShiftVerdict.Copy ? "copy."
                      : !settled ? "unclear (values were still moving)."
                      : !together ? "unclear (the values didn't end up equal; this change may not update all of them)."
                      : "unclear (a heal, or a hit unlike the earlier ones)."));
            return verdict;
        }
        finally
        {
            // Take the shift back out of a, whatever the verdict: a verdict can be wrong (a hit bigger than any seen, a heal),
            // and if a is real this restores the player's health, with whatever the game did meanwhile (a hit, regeneration)
            // kept; if a is a copy the game refreshes it at the next change anyway. The others are never written. (v5.1 put a
            // back to where it was when it alone had changed, taking that for the game clamping it; a regenerating real value
            // changes alone all the time, and that lost the regeneration, so the shift is simply taken out now; a clamped copy
            // doesn't mind.) If the bar was hidden meanwhile and a holds something no hit explains (stopped while waiting for
            // the bar, say), the slot may hold something else now and is left alone.
            if (undo)
                ReadThenWrite(a.Addr, a.Kind, cur =>
                {
                    if (hiddenSeen && !(!double.IsNaN(settledAt) ? Plausible(cur - settledAt) : Plausible(cur - aPre) || Plausible(cur - reference)))
                        return null;
                    double back = Math.Min(cur - shift, Math.Max(maxEst, b));
                    return back > 0 ? back : null;
                }, out _, out _);
        }
    }

    private enum WriteResult { Written, Skipped, Unreadable, Failed }

    /// <summary>
    /// Reads a value and writes what <paramref name="next"/> makes of it (null: write nothing), with the game paused for that
    /// instant so a regeneration tick or a hit can't land between the read and the write and be lost.
    /// </summary>
    private WriteResult ReadThenWrite(long addr, ValueKind kind, Func<double, double?> next, out double read, out double written)
    {
        written = double.NaN;
        bool paused = _mem.Suspend();
        try
        {
            if (!_mem.TryReadValue(addr, kind, out read) || !double.IsFinite(read)) return WriteResult.Unreadable;
            var v = next(read);
            if (v == null) return WriteResult.Skipped;
            written = v.Value;
            return _mem.WriteValue(addr, kind, v.Value) ? WriteResult.Written : WriteResult.Failed;
        }
        finally
        {
            if (paused) _mem.Resume();
        }
    }

    private TableEntry MakeEntry(long addr, ValueKind kind, double frac)
    {
        _mem.TryReadValue(addr, kind, out double v);
        double max = frac > 0.05 ? v / frac : v;
        max = kind.IsFloating() ? Math.Round(max, 1) : Math.Round(max);
        var e = new TableEntry
        {
            Name = _stat, Kind = kind, RawAddress = addr, MaxValue = Math.Max(max, v),
            Mode = FreezeMode.LockAtMax, FreezeValue = Math.Max(max, v)
        };
        // Games almost always keep the max right next to the current value. Finding it means refills stay
        // correct after level-ups. Only attempted when the bar isn't full, so current ≠ max.
        if (frac < 0.97 && v > 0)
        {
            var off = FindMaxNearby(addr, kind, v, e.MaxValue);
            if (off.HasValue && _mem.TryReadValue(addr + off.Value, kind, out double real))
            {
                e.MaxOffset = off;
                e.MaxValue = real;
            }
        }
        return e;
    }

    /// <summary>Looks ±0x400 bytes around the value for a field equal to the estimated max (within 3%).</summary>
    private long? FindMaxNearby(long addr, ValueKind kind, double cur, double estMax)
    {
        const int range = 0x400;
        int size = kind.Size(), align = kind.Alignment();
        var buf = new byte[range * 2 + size];
        long start = addr - range;
        int got = _mem.Read(start, buf, 0, buf.Length);
        long? best = null;
        for (int off = 0; off + size <= got; off += align)
        {
            long rel = start + off - addr;
            if (rel == 0) continue;
            double m = kind.Decode(buf.AsSpan(off, size));
            if (double.IsNaN(m) || m <= cur || m > estMax * 1.03 || m < estMax * 0.97) continue;
            if (best == null || Math.Abs(rel) < Math.Abs(best.Value)) best = rel;
        }
        return best;
    }

    // ------------------------------------------------------------------ pointer paths

    private AutoFindResult FinishWithPointers(TableEntry entry, CancellationToken ct)
    {
        var res = new AutoFindResult { Entry = entry };
        string found = $"Found {_stat.ToLowerInvariant()} ({entry.Kind}, max ≈ {entry.Kind.Format(entry.MaxValue)})";
        var (kept, fromEarlier) = AttachPointerPath(_mem, entry, _stat, PointerDepth, PointerMaxOffset, _status, ct);
        res.PathsKept = kept;
        res.PathConfirmedAcrossSessions = fromEarlier;
        res.Message = fromEarlier
            ? $"{found}. {kept:N0} saved pointer path(s) still work after the restart, so the profile is now permanent."
            : kept == 0
            ? $"{found}, but no pointer path. It works until the game restarts. Try Auto-find again with more pointer levels."
            : $"{found} and {kept:N0} candidate pointer path(s). Next time you start the game, run Auto-find once more: it will keep only the paths that survived and lock the profile in.";
        return res;
    }

    /// <summary>
    /// Gives a found value a pointer path so it survives a restart: paths saved for this stat in an earlier session that
    /// still reach the address are kept (proven across a restart), otherwise a pointer scan runs and its results are saved
    /// for the next session to check. Returns how many paths were kept and whether they came from an earlier session.
    /// v6: shared with the number and position finders.
    /// </summary>
    public static (int Kept, bool FromEarlier) AttachPointerPath(ProcessMemory mem, TableEntry entry, string stat, int depth, long maxOffset,
                                                              IProgress<string> status, CancellationToken ct)
    {
        string file = ChainsFileFor(mem.ProcessName, stat);
        List<PointerChain> saved = null;
        if (File.Exists(file))
        {
            try { saved = JsonSerializer.Deserialize<List<PointerChain>>(File.ReadAllText(file)); } catch { }
        }
        if (saved is { Count: > 0 })
        {
            var kept = PointerScanner.Validate(mem, saved, entry.RawAddress);
            if (kept.Count > 0)
            {
                File.WriteAllText(file, JsonSerializer.Serialize(kept));
                entry.Chain = kept[0];
                return (kept.Count, true);
            }
        }
        status?.Report($"{stat} found. Searching for pointer paths so it works next launch…");
        var ps = new PointerScanner { MaxDepth = depth, MaxOffset = maxOffset };
        var chains = ps.Scan(mem, entry.RawAddress, status, ct);
        File.WriteAllText(file, JsonSerializer.Serialize(chains));
        if (chains.Count > 0) entry.Chain = chains[0];
        return (chains.Count, false);
    }
}
