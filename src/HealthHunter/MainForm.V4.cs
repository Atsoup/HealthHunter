using System.Diagnostics;
using System.Globalization;

namespace HealthHunter;

/// <summary>
/// v4 additions: per-entry hotkeys (keyboard + controller), toggle feedback, auto-attach, groups and
/// save/restore slots, value graph, memory view and scan options. All of it uses the same plain
/// ReadProcessMemory / WriteProcessMemory access as the rest of the app.
/// </summary>
public sealed partial class MainForm
{
    private readonly AppSettings _settings = AppSettings.Load();
    private Overlay _overlay;
    private readonly CheckBox _autoAttachChk = new() { Text = "Auto-attach to games that have a saved profile", AutoSize = true };
    private readonly CheckBox _soundChk = new() { Text = "Beep when a hotkey fires", AutoSize = true };
    private readonly CheckBox _overlayChk = new() { Text = "Show on-screen messages when a hotkey fires", AutoSize = true };
    private int _noAutoAttachPid;
    private int _tick;
    private string _exeFingerprint;
    private bool _gameUpdated;
    private readonly Dictionary<string, Dictionary<string, double>> _slots = new(StringComparer.OrdinalIgnoreCase);

    // table extras
    private readonly ModernButton _hotkeyBtn = new() { Text = "Hotkeys…", AutoSize = true };
    private readonly ValueGraph _graph = new() { Height = 140 };
    private TableEntry _graphEntry;

    // pointer options
    private readonly CheckBox _mainOnlyChk = new() { Text = "Base in game exe only", AutoSize = true };
    private readonly TextBox _lastOffBox = new() { Width = 56, Margin = new Padding(0, 5, 14, 0) };

    // per-entry hotkeys
    private const int HkEntryBase = 100;
    private readonly Dictionary<int, (TableEntry Entry, EntryHotkey Key)> _entryHotkeys = new();
    private readonly System.Windows.Forms.Timer _padTimer = new() { Interval = 40 };
    private ushort _padPrev;
    private bool _padTimerHooked;

    // ================================================================= scan options / undo

    private bool ApplyScanOptions(ScanType type)
    {
        if (_scanner == null) return false;
        _scanner.Aligned = _fastScanChk.Checked;
        _scanner.KeepUndo = true;
        _scanner.IncludeMappedMemory = _mappedChk.Checked;
        long from = 0, to = 0;
        if (_rangeFrom.Text.Trim().Length > 0)
        {
            var a = _mem.ParseAddress(_rangeFrom.Text);
            if (a == null) { MessageBox.Show("Range start must be hex (1A2B00) or module+offset (game.exe+1000)."); return false; }
            from = a.Value;
        }
        if (_rangeTo.Text.Trim().Length > 0)
        {
            var a = _mem.ParseAddress(_rangeTo.Text);
            if (a == null) { MessageBox.Show("Range end must be hex (1A2B00) or module+offset (game.exe+1000)."); return false; }
            to = a.Value;
        }
        if (to != 0 && to <= from) { MessageBox.Show("Range end must be above range start."); return false; }
        _scanner.RangeStart = from;
        _scanner.RangeEnd = to;
        if (type.NeedsSecondValue())
        {
            var v2 = ParseNumber(_value2Box.Text);
            if (double.IsNaN(v2)) { MessageBox.Show("Type both ends of the range."); return false; }
            _scanner.Value2 = v2;
        }
        return true;
    }

    /// <summary>Auto-find manages its own scans; make sure manual-tab options don't slow it down.</summary>
    private void ResetScanOptionsForAuto()
    {
        if (_scanner == null) return;
        _scanner.Aligned = true;
        _scanner.KeepUndo = false;
        _scanner.IncludeMappedMemory = false;
        _scanner.RangeStart = _scanner.RangeEnd = 0;
    }

    private void UndoScan()
    {
        if (_busy || _scanner == null) return;
        if (_scanner.Undo()) _countLbl.Text = $"Undone. {_scanner.UndoDepth} more undo step(s) available.";
        RefreshResultsList();
        UpdateGuide();
        UpdateButtons();
    }

    // ================================================================= profile helpers

    private GameProfile CurrentProfile() => new()
    {
        ProcessName = _mem.ProcessName,
        Entries = _entries,
        Bars = _bars,
        Slots = _slots,
        ExeFingerprint = _exeFingerprint,
        AutoBars = _autoBars
    };

    private static string HotkeySummary(TableEntry e) =>
        e.Hotkeys == null || e.Hotkeys.Count == 0 ? "" : string.Join(";  ", e.Hotkeys.Select(h => $"{h.KeyText()}: {h.ActionText(e.Kind)}"));

    // ================================================================= feedback

    /// <summary>Beep and/or corner message when a hotkey fires, so you know it worked without alt-tabbing.</summary>
    private void Notify(string message, bool on)
    {
        if (_soundChk.Checked)
            Task.Run(() => { try { Console.Beep(on ? 1100 : 600, 90); } catch { } });
        if (_overlayChk.Checked)
        {
            try { (_overlay ??= new Overlay()).ShowMessage(message, on); } catch { }
        }
        _status.Text = message;
    }

    private static string ToggleLabel(TableEntry e) => e.Mode switch
    {
        FreezeMode.LockAtMax => "Infinite " + e.Name.ToLowerInvariant(),
        FreezeMode.Floor => $"{e.Name} ≥ {e.FloorPercent * 100:0}%",
        FreezeMode.MultiplyGains => $"{e.Name} gains ×{e.Multiplier:0.##}",
        _ => "Freeze " + e.Name
    };

    // ================================================================= per-entry hotkeys

    private void RegisterEntryHotkeys()
    {
        UnregisterEntryHotkeys();
        int id = HkEntryBase;
        var failed = new List<string>();
        bool anyPad = false;
        foreach (var e in _entries)
        {
            if (e.Hotkeys == null) continue;
            foreach (var h in e.Hotkeys)
            {
                _entryHotkeys[id] = (e, h);
                if (h.Key != 0 && !Native.RegisterHotKey(Handle, id, h.Modifiers | Native.MOD_NOREPEAT, (uint)h.Key))
                    failed.Add(h.KeyText());
                if (h.PadButtons != 0) anyPad = true;
                id++;
            }
        }
        if (failed.Count > 0)
            _status.Text = "Hotkey already used by another program: " + string.Join(", ", failed.Distinct());
        if (!_padTimerHooked) { _padTimer.Tick += (_, _) => PadTick(); _padTimerHooked = true; }
        _padPrev = 0;
        if (anyPad) _padTimer.Start(); else _padTimer.Stop();
    }

    private void UnregisterEntryHotkeys()
    {
        foreach (var id in _entryHotkeys.Keys) Native.UnregisterHotKey(Handle, id);
        _entryHotkeys.Clear();
        _padTimer.Stop();
    }

    private void OnEntryHotkey(int id)
    {
        if (_entryHotkeys.TryGetValue(id, out var b)) RunHotkey(b.Entry, b.Key);
    }

    private void PadTick()
    {
        if (_mem == null) return;
        ushort cur = Gamepad.Buttons();
        if (cur == _padPrev) return;
        // A combo fires when its last button goes down. If LB+A and A are both bound, LB+A wins.
        var firing = _entryHotkeys.Values
            .Where(b => b.Key.PadButtons != 0 && (cur & b.Key.PadButtons) == b.Key.PadButtons && (_padPrev & b.Key.PadButtons) != b.Key.PadButtons)
            .ToList();
        _padPrev = cur;
        var masks = firing.Select(f => f.Key.PadButtons).ToList();
        foreach (var f in firing)
        {
            ushort m = f.Key.PadButtons;
            if (masks.Any(o => o != m && (o & m) == m)) continue; // a bigger combo containing this one fired
            RunHotkey(f.Entry, f.Key);
        }
    }

    private void RunHotkey(TableEntry e, EntryHotkey h)
    {
        if (_mem == null || e == null) return;
        long? a = e.Resolve(_mem);
        switch (h.Action)
        {
            case HotkeyAction.Toggle:
                SetFrozen(e, !e.Frozen);
                RebuildGrid();
                Notify($"{ToggleLabel(e)}: {(e.Frozen ? "ON" : "OFF")}", e.Frozen);
                break;
            case HotkeyAction.Refill:
                Refill(e);
                Notify($"{e.Name} refilled", true);
                break;
            case HotkeyAction.SetValue:
            case HotkeyAction.Add:
            case HotkeyAction.Subtract:
                if (a == null || !_mem.TryReadValue(a.Value, e.Kind, out var cur)) { Notify($"{e.Name}: not in memory right now", false); return; }
                double v = h.Action switch
                {
                    HotkeyAction.SetValue => h.Value,
                    HotkeyAction.Add => cur + h.Value,
                    _ => cur - h.Value
                };
                _mem.WriteValue(a.Value, e.Kind, v);
                if (e.Frozen) e.FreezeValue = v;
                Notify($"{e.Name} = {e.Kind.Format(v)}", true);
                break;
            case HotkeyAction.SaveSlot:
                SaveSlot(e, (int)h.Value);
                break;
            case HotkeyAction.RestoreSlot:
                RestoreSlot(e, (int)h.Value);
                break;
        }
    }

    private void EditHotkeys(TableEntry e)
    {
        if (e == null) { MessageBox.Show("Select a table row first."); return; }
        using var dlg = new HotkeyDialog(e);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        e.Hotkeys = dlg.Result;
        RebuildGrid();
        if (_mem != null) RegisterHotkeys();
        SaveProfileQuiet();
        if (e.Chain == null)
            _status.Text = $"Hotkeys set. '{e.Name}' has no pointer path yet, so they only last this session (see the Pointer paths page).";
    }

    // ================================================================= groups and slots

    private List<TableEntry> GroupOf(TableEntry e) =>
        string.IsNullOrWhiteSpace(e.Group)
            ? new List<TableEntry> { e }
            : _entries.Where(x => string.Equals(x.Group, e.Group, StringComparison.OrdinalIgnoreCase)).ToList();

    private static string SlotKey(TableEntry e, int slot) =>
        (string.IsNullOrWhiteSpace(e.Group) ? "entry:" + e.Name : e.Group.Trim()) + "#" + slot;

    private string GroupLabel(TableEntry e) => string.IsNullOrWhiteSpace(e.Group) ? e.Name : e.Group;

    /// <summary>Remembers the current values of the entry's whole group (e.g. X/Y/Z position).</summary>
    private void SaveSlot(TableEntry e, int slot)
    {
        if (_mem == null || e == null) return;
        var vals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in GroupOf(e))
            if (m.Resolve(_mem) is long a && _mem.TryReadValue(a, m.Kind, out var v)) vals[m.Name] = v;
        if (vals.Count == 0) { Notify($"{GroupLabel(e)}: nothing to save (not in memory)", false); return; }
        _slots[SlotKey(e, slot)] = vals;
        SaveProfileQuiet();
        Notify($"{GroupLabel(e)} saved to slot {slot}", true);
    }

    /// <summary>Writes back the values saved in a slot (e.g. teleport back to a saved position).</summary>
    private void RestoreSlot(TableEntry e, int slot)
    {
        if (_mem == null || e == null) return;
        if (!_slots.TryGetValue(SlotKey(e, slot), out var vals)) { Notify($"{GroupLabel(e)}: slot {slot} is empty", false); return; }
        int n = 0;
        foreach (var m in GroupOf(e))
        {
            if (!vals.TryGetValue(m.Name, out var v) || m.Resolve(_mem) is not long a) continue;
            if (_mem.WriteValue(a, m.Kind, v)) n++;
            if (m.Frozen) m.FreezeValue = v;
        }
        Notify(n > 0 ? $"{GroupLabel(e)} restored from slot {slot}" : $"{GroupLabel(e)}: couldn't restore slot {slot}", n > 0);
    }

    private ContextMenuStrip BuildGridMenu()
    {
        var menu = new ContextMenuStrip();
        var hot = new ToolStripMenuItem("Hotkeys…", null, (_, _) => EditHotkeys(SelectedEntry));
        var freezeG = new ToolStripMenuItem("Freeze group", null, (_, _) => SetGroupFrozen(true));
        var unfreezeG = new ToolStripMenuItem("Unfreeze group", null, (_, _) => SetGroupFrozen(false));
        var save = new ToolStripMenuItem("Save group values to slot");
        var restore = new ToolStripMenuItem("Restore group values from slot");
        for (int i = 1; i <= 5; i++)
        {
            int slot = i;
            save.DropDownItems.Add($"Slot {slot}", null, (_, _) => SaveSlot(SelectedEntry, slot));
            restore.DropDownItems.Add($"Slot {slot}", null, (_, _) => RestoreSlot(SelectedEntry, slot));
        }
        var view = new ToolStripMenuItem("View in memory (find neighbours)", null, (_, _) => ViewEntryInMemory(SelectedEntry));
        var copy = new ToolStripMenuItem("Copy address", null, (_, _) =>
        {
            if (_mem != null && SelectedEntry?.Resolve(_mem) is long a) Clipboard.SetText(a.ToString("X"));
        });
        menu.Items.AddRange(new ToolStripItem[] { hot, new ToolStripSeparator(), freezeG, unfreezeG, save, restore, new ToolStripSeparator(), view, copy });
        menu.Opening += (_, ev) => { if (SelectedEntry == null) ev.Cancel = true; };
        _grid.CellMouseDown += (_, ev) =>
        {
            if (ev.Button == MouseButtons.Right && ev.RowIndex >= 0 && ev.ColumnIndex >= 0)
                _grid.CurrentCell = _grid.Rows[ev.RowIndex].Cells[ev.ColumnIndex];
        };
        return menu;
    }

    private void SetGroupFrozen(bool on)
    {
        var e = SelectedEntry;
        if (e == null) return;
        foreach (var m in GroupOf(e)) SetFrozen(m, on);
        RebuildGrid();
    }

    // ================================================================= value graph

    private void ResetGraph()
    {
        var e = SelectedEntry;
        if (ReferenceEquals(e, _graphEntry)) return;
        _graphEntry = e;
        _graph.Clear();
        _graph.Title = e?.Name ?? "";
        _graph.Kind = e?.Kind ?? ValueKind.Int32;
    }

    private void GraphTick()
    {
        if (_tabs.SelectedTab != _tablePage) return;
        ResetGraph();
        var e = _graphEntry;
        if (e == null || _mem == null) return;
        _graph.Title = e.Name;
        _graph.Add(e.Resolve(_mem) is long a && _mem.TryReadValue(a, e.Kind, out var v) ? v : double.NaN);
    }

    // ================================================================= auto-attach and anti-cheat re-check

    private void TryAutoAttach()
    {
        List<string> names;
        try { names = ProfileStore.ProfiledProcessNames(); } catch { return; }
        foreach (var name in names)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == _noAutoAttachPid || p.Id == Environment.ProcessId) continue;
                    // Give the game time to load its modules, so the anti-cheat check sees everything.
                    try { if ((DateTime.Now - p.StartTime).TotalSeconds < 10) continue; } catch { }
                    if (Attach(p, quiet: true)) return;
                }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>Anti-cheat can load after the game starts; detach if it shows up mid-session.</summary>
    private bool RecheckAntiCheat()
    {
        try
        {
            _mem.RefreshModules();
            var ac = _mem.DetectAntiCheat();
            if (ac == null) return false;
            int pid = _mem.Pid;
            Detach($"Detached: anti-cheat detected ({ac}). HealthHunter only works with single-player / offline games.");
            _noAutoAttachPid = pid;
            return true;
        }
        catch { return false; }
    }

    // ================================================================= memory view

    private NavPage _memPage;
    private readonly TextBox _memAddr = new() { Width = 240, PlaceholderText = "address (hex) or game.exe+1234", Margin = new Padding(0, 5, 8, 0) };
    private readonly ModernButton _memGoBtn = new() { Text = "Go", AutoSize = true, Variant = ButtonVariant.Primary };
    private readonly ModernButton _memEntryBtn = new() { Text = "Go to selected table entry", AutoSize = true };
    private readonly ComboBox _memKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100, Margin = new Padding(0, 5, 8, 0) };
    private readonly ListView _memList = new() { View = View.Details, VirtualMode = true, FullRowSelect = true, HideSelection = false, Font = new Font("Consolas", 9.5f) };
    private readonly Label _memStatus = new() { AutoSize = false, Height = 28, ForeColor = Theme.TextDim, TextAlign = ContentAlignment.MiddleLeft };
    private const int MemBefore = 0x100, MemAfter = 0x300, MemStride = 4;
    private const int MemRows = (MemBefore + MemAfter) / MemStride;
    private long _memBase;
    private TableEntry _memOrigin;
    private byte[] _memBuf = new byte[MemBefore + MemAfter + 8];
    private byte[] _memPrevBuf;
    private bool _memValid;
    private readonly int[] _memChangedAt = new int[MemRows];

    private void BuildMemoryTab()
    {
        var page = _memPage = Page("Memory view", '\uE943', "#", "Memory view",
            "Live view around a value. Stamina, mana, ammo or X/Y/Z position usually sit right next to health.");
        foreach (var k in Enum.GetValues<ValueKind>()) _memKind.Items.Add(k);
        _memKind.SelectedItem = ValueKind.Int32;
        var card = new Card();
        var row = Ui.Row(Ui.Caption("Address"), _memAddr, _memGoBtn, _memEntryBtn, Ui.Caption("Add rows as"), _memKind);
        var help = Ui.Para("Rows that changed in the last 2 seconds turn red. Double-click a row to add it to the cheat table as the type chosen above; " +
                           "if you came from an entry with a pointer path, the new entry gets a matching path so it survives restarts too.");
        Ui.Stack(card, new Control[] { row, help }, _memList, new Control[] { _memStatus });
        Ui.Stack(page.Body, null, card);
        _memList.Columns.Add("Offset", 80);
        _memList.Columns.Add("Address", 200);
        _memList.Columns.Add("Bytes", 110);
        _memList.Columns.Add("Int32", 110);
        _memList.Columns.Add("Float", 120);
        _memList.Columns.Add("Double (8 bytes)", 150);
        _memList.Columns.Add("Byte / Int16", 110);
        _memList.RetrieveVirtualItem += MemList_RetrieveVirtualItem;
        _memList.DoubleClick += (_, _) => AddMemoryRowToTable();
        _tabs.Add(page);

        _memGoBtn.Click += (_, _) => GoToTypedAddress();
        _memAddr.KeyDown += (_, ev) => { if (ev.KeyCode == Keys.Enter) { ev.SuppressKeyPress = true; GoToTypedAddress(); } };
        _memEntryBtn.Click += (_, _) => ViewEntryInMemory(SelectedEntry);
    }

    private void GoToTypedAddress()
    {
        if (_mem == null) { MessageBox.Show("Attach to the game first."); return; }
        var a = _mem.ParseAddress(_memAddr.Text);
        if (a == null) { MessageBox.Show("Type a hex address (1A2B3C) or module+offset (game.exe+1F00)."); return; }
        _memOrigin = null;
        ShowMemoryAt(a.Value);
    }

    private void ViewEntryInMemory(TableEntry e)
    {
        if (_mem == null || e == null) { MessageBox.Show("Attach, then select an entry on the Cheat table page."); return; }
        if (e.Resolve(_mem) is not long a) { MessageBox.Show("That entry doesn't resolve to an address right now."); return; }
        _memOrigin = e;
        _memKind.SelectedItem = e.Kind;
        ShowMemoryAt(a);
        _tabs.SelectedTab = _memPage;
    }

    private void ShowMemoryAt(long a)
    {
        _memBase = a;
        _memPrevBuf = null;
        Array.Clear(_memChangedAt);
        _memAddr.Text = a.ToString("X");
        _memList.VirtualListSize = MemRows;
        RefreshMemoryView();
        if (_memList.Items.Count > 0) _memList.EnsureVisible(MemBefore / MemStride);
    }

    private void RefreshMemoryView()
    {
        if (_mem == null || _memList.VirtualListSize == 0) return;
        if (_memOrigin != null && _memOrigin.Resolve(_mem) is long moved && moved != _memBase)
        {
            _memBase = moved; // pointer path moved (e.g. after loading a save): follow it
            _memPrevBuf = null;
        }
        long start = _memBase - MemBefore;
        int got = _mem.Read(start, _memBuf, 0, _memBuf.Length);
        _memValid = got > 0;
        if (got < _memBuf.Length) Array.Clear(_memBuf, Math.Max(0, got), _memBuf.Length - Math.Max(0, got));
        if (_memPrevBuf != null)
            for (int r = 0; r < MemRows; r++)
                if (!_memBuf.AsSpan(r * MemStride, MemStride).SequenceEqual(_memPrevBuf.AsSpan(r * MemStride, MemStride)))
                    _memChangedAt[r] = _tick;
        _memPrevBuf = (byte[])_memBuf.Clone();
        var mod = _mem.ModuleAt(_memBase);
        _memStatus.Text = $"Showing {start:X} – {start + MemBefore + MemAfter:X}" +
                          (mod != null ? $"  ({mod.Name}+{_memBase - mod.Base:X})" : "") +
                          (_memOrigin != null ? $"   •   following '{_memOrigin.Name}'" : "") +
                          (got <= 0 ? "   •   memory not readable" : "");
        _memList.Invalidate();
    }

    private void MemList_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
    {
        int r = e.ItemIndex;
        int off = r * MemStride;
        long addr = _memBase - MemBefore + off;
        long rel = addr - _memBase;
        string relText = rel == 0 ? "◆ 0" : (rel < 0 ? "-" : "+") + Math.Abs(rel).ToString("X");
        if (!_memValid || off + 8 > _memBuf.Length)
        {
            e.Item = new ListViewItem(new[] { relText, addr.ToString("X"), "??", "", "", "", "" });
            return;
        }
        var b = _memBuf.AsSpan(off);
        string bytes = string.Join(" ", b[..MemStride].ToArray().Select(x => x.ToString("X2")));
        double f = ValueKind.Float.Decode(b);
        string fl = double.IsNaN(f) || double.IsInfinity(f) || Math.Abs(f) > 1e9 || (f != 0 && Math.Abs(f) < 1e-6) ? "-" : f.ToString("0.###");
        double d = ValueKind.Double.Decode(b);
        string db = double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e12 || (d != 0 && Math.Abs(d) < 1e-9) ? "-" : d.ToString("0.###");
        var item = new ListViewItem(new[]
        {
            relText, addr.ToString("X"), bytes, ValueKind.Int32.Format(ValueKind.Int32.Decode(b)), fl, db,
            $"{b[0]} / {ValueKind.Int16.Format(ValueKind.Int16.Decode(b))}"
        });
        if (_memChangedAt[r] != 0 && _tick - _memChangedAt[r] <= 10) item.ForeColor = Theme.Danger;
        if (rel == 0) { item.BackColor = Theme.AccentDim; }
        e.Item = item;
    }

    private void AddMemoryRowToTable()
    {
        if (_mem == null || _memList.SelectedIndices.Count == 0) return;
        int r = _memList.SelectedIndices[0];
        long addr = _memBase - MemBefore + (long)r * MemStride;
        long delta = addr - _memBase;
        var kind = (ValueKind)_memKind.SelectedItem;
        var entry = new TableEntry { Kind = kind, Mode = FreezeMode.FreezeCurrent };
        string sign = delta < 0 ? "-" : "+";
        if (_memOrigin?.Chain is PointerChain c)
        {
            // Same path as the origin, shifted by the distance between the two values.
            var chain = new PointerChain { Module = c.Module, ModuleOffset = c.ModuleOffset, Offsets = new List<long>(c.Offsets) };
            if (chain.Offsets.Count > 0) chain.Offsets[^1] += delta; else chain.ModuleOffset += delta;
            entry.Chain = chain;
            entry.Name = $"{_memOrigin.Name} {sign}{Math.Abs(delta):X}";
            entry.Group = _memOrigin.Group;
        }
        else
        {
            entry.RawAddress = addr;
            entry.Name = $"Value {addr:X}";
        }
        entry.MaxValue = _mem.TryReadValue(addr, kind, out var v) && v > 0 ? v : 100;
        _entries.Add(entry);
        RebuildGrid();
        _memStatus.Text = $"Added '{entry.Name}' ({kind}) to the cheat table. Rename it there.";
    }
}
