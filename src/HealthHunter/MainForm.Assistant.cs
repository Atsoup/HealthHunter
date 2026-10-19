using System.Diagnostics;
using System.Drawing;

namespace HealthHunter;

/// <summary>
/// v6: the Assistant page: one switch that attaches to the game in front, finds the HUD bars, on-screen numbers and
/// the player's position by itself, and turns the cheats on. Everything it finds lands in the same cheat table and
/// profile as the manual pages.
/// </summary>
public sealed partial class MainForm
{
    private NavPage _asPage;
    private Assistant _assistant;
    private readonly ModernButton _asToggle = new() { Text = "Turn the assistant ON", AutoSize = true, Variant = ButtonVariant.Primary, MinimumSize = new Size(220, 44) };
    private readonly Pill _asPill = new() { Text = "Off" };
    private readonly TextBlock _asStatus = new() { Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.Accent, Padding = new Padding(0, 6, 0, 6) };
    private readonly CheckBox _asFront = new() { Text = "Attach to whichever game is in front (anti-cheat games are still refused)", AutoSize = true };
    private readonly CheckBox _asApply = new() { Text = "Turn cheats on as soon as a value is found (infinite health, never-decreasing ammo and gold…)", AutoSize = true };
    private readonly CheckBox _asNumbers = new() { Text = "Find on-screen numbers too: ammo, gold, XP, stats (reads the HUD with Windows OCR)", AutoSize = true };
    private readonly CheckBox _asMovement = new() { Text = "Find the player's position from walking and jumping (hold height, teleport slots)", AutoSize = true };
    private readonly CheckBox _asVerify = new() { Text = "Confirm each bar's value by changing it once and watching the game (recommended)", AutoSize = true };
    private readonly FlowLayoutPanel _asTogglePanel = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 0, 0, 6) };
    private readonly ListView _asItems = new() { View = View.Details, FullRowSelect = true, HideSelection = false, Height = 150 };
    private readonly TextBox _asFeed = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Mono, Height = 170 };
    private readonly PictureBox _asPreview = new() { Height = 90, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Surface2 };
    private readonly Label _asBars = new() { AutoSize = true, ForeColor = Theme.TextDim };
    private List<SavedBar> _autoBars = new();
    private volatile List<TableEntry> _entriesSnapshot = new();
    private int _asFeedLines;

    private void BuildAssistantTab()
    {
        var page = _asPage = Page("Assistant", '', "»", "Assistant",
            "Hands-off mode. Switch it on, play the game, and it finds the bars, numbers and position by itself. Single-player and offline games only.");

        var top = new Card { FitContent = true };
        Ui.Stack(top, new Control[]
        {
            Ui.Row(_asToggle, _asPill),
            _asStatus,
            Ui.Para("What it does: attaches to the game in front (after the anti-cheat check), watches the game window, spots every HUD bar " +
                    "that moves (red = health, blue = mana, yellow/green = stamina…), takes one snapshot of the game's memory and runs auto-find " +
                    "for all of them at once from it, confirming each value with the write-once copy test. Then it reads the numbers on screen " +
                    "with Windows OCR and finds those, and works out your position from walking and jumping. Every find gets a pointer path and " +
                    "is saved to the profile. Play normally; it needs the bars and numbers to change a few times.")
        });

        var opts = new Card { Title = "What it may do", FitContent = true };
        foreach (var c in new Control[] { _asFront, _asApply, _asNumbers, _asMovement, _asVerify }) c.Margin = new Padding(0, 3, 0, 3);
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        col.Controls.AddRange(new Control[] { _asFront, _asApply, _asNumbers, _asMovement, _asVerify });
        Ui.Stack(opts, new Control[] { col });

        var found = new Card { Title = "Found so far", FitContent = true };
        _asItems.Columns.Add("What", 110);
        _asItems.Columns.Add("From", 230);
        _asItems.Columns.Add("State", 80);
        _asItems.Columns.Add("Detail", 520);
        Ui.Stack(found, new Control[] { _asTogglePanel, _asItems });

        var seen = new Card { Title = "What it sees", FitContent = true };
        Ui.Stack(seen, new Control[] { _asBars, _asPreview });

        var feed = new Card { Title = "Feed", FitContent = true };
        Ui.Stack(feed, new Control[] { _asFeed });

        page.Body.AutoScroll = true;
        page.Body.Padding = new Padding(0, 0, 8, 0);
        Ui.Stack(page.Body, new Control[] { top, Ui.Gap(12), found, Ui.Gap(12), opts, Ui.Gap(12), seen, Ui.Gap(12), feed, Ui.Gap(12) });
        _tabs.Add(page);

        _asFront.Checked = _settings.AssistantAttachFront;
        _asApply.Checked = _settings.AssistantApply;
        _asNumbers.Checked = _settings.AssistantNumbers;
        _asMovement.Checked = _settings.AssistantMovement;
        _asVerify.Checked = _settings.AssistantVerify;
        foreach (var c in new[] { _asFront, _asApply, _asNumbers, _asMovement, _asVerify })
            c.CheckedChanged += (_, _) => { ApplyAssistantOptions(); SaveAssistantSettings(); };
        _asToggle.Click += (_, _) => ToggleAssistant();

        _assistant = new Assistant
        {
            TryAttach = pid =>
            {
                try
                {
                    return (bool)Invoke(() =>
                    {
                        if (_mem != null) return true;
                        Process p;
                        try { p = Process.GetProcessById(pid); } catch { return false; }
                        return Attach(p, quiet: true);
                    });
                }
                catch { return false; }
            },
            Memory = () => _mem,
            Entries = () => _entriesSnapshot,
            Found = (e, item) => Post(() => OnAssistantFound(e, item)),
            Options = list => Post(() => OnAssistantOptions(list)),
            Feed = line => Post(() => AppendFeed(line)),
            Status = s => Post(() => { _asStatus.Text = s; if (_tabs.SelectedTab != _asPage) _status.Text = "Assistant: " + s; }),
            Profile = () => { try { return _mem == null ? null : ProfileStore.Load(_mem.ProcessName); } catch { return null; } },
            SaveBars = bars => Post(() => { _autoBars = bars; SaveProfileQuiet(); })
        };
        ApplyAssistantOptions();
    }

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    private void ApplyAssistantOptions()
    {
        if (_assistant == null) return;
        var o = _assistant.Opt;
        o.AttachFront = _asFront.Checked; o.Apply = _asApply.Checked; o.Numbers = _asNumbers.Checked; o.Movement = _asMovement.Checked; o.Verify = _asVerify.Checked;
        o.PointerDepth = (int)_depth.Value;
        o.PointerMaxOffset = long.TryParse(_maxOff.Text, System.Globalization.NumberStyles.HexNumber, null, out long mo) ? mo : 0x1000;
    }

    private void SaveAssistantSettings()
    {
        _settings.AssistantAttachFront = _asFront.Checked; _settings.AssistantApply = _asApply.Checked; _settings.AssistantNumbers = _asNumbers.Checked;
        _settings.AssistantMovement = _asMovement.Checked; _settings.AssistantVerify = _asVerify.Checked; _settings.AssistantOn = _assistant?.Running == true;
        _settings.Save();
    }

    private void ToggleAssistant()
    {
        if (_assistant.Running) StopAssistant();
        else StartAssistant();
        SaveAssistantSettings();
    }

    private void StartAssistant()
    {
        ApplyAssistantOptions();
        _assistant.Start();
        _asToggle.Text = "Turn the assistant OFF";
        _asToggle.Variant = ButtonVariant.Danger;
        _asPill.Text = "On"; _asPill.Dot = Theme.Success;
        _tabs.SelectedTab = _asPage;
    }

    private void StopAssistant()
    {
        _assistant.Stop();
        _assistant.WaitForFinders(2500); // finders put back their test writes first
        _asToggle.Text = "Turn the assistant ON";
        _asToggle.Variant = ButtonVariant.Primary;
        _asPill.Text = "Off"; _asPill.Dot = Theme.TextDim;
        _asStatus.Text = "Assistant off.";
    }

    private void AppendFeed(string line)
    {
        if (_asFeedLines++ > 400) { _asFeed.Clear(); _asFeedLines = 0; }
        _asFeed.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }

    private void OnAssistantFound(TableEntry e, AssistantItem item)
    {
        if (_mem == null) return;
        int at = _entries.FindIndex(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) { e.Hotkeys = _entries[at].Hotkeys; _entries[at] = e; } else _entries.Add(e);
        _entries.RemoveAll(x => x.Chain == null && x.Name.StartsWith(e.Name + " option ", StringComparison.OrdinalIgnoreCase));
        bool apply = _asApply.Checked && !string.Equals(e.Group, "Position", StringComparison.OrdinalIgnoreCase);
        if (apply)
        {
            SetFrozen(e, true);
            item.State = "on";
            Notify($"{ToggleLabel(e)}: ON", true);
        }
        else System.Media.SystemSounds.Exclamation.Play();
        RebuildGrid();
        OnStatChanged();
        SaveProfileQuiet();
        UpdateButtons();
    }

    private void OnAssistantOptions(List<TableEntry> list)
    {
        if (_mem == null || list.Count == 0) return;
        string stem = list[0].Name.Split(" option ")[0];
        _entries.RemoveAll(x => x.Chain == null && x.Name.StartsWith(stem + " option ", StringComparison.OrdinalIgnoreCase));
        _entries.AddRange(list);
        RebuildGrid();
    }

    /// <summary>Called from the UI timer: refreshes what the page shows.</summary>
    private void AssistantTick()
    {
        if (_assistant == null) return;
        if (_tabs.SelectedTab != _asPage) return;
        if (_tick % 4 != 0) return;
        var items = _assistant.Items;
        if (_asItems.Items.Count != items.Count)
        {
            _asItems.BeginUpdate();
            _asItems.Items.Clear();
            foreach (var it in items) _asItems.Items.Add(new ListViewItem(new[] { it.Name, it.Source, it.State, it.Detail ?? "" }));
            _asItems.EndUpdate();
        }
        else
        {
            for (int i = 0; i < items.Count; i++)
            {
                var row = _asItems.Items[i];
                var it = items[i];
                if (row.SubItems[2].Text != it.State) row.SubItems[2].Text = it.State;
                if (row.SubItems[3].Text != (it.Detail ?? "")) row.SubItems[3].Text = it.Detail ?? "";
            }
        }
        var bars = _assistant.Hud.Bars;
        var live = bars.Where(b => b.Live || b.FromProfile).ToList();
        _asBars.Text = _mem == null ? "Not attached." :
            $"{bars.Count} shapes tracked in the game window, {live.Count} moving bar(s)" +
            (live.Count > 0 ? ": " + string.Join(", ", live.Select(b => $"{(b.Label.Length > 0 ? b.Label : b.Hue + " bar")} {b.Fill * 100:0}%")) : ". Play normally; bars that move get found.");
        if (_tick % 8 == 0)
        {
            var frame = _assistant.LastFrame;
            if (frame != null)
            {
                using (var g = Graphics.FromImage(frame))
                using (var pen = new Pen(Color.Lime, 2))
                using (var pen2 = new Pen(Color.Yellow, 1))
                    foreach (var b in bars)
                    {
                        var r = b.FullRect;
                        g.DrawRectangle(b.Live || b.FromProfile ? pen : pen2, r);
                    }
                var old = _asPreview.Image; _asPreview.Image = frame; old?.Dispose();
            }
        }
    }
}
