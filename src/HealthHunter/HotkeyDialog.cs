using System.Globalization;

namespace HealthHunter;

/// <summary>Edits the hotkeys of one table entry: key combo and/or controller combo, plus what it does.</summary>
public sealed class HotkeyDialog : ScaledForm
{
    private readonly TableEntry _entry;
    private readonly List<EntryHotkey> _list;
    private readonly ListBox _box = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f) };
    private readonly TextBox _keyBox = new() { Width = 170, ReadOnly = true, Text = "click, then press keys", BackColor = SystemColors.Window };
    private readonly Button _padBtn = new() { Text = "Listen for controller", AutoSize = true };
    private readonly Label _padLbl = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 0), Text = "Pad: (none)" };
    private readonly ComboBox _action = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly TextBox _value = new() { Width = 80, Text = "0" };
    private readonly Button _addBtn = new() { Text = "Add", AutoSize = true };
    private readonly Button _removeBtn = new() { Text = "Remove selected", AutoSize = true };
    private readonly System.Windows.Forms.Timer _padTimer = new() { Interval = 30 };

    private uint _mods;
    private int _key;
    private ushort _pad;
    private ushort _padPeak;
    private int _padTicks;

    public HotkeyDialog(TableEntry entry)
    {
        _entry = entry;
        _list = entry.Hotkeys.Select(h => new EntryHotkey { Modifiers = h.Modifiers, Key = h.Key, PadButtons = h.PadButtons, Action = h.Action, Value = h.Value }).ToList();
        Text = $"Hotkeys for '{entry.Name}'";
        Width = 640; Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5f);
        MinimizeBox = MaximizeBox = false;

        foreach (var a in Enum.GetValues<HotkeyAction>()) _action.Items.Add(a);
        _action.SelectedItem = HotkeyAction.Toggle;
        _action.SelectedIndexChanged += (_, _) => UpdateValueHint();

        // Sizes its height to the wrapped text (a fixed height cut the last line off at larger scaling).
        var help = new TextBlock
        {
            Padding = new Padding(6),
            Text = "Keys work while the game has focus. Use at least one of Ctrl / Alt / Shift so normal typing isn't affected.\r\n" +
                   "Controller combos fire when all buttons are held. Save/Restore slot act on every entry in this entry's Group " +
                   "(Value = slot number), e.g. a Position group with X, Y, Z."
        };
        var row1 = Flow(new Label { Text = "Keys", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _keyBox, _padBtn, _padLbl);
        var row2 = Flow(new Label { Text = "Action", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _action,
                        new Label { Text = "Value", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _value, _addBtn);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bottom = Flow(_removeBtn, ok, cancel);
        row1.Dock = row2.Dock = DockStyle.Top;
        bottom.Dock = DockStyle.Bottom;
        Controls.Add(_box);
        Controls.Add(row2);
        Controls.Add(row1);
        Controls.Add(help);
        Controls.Add(bottom);
        _box.BringToFront();
        // Auto-size buttons never shrink below their 75 × 23 default size; state that floor as a design minimum
        // so it scales with the font like every other size (otherwise they look cramped at higher scaling).
        foreach (var b in new[] { _padBtn, _addBtn, _removeBtn, ok, cancel }) b.MinimumSize = new Size(75, 23);
        Theme.Apply(this);
        EnableDesignScaling(); // sizes above are 100 % values; scale them with the dialog's (body) font
        AcceptButton = ok;
        CancelButton = cancel;

        _keyBox.KeyDown += KeyBox_KeyDown;
        _keyBox.Enter += (_, _) => { if (_key == 0) _keyBox.Text = "press a combo…"; };
        _padBtn.Click += (_, _) => { _padPeak = 0; _padTicks = 0; _padLbl.Text = "Hold the buttons now…"; _padTimer.Start(); };
        _padTimer.Tick += PadTick;
        _addBtn.Click += (_, _) => AddHotkey();
        _removeBtn.Click += (_, _) => { if (_box.SelectedIndex >= 0) { _list.RemoveAt(_box.SelectedIndex); RefreshList(); } };
        FormClosed += (_, _) => _padTimer.Dispose();
        RefreshList();
        UpdateValueHint();
    }

    public List<EntryHotkey> Result => _list;

    private static FlowLayoutPanel Flow(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(4), WrapContents = true };
        f.Controls.AddRange(cs);
        return f;
    }

    private void UpdateValueHint()
    {
        var a = (HotkeyAction)_action.SelectedItem;
        _value.Enabled = a is not (HotkeyAction.Toggle or HotkeyAction.Refill);
        if (a is HotkeyAction.SaveSlot or HotkeyAction.RestoreSlot && !int.TryParse(_value.Text, out _)) _value.Text = "1";
    }

    private void KeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        var k = e.KeyCode;
        if (k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LControlKey or Keys.RControlKey
                or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu) return;
        if (k is Keys.Back or Keys.Delete && !e.Control && !e.Alt && !e.Shift) { _key = 0; _mods = 0; _keyBox.Text = "(none)"; return; }
        _mods = (e.Control ? Native.MOD_CONTROL : 0) | (e.Alt ? Native.MOD_ALT : 0) | (e.Shift ? Native.MOD_SHIFT : 0);
        _key = (int)k;
        _keyBox.Text = new EntryHotkey { Modifiers = _mods, Key = _key }.KeyText();
    }

    private void PadTick(object sender, EventArgs e)
    {
        // Record the largest combo held, finish when everything is released (or after ~5 s).
        ushort b = Gamepad.Buttons();
        if ((b | _padPeak) != _padPeak && b != 0) _padPeak |= b;
        _padTicks++;
        if ((_padPeak != 0 && b == 0) || _padTicks > 170)
        {
            _padTimer.Stop();
            _pad = _padPeak;
            _padLbl.Text = _pad == 0 ? "No controller input seen (is one connected?)" : "Pad: " + Gamepad.Describe(_pad);
        }
        else if (_padPeak != 0) _padLbl.Text = "Pad: " + Gamepad.Describe(_padPeak) + "  (release to finish)";
    }

    private void AddHotkey()
    {
        if (_key == 0 && _pad == 0) { MessageBox.Show(this, "Press a key combo in the Keys box, or record a controller combo first."); return; }
        var a = (HotkeyAction)_action.SelectedItem;
        double v = 0;
        if (_value.Enabled && !double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v) &&
            !double.TryParse(_value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
        { MessageBox.Show(this, "Value must be a number."); return; }
        if (a is HotkeyAction.SaveSlot or HotkeyAction.RestoreSlot) v = Math.Clamp(Math.Round(v), 1, 9);
        _list.Add(new EntryHotkey { Modifiers = _mods, Key = _key, PadButtons = _pad, Action = a, Value = v });
        _key = 0; _mods = 0; _pad = 0;
        _keyBox.Text = "(none)";
        _padLbl.Text = "Pad: (none)";
        RefreshList();
    }

    private void RefreshList()
    {
        _box.Items.Clear();
        foreach (var h in _list) _box.Items.Add($"{h.KeyText()}  →  {h.ActionText(_entry.Kind)}");
    }
}
