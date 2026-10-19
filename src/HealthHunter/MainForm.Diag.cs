using System.Drawing;
using System.Security.Principal;

namespace HealthHunter;

/// <summary>
/// v5: Diagnostics page (live log + self-check) and Settings page. The self-check uses the same
/// read-only ReadProcessMemory / VirtualQueryEx calls as the scanner and writes nothing to the game.
/// </summary>
public sealed partial class MainForm
{
    private NavPage _diagPage, _settingsPage;
    private readonly TextBox _diagBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = Theme.Mono };
    private readonly ModernButton _selfCheckBtn = new() { Text = "Run self-check", AutoSize = true, Variant = ButtonVariant.Primary };
    private readonly ModernButton _copyDiagBtn = new() { Text = "Copy log", AutoSize = true };
    private readonly ModernButton _openLogsBtn = new() { Text = "Open log folder", AutoSize = true };
    private readonly ModernButton _clearDiagBtn = new() { Text = "Clear", AutoSize = true, Variant = ButtonVariant.Ghost };

    private static bool IsElevated()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private void BuildDiagTab()
    {
        var page = _diagPage = Page("Diagnostics", '\uE9D9', "!", "Diagnostics",
            "Every scan, bar reading and auto-find decision is logged here. If something isn't found, copy this and paste it back.");
        var card = new Card();
        var help = Ui.Para("If health isn't found: attach, go into gameplay with the bar visible, click Run self-check, then try auto-find or a manual " +
                           "scan again. Afterwards click Copy log and paste it into the chat. Logs are also saved in %AppData%\\HealthHunter\\logs.");
        Ui.Stack(card, new Control[] { Ui.Row(_selfCheckBtn, _copyDiagBtn, _openLogsBtn, _clearDiagBtn), help, Ui.Gap(4) }, _diagBox);
        Ui.Stack(page.Body, null, card);
        _tabs.Add(page);

        _diagBox.Text = Diag.All();
        Diag.Logged += line =>
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (_diagBox.TextLength > 500_000) _diagBox.Text = Diag.All();
                    else _diagBox.AppendText(line + Environment.NewLine);
                }));
            }
            catch { }
        };
        _selfCheckBtn.Click += async (_, _) => await RunSelfCheckAsync();
        _copyDiagBtn.Click += (_, _) =>
        {
            var text = Diag.All();
            if (text.Length == 0) return;
            try { Clipboard.SetText(text); _status.Text = "Diagnostics copied. Paste them into the chat."; } catch { }
        };
        _openLogsBtn.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Diag.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Diag.Folder) { UseShellExecute = true });
            }
            catch { }
        };
        _clearDiagBtn.Click += (_, _) => { Diag.Clear(); _diagBox.Clear(); };
    }

    private void BuildSettingsTab()
    {
        var page = _settingsPage = Page("Settings", '\uE713', "*", "Settings", null);
        var general = new Card { Title = "General", FitContent = true };
        foreach (var c in new Control[] { _autoAttachChk, _soundChk, _overlayChk, _showAll }) c.Margin = new Padding(0, 4, 0, 4);
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        col.Controls.AddRange(new Control[] { _autoAttachChk, _soundChk, _overlayChk, _showAll });
        Ui.Stack(general, new Control[] { col });

        var about = new Card { Title = "About", FitContent = true };
        Ui.Stack(about, new Control[]
        {
            Ui.Para($"HealthHunter {Application.ProductVersion}. For single-player and offline games only: it refuses to attach when it sees " +
                    "anti-cheat (EasyAntiCheat, BattlEye, Vanguard and others) and only reads and writes the game's memory, without injecting code.\r\n\r\n" +
                    "Profiles: %AppData%\\HealthHunter\\profiles   •   Logs: %AppData%\\HealthHunter\\logs")
        });
        Ui.Stack(page.Body, new Control[] { general, Ui.Gap(), about });
        _tabs.Add(page);

        _autoAttachChk.Checked = _settings.AutoAttach;
        _soundChk.Checked = _settings.Sounds;
        _overlayChk.Checked = _settings.Overlay;
        _autoAttachChk.CheckedChanged += (_, _) => { _settings.AutoAttach = _autoAttachChk.Checked; _settings.Save(); };
        _soundChk.CheckedChanged += (_, _) => { _settings.Sounds = _soundChk.Checked; _settings.Save(); };
        _overlayChk.CheckedChanged += (_, _) => { _settings.Overlay = _overlayChk.Checked; _settings.Save(); };
    }

    /// <summary>
    /// Writes everything a remote helper needs to know to the log: app and process facts, how much game memory
    /// exists and how much of it can actually be read, what the bar reader sees, and what the table entries read.
    /// </summary>
    private async Task RunSelfCheckAsync()
    {
        _selfCheckBtn.Enabled = false;
        try
        {
            Diag.Log("──── Self-check ────");
            Diag.Log($"App: {Application.ProductVersion}, {(Environment.Is64BitProcess ? "64" : "32")}-bit, {(IsElevated() ? "administrator" : "standard user")}, " +
                     $"Windows {Environment.OSVersion.Version}, {Environment.ProcessorCount} CPUs, temp folder {Path.GetTempPath()}.");
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
                Diag.Log($"Free space for memory snapshots: {Diag.Mb(drive.AvailableFreeSpace)}.");
            }
            catch { }
            var screen = Screen.PrimaryScreen;
            Diag.Log($"Screens: {Screen.AllScreens.Length}, primary {screen?.Bounds.Width}x{screen?.Bounds.Height}, virtual {SystemInformation.VirtualScreen}, window DPI {DeviceDpi}.");

            var mem = _mem;
            if (mem == null) Diag.Log("Not attached to a game.");
            else
            {
                Diag.Log($"Game: {mem.ProcessName} (pid {mem.Pid}), {(mem.Is64Bit ? "64" : "32")}-bit, {mem.Modules.Count} modules, alive {mem.IsAlive}, " +
                         $"exe {mem.ExeFingerprint() ?? "?"}.");
                await Task.Run(() => MemoryCheck(mem));
                foreach (var e in _entries)
                {
                    var a = e.Resolve(mem);
                    string val = a != null && mem.TryReadValue(a.Value, e.Kind, out var v) ? e.Kind.Format(v) : "unreadable";
                    Diag.Log($"Table entry '{e.Name}' ({e.Kind}): {e.Describe()} → {(a == null ? "does not resolve" : $"{a.Value:X} = {val}")}.");
                }
            }

            if (!_bar.IsCalibrated) Diag.Log($"Bar ({CurrentStat}): not selected yet.");
            else
            {
                var c = Color.FromArgb(_bar.Cal.FillArgb);
                double f = _bar.Measure(out var bmp);
                bmp?.Dispose();
                Diag.Log($"Bar ({CurrentStat}): box {_bar.Cal.X},{_bar.Cal.Y} {_bar.Cal.Width}x{_bar.Cal.Height}, color R{c.R} G{c.G} B{c.B}, " +
                         $"tolerance {_bar.Cal.Tolerance}, anchor {(_bar.Cal.Anchor switch { 1 => "left/top", 2 => "right/bottom", _ => "not learned yet" })}. " +
                         $"Reads {f * 100:0.0}% now, {_bar.LastMatchRatio * 100:0}% of pixels match the fill color" +
                         (_bar.LastCaptureBlack ? ". WARNING: the capture is black (exclusive fullscreen or HDR)." : "."));
            }

            if (_scanner?.HasScan == true)
                Diag.Log($"Manual scan: {_scanner.ResultCount:N0} results after {_scanner.ScanCount} scan(s), types {ValueKindExt.KindsLabel(_scanner.Kinds)}" +
                         (_scanner.InSnapshotMode ? " (snapshot)." : $" ({_scanner.CountByKindText()})."));
            Diag.Log("──── End of self-check ────");
            _tabs.SelectedTab = _diagPage;
        }
        catch (Exception ex) { Diag.Log("Self-check error: " + ex.Message); }
        finally { _selfCheckBtn.Enabled = true; }
    }

    private static void MemoryCheck(ProcessMemory mem)
    {
        var all = mem.GetRegions(writableOnly: false, includeMapped: true);
        long Sum(IEnumerable<MemRegion> rs) => rs.Sum(r => r.Size);
        Diag.Log($"Readable memory: {all.Count:N0} regions, {Diag.Mb(Sum(all))} " +
                 $"(private {Diag.Mb(Sum(all.Where(r => r.Type == Native.MEM_PRIVATE)))}, " +
                 $"image {Diag.Mb(Sum(all.Where(r => r.Type == Native.MEM_IMAGE)))}, " +
                 $"mapped {Diag.Mb(Sum(all.Where(r => r.Type == Native.MEM_MAPPED)))}).");

        var scan = mem.GetRegions(writableOnly: true, includeMapped: false);
        var mapped = mem.GetRegions(writableOnly: true, includeMapped: true).Where(r => r.Type == Native.MEM_MAPPED).ToList();
        Diag.Log($"Scanned by default (writable, not mapped): {scan.Count:N0} regions, {Diag.Mb(Sum(scan))}. " +
                 $"Writable mapped memory (only with 'Include mapped memory'): {Diag.Mb(Sum(mapped))}.");

        // Read everything the scanner would read, page-safely, and report what failed.
        var buf = new byte[1 << 20];
        var ok = new bool[(1 << 20) / ProcessMemory.PageSize + 4];
        long bad = 0, total = 0, firstFail = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var r in scan)
        {
            for (long a = r.Base; a < r.End; a += buf.Length)
            {
                int len = (int)Math.Min(buf.Length, r.End - a);
                int b = mem.ReadPaged(a, buf, len, ok);
                if (b > 0 && firstFail == 0) firstFail = a;
                bad += b; total += len;
            }
        }
        Diag.Log($"Read test: {Diag.Mb(total)} read in {sw.Elapsed.TotalSeconds:0.0} s, {Diag.Mb(bad)} unreadable" +
                 (bad > 0 ? $" (first at {firstFail:X}). v5 skips those pages instead of losing whole blocks." : "."));
    }
}
